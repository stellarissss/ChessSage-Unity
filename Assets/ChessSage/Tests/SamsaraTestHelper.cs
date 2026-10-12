using System;
using System.Collections.Generic;
using System.IO;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// M5-B：Samsara 黄金测试夹具。
    ///
    /// 每个用例构造一个**隔离的** SamsaraEngine：把 seed 状态写入临时目录下的
    /// state.json 并让引擎加载它，从而绝不影响仓库内 Assets/StreamingAssets 下的
    /// 真源配置；用例结束后删除临时目录。
    ///
    /// 期望值来自 tools/verify/gen-samsara-fixtures.py（legacy Python 原版引擎导出），
    /// 存放于 Assets/ChessSage/Tests/Fixtures/samsara_*.json。
    /// </summary>
    internal sealed class SamsaraFixture : IDisposable
    {
        readonly string _dir;

        public SamsaraEngine Engine { get; }

        /// <summary>global 配置目录（Assets/StreamingAssets/Configs/global）。</summary>
        public static string GlobalDir => Path.Combine(TestEnv.ConfigsRoot, "global");

        public SamsaraFixture(JObject seed = null)
        {
            _dir = Path.Combine(Path.GetTempPath(), "samsara_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var save = Path.Combine(_dir, "state.json");
            var data = seed ?? new JObject { ["version"] = 4 };
            File.WriteAllText(save, data.ToString(Newtonsoft.Json.Formatting.None));
            Engine = new SamsaraEngine(GlobalDir, save);
        }

        public void Set(string key, JToken value) => Engine.State.Set(key, value);

        /// <summary>取顶层子对象的**实时**引用（对其写入即改内存状态）。</summary>
        public JObject Obj(string key) => (JObject)Engine.State.Get(key);

        public void SetSkills(IEnumerable<string> ids) => Engine.State.Set("skills", BuildSkills(ids));

        public static JObject BuildSkills(IEnumerable<string> ids)
        {
            var o = new JObject();
            foreach (var id in ids)
                o[id] = new JObject { ["unlocked_at"] = "2026-01-01T00:00:00", ["tier"] = 1 };
            return o;
        }

        /// <summary>加载 Fixtures/samsara_*.json（传入不含扩展名的完整夹具名）。</summary>
        public static JObject Fixture(string name) => TestEnv.LoadFixture(name);

        // ── 断言辅助 ──────────────────────────────────────────────

        public static void AssertNumber(double expected, JToken actual, double tol, string message)
        {
            Assert.IsNotNull(actual, message + " (实际值为 null)");
            Assert.AreEqual(expected, actual.Value<double>(), tol, message);
        }

        public static void AssertInt(int expected, JToken actual, string message)
        {
            Assert.IsNotNull(actual, message + " (实际值为 null)");
            Assert.AreEqual(expected, actual.Value<int>(), message);
        }

        public static void AssertBool(bool expected, JToken actual, string message)
        {
            Assert.IsNotNull(actual, message + " (实际值为 null)");
            Assert.AreEqual(expected, actual.Value<bool>(), message);
        }

        public static void AssertStr(string expected, JToken actual, string message)
        {
            var value = (actual == null || actual.Type == JTokenType.Null) ? null : actual.Value<string>();
            Assert.AreEqual(expected, value, message);
        }

        /// <summary>逐字段比较 JToken（数值用容差、布尔/字符串精确）。</summary>
        public static void AssertToken(JToken expected, JToken actual, string label)
        {
            if (expected == null || expected.Type == JTokenType.Null)
            {
                Assert.IsTrue(actual == null || actual.Type == JTokenType.Null, label + ": 期望 null");
                return;
            }
            switch (expected.Type)
            {
                case JTokenType.Boolean:
                    Assert.IsNotNull(actual, label + " (实际 null)");
                    Assert.AreEqual(expected.Value<bool>(), actual.Value<bool>(), label);
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    Assert.IsNotNull(actual, label + " (实际 null)");
                    Assert.AreEqual(expected.Value<double>(), actual.Value<double>(), 1e-9, label);
                    break;
                case JTokenType.String:
                    AssertStr(expected.Value<string>(), actual, label);
                    break;
                default:
                    Assert.IsTrue(JToken.DeepEquals(expected, actual), label + ": 期望 " + expected + " 实际 " + actual);
                    break;
            }
        }

        /// <summary>比较技能修饰符对象（全部已知键；数值容差比较）。</summary>
        public static void AssertModifiers(JObject expected, JObject actual, string label)
        {
            Assert.IsNotNull(actual, label + ": 修饰符为 null");
            foreach (var prop in expected.Properties())
                AssertToken(prop.Value, actual[prop.Name], label + "." + prop.Name);
            Assert.AreEqual(expected.Count, actual.Count, label + ": 修饰符键数量");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            }
            catch (Exception)
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
