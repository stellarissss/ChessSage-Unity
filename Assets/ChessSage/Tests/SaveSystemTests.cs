using System;
using System.Text;
using ChessSage.Core.Save;
using ChessSage.Core.Samsara;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// T6 存档系统测试（纯 Core，零引擎依赖，dotnet / EditMode 双跑）。
    ///
    /// 覆盖：序列化往返、信封结构（版本+校验和）、篡改/损坏显式报错、多槽位与元信息、
    /// 自动存档、v1→v2 迁移、以及对 SamsaraEngine 的适配。
    ///
    /// 【零副作用约定】全部走 <see cref="InMemorySaveStorage"/>，绝不写真实磁盘目录；
    /// 仅适配层用例借道既有 <c>SamsaraFixture</c> 的临时目录（与其它 Samsara 测试一致）。
    /// 期望值独立推导：校验和在测试内用标准库 SHA-256 重算，不调用 <see cref="SaveChecksum"/> 求期望。
    /// </summary>
    public sealed class SaveSystemTests
    {
        // ── 工具 ────────────────────────────────────────────────────────

        /// <summary>测试侧独立实现的 SHA-256（不使用被测的 SaveChecksum）。</summary>
        static string Sha256Hex(string text)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return "sha256:" + sb.ToString();
        }

        static JObject SampleSamsara(int karma, int level, string realm)
        {
            var detections = new JObject();
            foreach (var r in new[] { "hell", "hungry", "animal", "human", "asura", "heaven" })
                detections[r] = 0.0;
            return new JObject
            {
                ["version"] = 4,
                ["current_realm"] = realm,
                ["current_level"] = level,
                ["level_karma"] = karma,
                ["karma_max"] = 120,
                ["skills"] = new JObject
                {
                    ["stealth_t1"] = new JObject { ["unlocked_at"] = "2026-01-01T00:00:00", ["tier"] = 1 },
                },
                ["realm_detections"] = detections,
                ["bosses_defeated"] = new JArray(),
                ["playthrough_count"] = 1,
            };
        }

        // ── 序列化往返 ──────────────────────────────────────────────────

        [Test]
        public void SaveAndLoad_RoundTrip_PreservesSamsaraState()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            var state = SampleSamsara(karma: 42, level: 3, realm: "hell");

            var res = mgr.Save(0, state, variant: "xiangqi", playtimeSeconds: 123.5, label: "第一章");

            Assert.IsTrue(res.Success, "写入应成功");
            Assert.IsTrue(storage.Exists(SaveManager.SlotKey(0)), "槽位 0 应已写入");

            var loaded = mgr.Load(0);
            Assert.IsTrue(JToken.DeepEquals(state, loaded.Samsara), "Samsara 状态应无损往返");
            Assert.IsFalse(loaded.Migrated, "当前版本存档不应触发迁移");
            Assert.AreEqual(2, loaded.SchemaVersion, "当前 schema 版本应为 2");
        }

        [Test]
        public void SaveMetadata_DerivedFromState_AndPlaytimePersisted()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(1, SampleSamsara(karma: 88, level: 5, realm: "asura"),
                variant: "weiqi", playtimeSeconds: 3600.25);

            var meta = mgr.Load(1).Metadata;
            Assert.AreEqual(1, meta.Slot);
            Assert.AreEqual("asura", meta.CurrentRealm, "道应取自 state.current_realm");
            Assert.AreEqual(5, meta.CurrentLevel, "关卡应取自 state.current_level");
            Assert.AreEqual(88, meta.Karma, "业力应取自 state.level_karma");
            Assert.AreEqual(3600.25, meta.PlaytimeSeconds, 1e-9, "游玩时长应原样保存");
            Assert.AreEqual("weiqi", meta.Variant, "棋种应原样保存");
            Assert.IsFalse(meta.Autosave);
        }

        // ── 信封结构 + 校验和 ───────────────────────────────────────────

        [Test]
        public void SaveEnvelope_HasFormatVersionAndPayloadChecksum()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(0, SampleSamsara(karma: 10, level: 1, realm: "hell"));

            var doc = JObject.Parse(storage.Read(SaveManager.SlotKey(0)));
            Assert.AreEqual("chesssage.save", (string)doc["format"], "应写入格式标识");
            Assert.AreEqual(2, (int)doc["schema_version"], "schema_version 应为 2");
            Assert.IsNotNull(doc["meta"], "应含 meta");
            Assert.IsNotNull(doc["payload"], "应含 payload");

            var payload = doc["payload"];
            var checksum = (string)doc["checksum"];
            Assert.AreEqual(Sha256Hex(payload.ToString(Formatting.None)), checksum,
                "校验和应等于 payload 紧凑 JSON 的 SHA-256（测试侧独立重算）");
        }

        [Test]
        public void Checksum_IgnoresMetaChanges_ButDetectsPayloadChanges()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(0, SampleSamsara(karma: 10, level: 1, realm: "hell"), label: "原名");

            // 仅改元信息：不得判为篡改（校验和只覆盖 payload）。
            var doc = JObject.Parse(storage.Read(SaveManager.SlotKey(0)));
            doc["meta"]["label"] = "改过的名字";
            storage.Write(SaveManager.SlotKey(0), doc.ToString(Formatting.Indented));

            var loaded = mgr.Load(0);
            Assert.AreEqual("改过的名字", loaded.Metadata.Label, "元信息改动应可读写");

            // 改 payload：必须判为篡改。
            var doc2 = JObject.Parse(storage.Read(SaveManager.SlotKey(0)));
            doc2["payload"]["samsara"]["level_karma"] = 9999;
            storage.Write(SaveManager.SlotKey(0), doc2.ToString(Formatting.Indented));

            Assert.Throws<SaveCorruptedException>(() => mgr.Load(0), "payload 被改必须显式报错");
        }

        // ── 损坏 / 篡改 / 缺档 显式报错 ─────────────────────────────────

        [Test]
        public void Load_TamperedPayload_ThrowsCorrupted()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(0, SampleSamsara(karma: 42, level: 3, realm: "hell"));

            var doc = JObject.Parse(storage.Read(SaveManager.SlotKey(0)));
            doc["payload"]["samsara"]["level_karma"] = 123456;
            storage.Write(SaveManager.SlotKey(0), doc.ToString(Formatting.Indented));

            var ex = Assert.Throws<SaveCorruptedException>(() => mgr.Load(0));
            StringAssert.Contains("校验和", ex.Message, "应明确指出校验和不匹配");
        }

        [Test]
        public void Load_NotJson_ThrowsCorrupted()
        {
            var storage = new InMemorySaveStorage();
            storage.Write(SaveManager.SlotKey(0), "{ 这不是合法 JSON");
            var mgr = new SaveManager(storage);
            Assert.Throws<SaveCorruptedException>(() => mgr.Load(0));
        }

        [Test]
        public void Load_MissingChecksum_ThrowsCorrupted()
        {
            var storage = new InMemorySaveStorage();
            var doc = new JObject
            {
                ["format"] = "chesssage.save",
                ["schema_version"] = 2,
                ["payload"] = new JObject { ["samsara"] = new JObject(), ["game"] = new JObject() },
            };
            storage.Write(SaveManager.SlotKey(0), doc.ToString(Formatting.Indented));

            var mgr = new SaveManager(storage);
            var ex = Assert.Throws<SaveCorruptedException>(() => mgr.Load(0));
            StringAssert.Contains("校验和", ex.Message);
        }

        [Test]
        public void Load_UnknownFormatTag_ThrowsCorrupted()
        {
            var storage = new InMemorySaveStorage();
            storage.Write(SaveManager.SlotKey(0), "{\"schema_version\":2,\"payload\":{}}");
            var mgr = new SaveManager(storage);
            Assert.Throws<SaveCorruptedException>(() => mgr.Load(0));
        }

        [Test]
        public void Load_MissingSlot_ThrowsNotFound_AndTryLoadReturnsFalse()
        {
            var mgr = new SaveManager(new InMemorySaveStorage());
            Assert.Throws<SaveNotFoundException>(() => mgr.Load(2), "空槽应报 SaveNotFoundException");
            Assert.IsFalse(mgr.TryLoad(2, out var save, out var error), "TryLoad 空槽应返回 false");
            Assert.IsNull(save);
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void TryLoad_CorruptSave_ReturnsFalseWithReason()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(0, SampleSamsara(1, 1, "hell"));
            var doc = JObject.Parse(storage.Read(SaveManager.SlotKey(0)));
            doc["payload"]["samsara"]["level_karma"] = 7;
            storage.Write(SaveManager.SlotKey(0), doc.ToString(Formatting.Indented));

            Assert.IsFalse(mgr.TryLoad(0, out _, out var error), "损坏档 TryLoad 应返回 false");
            Assert.IsNotEmpty(error, "应回传失败原因");
        }

        // ── 版本迁移 ────────────────────────────────────────────────────

        [Test]
        public void Migrate_V1Save_UpgradesWithDefaultsAndLoadsOldSave()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);

            // 构造一份真实的 v1 存档：payload 无 game；meta 用旧字段 level，且无 playtime_seconds。
            var payload = new JObject
            {
                ["samsara"] = new JObject
                {
                    ["version"] = 3,
                    ["current_realm"] = "hungry",
                    ["current_level"] = 7,
                    ["level_karma"] = 55,
                    ["karma_max"] = 120,
                },
            };
            var v1 = new JObject
            {
                ["format"] = "chesssage.save",
                ["schema_version"] = 1,
                ["checksum"] = Sha256Hex(payload.ToString(Formatting.None)), // 测试侧独立重算
                ["saved_at"] = "2026-01-01T00:00:00",
                ["meta"] = new JObject
                {
                    ["slot"] = 0,
                    ["label"] = "旧档",
                    ["level"] = 7,           // 旧字段
                    ["karma"] = 55,
                    ["current_realm"] = "hungry",
                },
                ["payload"] = payload,
            };
            storage.Write(SaveManager.SlotKey(0), v1.ToString(Formatting.Indented));

            var loaded = mgr.Load(0);

            Assert.IsTrue(loaded.Migrated, "v1 档应被迁移");
            Assert.AreEqual(1, loaded.FromVersion, "原始版本应为 1");
            Assert.AreEqual(2, loaded.SchemaVersion, "迁移后应为 2");

            Assert.AreEqual(0.0, loaded.Metadata.PlaytimeSeconds, 1e-9, "新增 playtime_seconds 默认 0");
            Assert.AreEqual(7, loaded.Metadata.CurrentLevel, "旧 level→current_level 映射保留关卡");
            Assert.AreEqual(55, loaded.Metadata.Karma);
            Assert.AreEqual("hungry", loaded.Metadata.CurrentRealm);

            Assert.IsNotNull(loaded.GameProgress, "payload.game 应被补齐");
            Assert.AreEqual(0, loaded.GameProgress.Count, "payload.game 默认空对象");
            Assert.AreEqual(55, (int)loaded.Samsara["level_karma"], "samsara 数据原样保留");
            Assert.AreEqual(7, (int)loaded.Samsara["current_level"], "samsara 关卡原样保留");
        }

        [Test]
        public void Migrate_AlreadyCurrent_IsNoOp()
        {
            var doc = new JObject { ["schema_version"] = SaveMigrator.CurrentSchemaVersion, ["meta"] = new JObject() };
            SaveMigrator.Migrate(doc, out var from);
            Assert.AreEqual(2, from, "已是最新版本，fromVersion 即为 2");
            Assert.AreEqual(2, (int)doc["schema_version"]);
            Assert.IsNull(doc["meta"]["playtime_seconds"], "已最新不应再注入默认字段");
        }

        [Test]
        public void Load_FutureVersion_ThrowsVersionException()
        {
            var storage = new InMemorySaveStorage();
            var payload = new JObject { ["samsara"] = new JObject(), ["game"] = new JObject() };
            var doc = new JObject
            {
                ["format"] = "chesssage.save",
                ["schema_version"] = 99,
                ["checksum"] = Sha256Hex(payload.ToString(Formatting.None)),
                ["meta"] = new JObject(),
                ["payload"] = payload,
            };
            storage.Write(SaveManager.SlotKey(0), doc.ToString(Formatting.Indented));

            var mgr = new SaveManager(storage);
            var ex = Assert.Throws<SaveVersionException>(() => mgr.Load(0));
            Assert.AreEqual(99, ex.SaveVersion);
            Assert.AreEqual(2, ex.SupportedVersion);
        }

        // ── 多槽位 / 元信息 / 列表 ──────────────────────────────────────

        [Test]
        public void Slots_AreIndependent_ListedAndOverwritable()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            Assert.AreEqual(3, SaveManager.SlotCount, "槽位数至少为 3");

            mgr.Save(0, SampleSamsara(karma: 10, level: 1, realm: "hell"));
            mgr.Save(1, SampleSamsara(karma: 20, level: 2, realm: "hungry"));
            mgr.Save(2, SampleSamsara(karma: 30, level: 3, realm: "animal"));

            var list = mgr.ListSlots();
            Assert.AreEqual(3, list.Length);
            Assert.AreEqual(10, list[0].Karma);
            Assert.AreEqual(20, list[1].Karma);
            Assert.AreEqual(30, list[2].Karma);
            Assert.AreEqual("animal", list[2].CurrentRealm);

            // 覆盖槽位 1 不影响其它槽位。
            mgr.Save(1, SampleSamsara(karma: 99, level: 9, realm: "heaven"));
            Assert.AreEqual(99, mgr.Load(1).Metadata.Karma, "覆盖后应读回新值");
            Assert.AreEqual(10, mgr.Load(0).Metadata.Karma, "其它槽位不受影响");
            Assert.AreEqual(30, mgr.Load(2).Metadata.Karma, "其它槽位不受影响");
        }

        [Test]
        public void DeleteSlot_RemovesOnlyThatSlot()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(0, SampleSamsara(1, 1, "hell"));
            mgr.Save(1, SampleSamsara(2, 2, "hungry"));

            mgr.Delete(0);
            Assert.IsFalse(mgr.HasSlot(0), "槽位 0 应已删除");
            Assert.IsTrue(mgr.HasSlot(1), "槽位 1 应保留");
            Assert.IsNull(mgr.Peek(0), "空槽 Peek 返回 null");
            Assert.AreEqual(2, mgr.Peek(1).Karma);
        }

        [Test]
        public void ListSlots_MarksCorruptSlot_WithoutAffectingOthers()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(0, SampleSamsara(karma: 10, level: 1, realm: "hell"));
            mgr.Save(2, SampleSamsara(karma: 30, level: 3, realm: "animal"));

            // 把槽位 1 写成损坏内容。
            storage.Write(SaveManager.SlotKey(1), "{ 坏档");

            var list = mgr.ListSlots();
            Assert.IsFalse(list[0].Corrupt, "槽位 0 应完好");
            Assert.IsNotNull(list[1], "损坏槽仍应返回占位元信息");
            Assert.IsTrue(list[1].Corrupt, "损坏槽必须被明确标记，绝不静默当作好档");
            Assert.IsNotEmpty(list[1].CorruptReason, "损坏槽应给出原因");
            Assert.AreEqual(30, list[2].Karma, "完好槽位不受坏档影响");
        }

        [Test]
        public void Save_InvalidSlot_Throws()
        {
            var mgr = new SaveManager(new InMemorySaveStorage());
            Assert.Throws<ArgumentOutOfRangeException>(() => mgr.Save(-1, SampleSamsara(1, 1, "hell")));
            Assert.Throws<ArgumentOutOfRangeException>(() => mgr.Save(SaveManager.SlotCount, SampleSamsara(1, 1, "hell")));
        }

        // ── 自动存档 ────────────────────────────────────────────────────

        [Test]
        public void Autosave_UsesDedicatedKey_AndDoesNotConsumeSlots()
        {
            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Autosave(SampleSamsara(karma: 77, level: 4, realm: "human"), variant: "xiangqi");

            Assert.IsTrue(storage.Exists("autosave"), "自动存档应写入独立键");
            Assert.IsTrue(mgr.HasAutosave());
            foreach (var meta in mgr.ListSlots()) Assert.IsNull(meta, "自动存档不占用常规槽位");

            var loaded = mgr.LoadAutosave();
            Assert.IsTrue(loaded.Metadata.Autosave, "应标记为自动存档");
            Assert.AreEqual(-1, loaded.Metadata.Slot, "自动存档槽号为 -1");
            Assert.AreEqual(77, loaded.Metadata.Karma);
            Assert.AreEqual(4, loaded.Metadata.CurrentLevel);
        }

        // ── 与 SamsaraEngine 适配（经既有临时目录夹具，零真实配置改动）────

        [Test]
        public void SamsaraAdapter_CaptureRestore_RoundTripsThroughStorage()
        {
            using var source = new SamsaraFixture();
            source.Engine.IncreaseKarma(30);                     // 50 → 80
            Assert.AreEqual(80, source.Engine.GetKarma(), "前置：源引擎业力应为 80");

            var storage = new InMemorySaveStorage();
            var mgr = new SaveManager(storage);
            mgr.Save(0, SamsaraSaveAdapter.Capture(source.Engine), variant: "xiangqi");

            var loaded = mgr.Load(0);
            using var target = new SamsaraFixture();
            SamsaraSaveAdapter.Restore(target.Engine, loaded.Samsara);

            Assert.AreEqual(source.Engine.GetKarma(), target.Engine.GetKarma(),
                "业力应经存档往返后一致");
            Assert.AreEqual(source.Engine.GetCurrentLevel(), target.Engine.GetCurrentLevel(),
                "关卡应经存档往返后一致");
        }
    }
}
