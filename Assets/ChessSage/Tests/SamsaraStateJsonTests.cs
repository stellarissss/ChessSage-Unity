using System.Collections.Generic;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// JSON 加载与状态默认值测试（SamsaraJson / SamsaraState / SamsaraConstants）。
    ///
    /// 覆盖：global/ 下全部配置解析不抛异常且关键字段完整；从最小 seed 构造状态时
    /// 的默认值行为；技能键规范化；缺失字段时的默认值语义。
    /// </summary>
    public sealed class SamsaraStateJsonTests
    {
        [Test]
        public void AllGlobalConfigs_ParseWithoutError()
        {
            var fx = SamsaraFixture.Fixture("samsara_state");
            foreach (var nameToken in (JArray)fx["global_configs"])
            {
                var name = nameToken.Value<string>();
                JObject parsed = null;
                Assert.DoesNotThrow(() => parsed = TestEnv.LoadGlobalConfig(name), name + ".json 应能解析");
                Assert.IsNotNull(parsed, name + ".json 解析结果非空");
                Assert.Greater(parsed.Count, 0, name + ".json 不应为空对象");
            }
        }

        [Test]
        public void KeyPaths_ArePresentInConfigs()
        {
            var fx = SamsaraFixture.Fixture("samsara_state");
            var kp = (JObject)fx["key_paths"];

            var story = TestEnv.LoadGlobalConfig("story");
            foreach (var e in (JArray)kp["story_endings"])
                Assert.IsNotNull(story["endings"][e.Value<string>()], "story.endings." + e.Value<string>());
            foreach (var k in (JArray)kp["story_detection_keys"])
                Assert.IsNotNull(story["detection"][k.Value<string>()], "story.detection." + k.Value<string>());
            foreach (var k in (JArray)kp["tiandao_keys"])
                Assert.IsNotNull(story["tiandao"][k.Value<string>()], "story.tiandao." + k.Value<string>());

            var pools = TestEnv.LoadGlobalConfig("level_pools");
            foreach (var r in (JArray)kp["level_pools_realms"])
                Assert.IsNotNull(pools[r.Value<string>()], "level_pools." + r.Value<string>());

            var objectives = TestEnv.LoadGlobalConfig("objective_types");
            foreach (var k in (JArray)kp["objective_types_keys"])
                Assert.IsNotNull(objectives[k.Value<string>()], "objective_types." + k.Value<string>());
        }

        [Test]
        public void Defaults_FromMinimalSeed_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_state");
            var exp = (JObject)fx["expected_defaults"];
            using var f = new SamsaraFixture(); // seed {"version":4}
            var s = f.Engine.State;

            Assert.AreEqual(exp["version"].Value<int>(), s.GetInt("version", -1), "version");
            Assert.AreEqual(exp["current_realm"].Value<string>(), s.GetString("current_realm", null), "current_realm");
            Assert.AreEqual(exp["current_level"].Value<int>(), s.GetInt("current_level", -1), "current_level");
            Assert.AreEqual(exp["skill_points"].Value<int>(), s.GetInt("skill_points", -1), "skill_points");
            Assert.AreEqual(exp["karma_max"].Value<int>(), s.GetInt("karma_max", -1), "karma_max");
            Assert.AreEqual(exp["karma_single_max"].Value<int>(), s.GetInt("karma_single_max", -1), "karma_single_max");
            Assert.AreEqual(exp["initial_karma"].Value<int>(), s.GetInt("initial_karma", -1), "initial_karma");
            Assert.AreEqual(exp["turn_limit"].Value<int>(), s.GetInt("turn_limit", -1), "turn_limit");
            Assert.AreEqual(exp["level_karma"].Value<int>(), s.GetInt("level_karma", -1), "level_karma（=初始业力）");
            Assert.AreEqual(exp["no_cheat_this_level"].Value<bool>(), s.GetBool("no_cheat_this_level", false), "no_cheat_this_level");
            Assert.AreEqual(exp["playthrough_count"].Value<int>(), s.GetInt("playthrough_count", -1), "playthrough_count");
            Assert.AreEqual(exp["prayer_count"].Value<int>(), s.GetInt("prayer_count", -1), "prayer_count");

            CollectionAssert.AreEqual(
                ToStrings((JArray)exp["skills_keys"]),
                SortedKeys((JObject)s.Get("skills")), "默认已解锁技能（stealth_t1）");
            CollectionAssert.AreEqual(
                ToStrings((JArray)exp["realm_progress_keys"]),
                SortedKeys((JObject)s.Get("realm_progress")), "realm_progress 六道");
            CollectionAssert.AreEqual(
                ToStrings((JArray)exp["memory_fragments_keys"]),
                SortedKeys((JObject)s.Get("memory_fragments_unlocked")), "记忆碎片六道");
            CollectionAssert.AreEqual(
                ToStrings((JArray)exp["endings_keys"]),
                SortedKeys((JObject)s.Get("endings_unlocked")), "结局键");

            var tmpl = (JObject)exp["realm_progress_template"];
            var hell = (JObject)s.Get("realm_progress")["hell"];
            Assert.AreEqual(tmpl["completed"].Value<bool>(), hell["completed"].Value<bool>(), "realm_progress.hell.completed");
            Assert.AreEqual(tmpl["levels_passed"].Value<int>(), hell["levels_passed"].Value<int>(), "realm_progress.hell.levels_passed");
            Assert.AreEqual(tmpl["no_cheat_full_clear"].Value<bool>(), hell["no_cheat_full_clear"].Value<bool>(), "realm_progress.hell.no_cheat_full_clear");
        }

        [Test]
        public void NormalizeSkillKey_MatchesPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_state");
            using var f = new SamsaraFixture();
            foreach (var tk in (JArray)fx["normalize_cases"])
            {
                var c = (JObject)tk;
                var input = c["input"].Value<string>();
                Assert.AreEqual(c["expected"].Value<string>(), f.Engine.State.NormalizeSkillKey(input), $"normalize({input})");
            }
        }

        [Test]
        public void MissingFields_ReturnFallbacks()
        {
            using var f = new SamsaraFixture();
            var s = f.Engine.State;

            Assert.IsNull(s.Get("does_not_exist"), "Get 缺失键返回 null");
            Assert.AreEqual(42, s.GetInt("does_not_exist", 42), "GetInt 回退值");
            Assert.AreEqual(1.5, s.GetDouble("does_not_exist", 1.5), 1e-9, "GetDouble 回退值");
            Assert.IsTrue(s.GetBool("does_not_exist", true), "GetBool 回退值");
            Assert.AreEqual("fallback", s.GetString("does_not_exist", "fallback"), "GetString 回退值");
        }

        [Test]
        public void Seed_WithMissingKeys_BackfillsDefaults()
        {
            // 只给 current_realm，其余应被默认化
            using var f = new SamsaraFixture(new JObject { ["version"] = 4, ["current_realm"] = "human" });
            var s = f.Engine.State;

            Assert.AreEqual("human", s.GetString("current_realm", null), "seed 值保留");
            Assert.AreEqual(120, s.GetInt("karma_max", -1), "karma_max 默认化");
            Assert.AreEqual(20, s.GetInt("turn_limit", -1), "turn_limit 默认化");
            Assert.AreEqual(4, s.GetInt("version", -1), "version 保留");
            Assert.IsNotNull(s.Get("realm_progress")["human"], "realm_progress 默认化");
        }

        [Test]
        public void Constants_RealmsAndNames()
        {
            CollectionAssert.AreEqual(
                new[] { "hell", "hungry", "animal", "human", "asura", "heaven" },
                SamsaraConstants.Realms, "六道顺序");
            Assert.AreEqual("地狱道", SamsaraConstants.RealmName("hell"), "地狱道名");
            Assert.AreEqual("天道", SamsaraConstants.RealmName("heaven"), "天道名");
            Assert.AreEqual(string.Empty, SamsaraConstants.RealmName("nope"), "未知道返回空串");
        }

        [Test]
        public void FrontendState_HasRequiredKeys()
        {
            using var f = new SamsaraFixture();
            var state = f.Engine.GetState();
            foreach (var key in new[] { "karma", "karma_max", "karma_single_max", "detection",
                "realm_detections", "current_realm", "current_level", "skill_points",
                "skill_modifiers", "alignment", "detection_state", "memory_fragments_unlocked",
                "endings_unlocked", "realm_progress", "prayer_count", "version" })
                Assert.IsNotNull(state[key], "frontend state 缺字段: " + key);
        }

        static List<string> SortedKeys(JObject obj)
        {
            var list = new List<string>();
            foreach (var p in obj.Properties()) list.Add(p.Name);
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }

        static List<string> ToStrings(JArray arr)
        {
            var list = new List<string>();
            if (arr != null)
                foreach (var t in arr) list.Add(t.Value<string>());
            return list;
        }
    }
}
