using System;
using System.Globalization;
using System.IO;
using ChessSage.Core.Game;
using ChessSage.Core.Samsara;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Save
{
    /// <summary>
    /// 存档管理器（纯 Core，零引擎依赖）。
    ///
    /// 职责：
    ///   · 序列化：把 Samsara 完整状态（+ 可选对局进度）写入带版本号/校验和的信封；
    ///   · 版本迁移：读取时经 <see cref="SaveMigrator"/> 把旧档平移到当前 schema；
    ///   · 完整性校验：SHA-256 校验和，损坏/被篡改的档**显式抛错**（绝不静默加载）；
    ///   · 多槽位：<see cref="SlotCount"/> 个常规槽 + 独立「自动存档」键；
    ///   · 自动存档：<see cref="Autosave"/>（触发点由调用方决定，本类只提供能力）。
    ///
    /// 文件读写全部经 <see cref="ISaveStorage"/> 抽象：发布用 <see cref="FileSaveStorage"/>，
    /// 测试用 <see cref="InMemorySaveStorage"/>（零副作用、可重复跑）。
    ///
    /// 【与 SamsaraEngine 的关系】Bootstrap 目前以 savePath=null 构造引擎（不落盘），
    /// 本类**不改变**该行为；接入方式见 <see cref="SamsaraSaveAdapter"/>。
    /// </summary>
    public sealed class SaveManager
    {
        /// <summary>常规槽位数量（>=3）。</summary>
        public const int SlotCount = 3;

        /// <summary>信封格式标识，用于识别非法/无关文件。</summary>
        public const string FormatTag = "chesssage.save";

        const string SlotPrefix = "slot_";
        const string AutosaveKey = "autosave";

        readonly ISaveStorage _storage;

        public SaveManager(ISaveStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public ISaveStorage Storage => _storage;

        public static string SlotKey(int slot) => SlotPrefix + slot.ToString(CultureInfo.InvariantCulture);

        public bool HasSlot(int slot) => IsValidSlot(slot) && _storage.Exists(SlotKey(slot));
        public bool HasAutosave() => _storage.Exists(AutosaveKey);

        static bool IsValidSlot(int slot) => slot >= 0 && slot < SlotCount;

        // ══════════════════════════════════════════════════════════
        // 写入
        // ══════════════════════════════════════════════════════════

        /// <summary>写入指定槽位（0..SlotCount-1）。</summary>
        public SaveResult Save(
            int slot, JObject samsaraState, JObject gameProgress = null,
            string variant = null, double playtimeSeconds = 0.0, string label = null, bool autosave = false)
        {
            if (!IsValidSlot(slot))
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"槽位必须在 0..{SlotCount - 1} 之间");
            return Write(SlotKey(slot), slot, samsaraState, gameProgress, variant, playtimeSeconds, label, autosave);
        }

        /// <summary>关键时刻自动存档（写入独立键，不占用常规槽位）。</summary>
        public SaveResult Autosave(
            JObject samsaraState, JObject gameProgress = null,
            string variant = null, double playtimeSeconds = 0.0)
        {
            return Write(AutosaveKey, -1, samsaraState, gameProgress, variant, playtimeSeconds, "自动存档", true);
        }

        SaveResult Write(
            string key, int slot, JObject samsaraState, JObject gameProgress,
            string variant, double playtimeSeconds, string label, bool autosave)
        {
            var meta = BuildMeta(slot, samsaraState, variant, playtimeSeconds, label, autosave);
            var payload = new JObject
            {
                ["samsara"] = samsaraState?.DeepClone() ?? new JObject(),
                ["game"] = gameProgress?.DeepClone() ?? new JObject(),
            };

            var document = new JObject
            {
                ["format"] = FormatTag,
                ["schema_version"] = SaveMigrator.CurrentSchemaVersion,
                ["checksum"] = SaveChecksum.Compute(payload),
                ["saved_at"] = meta.SavedAt,
                ["meta"] = meta.ToJObject(),
                ["payload"] = payload,
            };

            _storage.Write(key, document.ToString(Formatting.Indented));
            return new SaveResult { Success = true, Slot = slot, Metadata = meta };
        }

        /// <summary>从完整 Samsara 状态派生元信息（业力取 level_karma）。</summary>
        static SaveMetadata BuildMeta(
            int slot, JObject samsara, string variant, double playtimeSeconds, string label, bool autosave)
        {
            return new SaveMetadata
            {
                Slot = slot,
                Label = string.IsNullOrEmpty(label) ? (autosave ? "自动存档" : $"存档 {slot + 1}") : label,
                SavedAt = NowIso(),
                CurrentRealm = SaveMetadata.ReadString(samsara, "current_realm", string.Empty),
                CurrentLevel = SaveMetadata.ReadInt(samsara, "current_level", 0),
                Karma = SaveMetadata.ReadInt(samsara, "level_karma", 0),
                PlaytimeSeconds = playtimeSeconds,
                Autosave = autosave,
                Variant = variant ?? string.Empty,
                SchemaVersion = SaveMigrator.CurrentSchemaVersion,
            };
        }

        // ══════════════════════════════════════════════════════════
        // 读取
        // ══════════════════════════════════════════════════════════

        public LoadedSave Load(int slot)
        {
            if (!IsValidSlot(slot))
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"槽位必须在 0..{SlotCount - 1} 之间");
            return LoadKey(SlotKey(slot), slot);
        }

        public LoadedSave LoadAutosave() => LoadKey(AutosaveKey, -1);

        /// <summary>非抛出式读取：损坏/缺失/版本过高时返回 false 并回传错误原因。</summary>
        public bool TryLoad(int slot, out LoadedSave save, out string error)
        {
            save = null;
            error = null;
            try
            {
                save = Load(slot);
                return true;
            }
            catch (SaveException e)
            {
                error = e.Message;
                return false;
            }
        }

        LoadedSave LoadKey(string key, int slot)
        {
            if (!_storage.Exists(key))
                throw new SaveNotFoundException($"存档不存在: {key}");

            var document = ParseDocument(_storage.Read(key));

            var payload = document["payload"] as JObject;
            if (payload == null)
                throw new SaveCorruptedException("存档缺少 payload 字段");

            // 1) 校验和必须**先于**迁移验证（迁移会改写 payload）。
            var checksum = SaveMetadata.ReadString(document, "checksum", string.Empty);
            if (string.IsNullOrEmpty(checksum))
                throw new SaveCorruptedException("存档缺少校验和（checksum）字段");
            if (!SaveChecksum.Verify(checksum, payload))
                throw new SaveCorruptedException("存档校验和不匹配：文件已损坏或被篡改");

            // 2) 版本检查与迁移。
            var fromVersion = SaveMigrator.ReadVersion(document);
            if (fromVersion > SaveMigrator.CurrentSchemaVersion)
                throw new SaveVersionException(fromVersion, SaveMigrator.CurrentSchemaVersion);
            var migrated = fromVersion < SaveMigrator.CurrentSchemaVersion;
            if (migrated) SaveMigrator.Migrate(document, out _);

            // 3) 提取（迁移后重新取值，可能已补默认字段）。
            payload = document["payload"] as JObject ?? new JObject();
            var meta = SaveMetadata.FromJObject(document["meta"] as JObject, slot);

            return new LoadedSave
            {
                Metadata = meta,
                Samsara = payload["samsara"] as JObject ?? new JObject(),
                GameProgress = payload["game"] as JObject ?? new JObject(),
                Migrated = migrated,
                FromVersion = fromVersion,
                SchemaVersion = SaveMigrator.CurrentSchemaVersion,
            };
        }

        JObject ParseDocument(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new SaveCorruptedException("存档内容为空");
            JObject document;
            try
            {
                // 必须禁用日期自动解析：否则 "2026-01-01T00:00:00" 之类字符串会被 Newtonsoft
                // 强转为 DateTime，既破坏「状态无损往返」的不变性，也会让校验和/序列化结果漂移
                // （与本工程 JsonIo.Settings 的 DateParseHandling.None 约定一致）。
                using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
                document = JObject.Load(reader);
            }
            catch (SaveException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new SaveCorruptedException("存档不是合法 JSON：" + e.Message, e);
            }
            var format = SaveMetadata.ReadString(document, "format", string.Empty);
            if (!string.Equals(format, FormatTag, StringComparison.Ordinal))
                throw new SaveCorruptedException($"存档格式标识非法（期望 {FormatTag}，实际 '{format}'）");
            return document;
        }

        /// <summary>读取元信息（不返回完整状态）；槽位为空返回 null，损坏则抛错。</summary>
        public SaveMetadata Peek(int slot)
        {
            if (!IsValidSlot(slot))
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"槽位必须在 0..{SlotCount - 1} 之间");
            if (!_storage.Exists(SlotKey(slot))) return null;
            return Load(slot).Metadata;
        }

        /// <summary>
        /// 列出全部常规槽位的元信息（长度恒为 <see cref="SlotCount"/>；空槽为 null）。
        /// 单个槽位损坏不会影响其余槽位——损坏槽以 <see cref="SaveMetadata.Corrupt"/>=true 标记。
        /// </summary>
        public SaveMetadata[] ListSlots()
        {
            var result = new SaveMetadata[SlotCount];
            for (var i = 0; i < SlotCount; i++)
            {
                if (!_storage.Exists(SlotKey(i))) { result[i] = null; continue; }
                try
                {
                    result[i] = Load(i).Metadata;
                }
                catch (SaveException e)
                {
                    result[i] = new SaveMetadata { Slot = i, Corrupt = true, CorruptReason = e.Message };
                }
            }
            return result;
        }

        /// <summary>删除指定槽位。</summary>
        public void Delete(int slot)
        {
            if (!IsValidSlot(slot))
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"槽位必须在 0..{SlotCount - 1} 之间");
            _storage.Delete(SlotKey(slot));
        }

        /// <summary>删除自动存档。</summary>
        public void DeleteAutosave() => _storage.Delete(AutosaveKey);

        static string NowIso()
            => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把 <see cref="SamsaraEngine"/> 与存档系统接起来的薄适配层。
    ///
    /// 【与 Bootstrap 的约定】Bootstrap 现以 savePath=null 构造 SamsaraEngine（不落盘）。
    /// 若要启用真实存档，应**由调用方**：
    ///   1) 用 <see cref="Capture"/> 取快照交给 <see cref="SaveManager.Save"/>；
    ///   2) 用 <see cref="Restore"/> 把读出的快照写回引擎。
    /// 本类不改变 Bootstrap 的既有行为，避免影响现有测试与试玩录制的确定性。
    /// </summary>
    public static class SamsaraSaveAdapter
    {
        /// <summary>取 Samsara 完整持久化状态快照。</summary>
        public static JObject Capture(SamsaraEngine engine)
            => engine?.GetFullState() ?? new JObject();

        /// <summary>把快照写回引擎（按顶层键合并，行为等价于读档后再触发默认化）。</summary>
        public static void Restore(SamsaraEngine engine, JObject state)
        {
            if (engine == null || state == null) return;
            engine.State.Update(state);
        }
    }

    /// <summary>对局进度快照采集（可选：仅在对局进行中才需要保存的进度）。</summary>
    public static class GameProgressCapture
    {
        public static JObject FromSession(GameSession session)
        {
            var progress = new JObject();
            if (session == null) return progress;
            progress["variant"] = session.Config?.Variant ?? string.Empty;
            progress["side_scheme"] = session.Config?.SideScheme ?? string.Empty;
            progress["board_state"] = session.State?.ToJson() ?? new JObject();
            progress["move_count"] = session.State?.MoveHistory?.Count ?? 0;
            progress["is_ended"] = session.IsEnded;
            return progress;
        }
    }
}
