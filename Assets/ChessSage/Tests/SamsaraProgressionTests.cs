using System.Collections.Generic;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 进度系统测试（ProgressionSystem）：关卡结算奖励、道内推进（无透支跳 2 道）、
    /// 道间倒退、通关解锁沙盒。
    /// </summary>
    public sealed class SamsaraProgressionTests
    {
        [Test]
        public void ResolveLevelCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_progression");
            foreach (var tk in (JArray)fx["resolve_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                var realm = c["realm"].Value<string>();
                using var f = new SamsaraFixture();
                f.Set("current_realm", realm);
                f.Set("level_karma", c["level_karma"].Value<int>());
                f.Set("overdraft_count", c["overdraft_count"].Value<int>());
                f.Set("sandbox_mode", c["sandbox_mode"].Value<bool>());
                f.Obj("realm_progress")[realm]["levels_passed"] = c["levels_passed_before"].Value<int>();

                var res = f.Engine.Progression.ResolveLevel(
                    c["won"].Value<bool>(), c["no_cheat"].Value<bool>(), c["boss_defeated"].Value<bool>());
                var exp = (JObject)c["expected"];

                Assert.AreEqual(exp["skill_points"].Value<int>(), res["skill_points"].Value<int>(), name + ": skill_points");
                Assert.AreEqual(exp["overshoot_carryover"].Value<int>(), res["overshoot_carryover"].Value<int>(), name + ": overshoot_carryover");
                Assert.AreEqual(exp["sandbox_unlocked"].Value<bool>(),
                    res["sandbox_unlocked"] != null && res["sandbox_unlocked"].Value<bool>(), name + ": sandbox_unlocked");

                var expectedReasons = new List<string>();
                foreach (var t in (JArray)exp["bonus_reasons"]) expectedReasons.Add(t.Value<string>());
                var actualReasons = new List<string>();
                foreach (var t in (JArray)res["bonus_reasons"]) actualReasons.Add(t.Value<string>());
                CollectionAssert.AreEqual(expectedReasons, actualReasons, name + ": bonus_reasons");

                Assert.AreEqual(c["expected_skill_points_total"].Value<int>(), f.Engine.GetSkillPoints(), name + ": 技能点总额");
                Assert.AreEqual(c["expected_levels_passed"].Value<int>(),
                    f.Engine.State.Get("realm_progress")[realm]["levels_passed"].Value<int>(), name + ": levels_passed");
            }
        }

        [Test]
        public void AdvanceRealmCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_progression");
            foreach (var tk in (JArray)fx["advance_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                var realm = c["realm"].Value<string>();
                using var f = new SamsaraFixture();
                f.Set("current_realm", realm);
                f.Set("overdraft_count", c["overdraft"].Value<int>());
                f.Obj("realm_progress")[realm]["levels_passed"] = c["levels_passed"].Value<int>();

                var res = f.Engine.Progression.AdvanceRealm();
                var exp = (JObject)c["expected"];

                Assert.AreEqual(exp["advanced"].Value<bool>(), res["advanced"].Value<bool>(), name + ": advanced");
                AssertStr(exp["new_realm"], res["new_realm"], name + ": new_realm");
                AssertStr(exp["direction"], res["direction"], name + ": direction");
                Assert.AreEqual(c["expected_realm_after"].Value<string>(), f.Engine.GetCurrentRealm(), name + ": current_realm");
            }
        }

        [Test]
        public void RetreatRealmCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_progression");
            foreach (var tk in (JArray)fx["retreat_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.Set("current_realm", c["realm"].Value<string>());

                var res = f.Engine.Progression.RetreatRealm();
                var exp = (JObject)c["expected"];

                Assert.AreEqual(exp["retreated"].Value<bool>(), res["retreated"].Value<bool>(), name + ": retreated");
                AssertStr(exp["new_realm"], res["new_realm"], name + ": new_realm");
                Assert.AreEqual(c["expected_realm_after"].Value<string>(), f.Engine.GetCurrentRealm(), name + ": current_realm");
            }
        }

        [Test]
        public void RealmLevelCounts_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_progression");
            using var f = new SamsaraFixture();
            foreach (var tk in ((JObject)fx["realm_level_counts"]).Properties())
            {
                // 关卡池总关卡数即 ProgressionSystem 内部 _get_realm_level_count 的权威来源
                Assert.AreEqual(tk.Value.Value<int>(), f.Engine.Levels.GetTotalLevels(tk.Name), tk.Name + ": 关卡数");
            }
        }

        [Test]
        public void PerfectWin_AwardsAllBonuses()
        {
            // 独立断言：基础 1 + 无AI 2 + 未透支 1 = 4
            using var f = new SamsaraFixture();
            f.Set("current_realm", "hell");
            f.Set("level_karma", 50);
            var res = f.Engine.Progression.ResolveLevel(won: true, noCheat: true, bossDefeated: false);
            Assert.AreEqual(4, res["skill_points"].Value<int>(), "完美通关奖励");
            Assert.AreEqual(4, f.Engine.GetSkillPoints(), "技能点入账");
        }

        [Test]
        public void CleanRealmClear_UnlocksSandbox()
        {
            using var f = new SamsaraFixture();
            f.Set("current_realm", "hell");
            f.Set("level_karma", 50);
            f.Obj("realm_progress")["hell"]["levels_passed"] = 4; // 再 +1 = 5 = 总数
            var res = f.Engine.Progression.ResolveLevel(won: true, noCheat: true, bossDefeated: false);
            Assert.IsTrue(res["sandbox_unlocked"] != null && res["sandbox_unlocked"].Value<bool>(), "通关奖励中标记沙盒解锁");
            Assert.IsTrue(f.Engine.State.Get("realm_progress")["hell"]["completed"].Value<bool>(), "道标记完成");
            Assert.AreEqual(5, f.Engine.State.Get("realm_progress")["hell"]["levels_passed"].Value<int>(), "关卡通过数 = 5");
            // 说明：State.IsSandboxUnlocked / GetRealmProgress.sandbox_unlocked 存在已知移植缺陷
            // （JArray.Contains(string) 按引用比较恒为 false），其正确行为断言见
            // SamsaraKnownDefectsTests（[Explicit]，待 engineering-lead 修复 Core 后并入常规套件）。
        }

        [Test]
        public void AdvanceRealm_WithoutOverdraft_JumpsTwoRealms()
        {
            using var f = new SamsaraFixture();
            f.Set("current_realm", "hell");
            f.Set("overdraft_count", 0);
            f.Obj("realm_progress")["hell"]["levels_passed"] = 5;
            var res = f.Engine.Progression.AdvanceRealm();
            Assert.AreEqual("animal", res["new_realm"].Value<string>(), "无透支：地狱→畜生（跳 2 道）");
            Assert.AreEqual("up_2", res["direction"].Value<string>(), "direction=up_2");
        }

        static void AssertStr(JToken expected, JToken actual, string label)
        {
            var exp = (expected == null || expected.Type == JTokenType.Null) ? null : expected.Value<string>();
            var act = (actual == null || actual.Type == JTokenType.Null) ? null : actual.Value<string>();
            Assert.AreEqual(exp, act, label);
        }
    }
}
