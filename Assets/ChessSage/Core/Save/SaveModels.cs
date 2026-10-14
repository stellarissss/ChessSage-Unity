using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Save
{
    /// <summary>存档元信息（供槽位列表快速展示：时间戳 / 道 / 关卡 / 业力 / 游玩时长）。</summary>
    public sealed class SaveMetadata
    {
        /// <summary>槽位号；自动存档为 -1。</summary>
        public int Slot { get; set; }

        /// <summary>槽位显示名。</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>保存时间（ISO-8601）。</summary>
        public string SavedAt { get; set; } = string.Empty;

        /// <summary>当前所在道（realm）。</summary>
        public string CurrentRealm { get; set; } = string.Empty;

        /// <summary>当前关卡序号。</summary>
        public int CurrentLevel { get; set; }

        /// <summary>存档时的业力值。</summary>
        public int Karma { get; set; }

        /// <summary>累计游玩时长（秒）。</summary>
        public double PlaytimeSeconds { get; set; }

        /// <summary>是否为自动存档。</summary>
        public bool Autosave { get; set; }

        /// <summary>棋种 id（如 xiangqi）。</summary>
        public string Variant { get; set; } = string.Empty;

        /// <summary>写入时的 schema 版本。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>槽位损坏标记（仅由列表接口在读取失败时置真，不写入文件）。</summary>
        public bool Corrupt { get; set; }

        /// <summary>损坏原因（仅列表接口使用）。</summary>
        public string CorruptReason { get; set; } = string.Empty;

        public JObject ToJObject()
        {
            return new JObject
            {
                ["slot"] = Slot,
                ["label"] = Label ?? string.Empty,
                ["saved_at"] = SavedAt ?? string.Empty,
                ["current_realm"] = CurrentRealm ?? string.Empty,
                ["current_level"] = CurrentLevel,
                ["karma"] = Karma,
                ["playtime_seconds"] = PlaytimeSeconds,
                ["autosave"] = Autosave,
                ["variant"] = Variant ?? string.Empty,
                ["schema_version"] = SchemaVersion,
            };
        }

        /// <summary>从 meta 对象读取；缺失字段用缺省值（旧档兼容）。</summary>
        public static SaveMetadata FromJObject(JObject o, int fallbackSlot)
        {
            var m = new SaveMetadata { Slot = fallbackSlot };
            if (o == null) return m;
            m.Slot = ReadInt(o, "slot", fallbackSlot);
            m.Label = ReadString(o, "label", string.Empty);
            m.SavedAt = ReadString(o, "saved_at", string.Empty);
            m.CurrentRealm = ReadString(o, "current_realm", string.Empty);
            m.CurrentLevel = ReadInt(o, "current_level", 0);
            m.Karma = ReadInt(o, "karma", 0);
            m.PlaytimeSeconds = ReadDouble(o, "playtime_seconds", 0.0);
            m.Autosave = ReadBool(o, "autosave", false);
            m.Variant = ReadString(o, "variant", string.Empty);
            m.SchemaVersion = ReadInt(o, "schema_version", 0);
            return m;
        }

        internal static int ReadInt(JObject o, string key, int fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<int>(); }
            catch (Exception)
            {
                return int.TryParse(t.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            }
        }

        internal static double ReadDouble(JObject o, string key, double fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<double>(); }
            catch (Exception)
            {
                return double.TryParse(t.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            }
        }

        internal static bool ReadBool(JObject o, string key, bool fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            return bool.TryParse(t.ToString(), out var v) ? v : fallback;
        }

        internal static string ReadString(JObject o, string key, string fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Type == JTokenType.String ? t.Value<string>() : t.ToString();
        }
    }

    /// <summary>存档写入结果。</summary>
    public sealed class SaveResult
    {
        public bool Success { get; set; }
        public int Slot { get; set; }
        public SaveMetadata Metadata { get; set; }
    }

    /// <summary>一次成功读取的结果：元信息 + 完整 payload 快照 + 迁移信息。</summary>
    public sealed class LoadedSave
    {
        public SaveMetadata Metadata { get; set; }

        /// <summary>完整的 Samsara 持久化状态（state.py get_full_state 等价物）。</summary>
        public JObject Samsara { get; set; }

        /// <summary>对局进度（可为空对象）。</summary>
        public JObject GameProgress { get; set; }

        /// <summary>读取时是否发生了版本迁移。</summary>
        public bool Migrated { get; set; }

        /// <summary>文件中记录的原始版本。</summary>
        public int FromVersion { get; set; }

        /// <summary>当前 schema 版本。</summary>
        public int SchemaVersion { get; set; }
    }
}
