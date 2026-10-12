using System;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 业力系统测试（KarmaSystem 状态机 + KarmaAssessor 本地镜像）。
    ///
    /// 覆盖：
    ///  1) 消业 recover / 作弊 consume / 技能调制（消业倍率）的夹具等价性；
    ///  2) 核心公式 Δ = C × O^α 的**独立推导**期望值（硬编码字面量，非调用同一实现）；
    ///  3) 技能树对 C/α 的实际调制（解锁前 vs 解锁后同一输入产出不同结果）；
    ///  4) 阈值/钳制边界（恰好等于、超过单次上限、负值下限）。
    ///
    /// 期望值来自 legacy-web Python 原版（tools/verify/gen-samsara-fixtures.py）。
    /// </summary>
    public sealed class SamsaraKarmaTests
    {
        // ── 夹具等价性：消业 ────────────────────────────────────────

        [Test]
        public void Recover_Cases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_karma");
            foreach (var tk in (JArray)fx["recover_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.SetSkills(ToStrings((JArray)c["skills"]));
                f.Set("level_karma", c["karma_before"].Value<int>());

                var amount = f.Engine.Karma.Recover(c["game_type"].Value<string>(), c["event_type"].Value<string>());

                Assert.AreEqual(c["expected_amount"].Value<int>(), amount, name + ": 消业量");
                Assert.AreEqual(c["expected_karma_after"].Value<int>(), f.Engine.GetKarma(), name + ": 业力终值");
            }
        }

        // ── 夹具等价性：作弊（单次上限/溢出） ───────────────────────

        [Test]
        public void Consume_Cases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_karma");
            foreach (var tk in (JArray)fx["consume_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.SetSkills(ToStrings((JArray)c["skills"]));
                f.Set("level_karma", c["karma_before"].Value<int>());

                var result = f.Engine.Karma.Consume(c["amount"].Value<int>());
                var exp = (JObject)c["expected"];

                Assert.AreEqual(exp["actual_increased"].Value<int>(), result.Item1, name + ": actual_increased");
                Assert.AreEqual(exp["is_overdraft"].Value<bool>(), result.Item2, name + ": is_overdraft");
                Assert.AreEqual(exp["overdraft_amount"].Value<double>(), result.Item3, 1e-9, name + ": overdraft_amount");
                Assert.AreEqual(exp["new_karma"].Value<int>(), f.Engine.GetKarma(), name + ": new_karma");
            }
        }

        // ── 本地镜像 KarmaAssessor ──────────────────────────────────

        [Test]
        public void AssessorIncrease_Cases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_karma");
            foreach (var tk in (JArray)fx["assessor_increase_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.Engine.Assessor.SetLocalKarmaState(
                    c["karma_before"].Value<int>(), c["karma_max"].Value<int>(), c["single_max"].Value<int>());

                var r = f.Engine.Assessor.IncreaseKarma(c["amount"].Value<int>(), (JObject)c["modifiers"]);
                var exp = (JObject)c["expected"];

                Assert.AreEqual(exp["actual_increased"].Value<int>(), r["actual_increased"].Value<int>(), name + ": actual_increased");
                Assert.AreEqual(exp["is_overdraft"].Value<bool>(), r["is_overdraft"].Value<bool>(), name + ": is_overdraft");
                Assert.AreEqual(exp["overdraft_amount"].Value<double>(), r["overdraft_amount"].Value<double>(), 1e-9, name + ": overdraft_amount");
                Assert.AreEqual(exp["new_karma"].Value<int>(), r["new_karma"].Value<int>(), name + ": new_karma");
                Assert.AreEqual(exp["success"].Value<bool>(), r["success"].Value<bool>(), name + ": success");
            }
        }

        [Test]
        public void AssessorDecrease_Cases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_karma");
            foreach (var tk in (JArray)fx["assessor_decrease_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.Engine.Assessor.SetLocalKarmaState(c["karma_before"].Value<int>(), 120, 120);

                var r = f.Engine.Assessor.DecreaseKarma(c["amount"].Value<int>(), (JObject)c["modifiers"]);

                Assert.AreEqual(c["expected_decreased"].Value<int>(), r["actual_decreased"].Value<int>(), name + ": actual_decreased");
                Assert.AreEqual(c["expected_karma_after"].Value<int>(), r["new_karma"].Value<int>(), name + ": new_karma");
            }
        }

        [Test]
        public void AssessorGetState_MatchesPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_karma");
            using var f = new SamsaraFixture();
            f.Engine.Assessor.SetLocalKarmaState(70, 120, 140);
            f.Engine.Assessor.SetRealmDetection(12.5, "animal");

            var mods = new JObject { ["karma_max_bonus"] = 20, ["karma_single_max_bonus"] = 30, ["initial_karma_reduction"] = 25 };
            var actual = f.Engine.Assessor.GetState(mods);
            var expected = (JObject)fx["assessor_get_state"];

            SamsaraFixture.AssertInt(expected["karma"]["current"].Value<int>(), actual["karma"]["current"], "karma.current");
            SamsaraFixture.AssertInt(expected["karma"]["max"].Value<int>(), actual["karma"]["max"], "karma.max");
            SamsaraFixture.AssertInt(expected["karma"]["single_max"].Value<int>(), actual["karma"]["single_max"], "karma.single_max");
            SamsaraFixture.AssertInt(expected["karma"]["initial"].Value<int>(), actual["karma"]["initial"], "karma.initial");
            SamsaraFixture.AssertNumber(expected["detection"].Value<double>(), actual["detection"], 1e-9, "detection");
            SamsaraFixture.AssertStr(expected["realm"].Value<string>(), actual["realm"], "realm");
        }

        // ── 核心公式：Δ = C × O^α（独立推导的期望值） ───────────────

        [Test]
        public void DetectionDelta_Formula_MatchesHandDerivedLiterals()
        {
            using var f = new SamsaraFixture();
            var mods = new JObject { ["detection_coefficient"] = 0.1, ["detection_alpha"] = 1.5 };

            // 独立推导：Δ = C × O^α，C=0.1，α=1.5
            //   O=100 → 0.1 × 100^1.5 = 0.1 × 1000   = 100
            //   O=64  → 0.1 × 64^1.5  = 0.1 × 512    = 51.2
            //   O=25  → 0.1 × 25^1.5  = 0.1 × 125    = 12.5
            //   O=4   → 0.1 × 4^1.5   = 0.1 × 8      = 0.8
            //   O=1   → 0.1 × 1^1.5   = 0.1 × 1      = 0.1
            Assert.AreEqual(100.0, f.Engine.Assessor.CalculateDetectionDelta(100, mods), 1e-9, "O=100");
            Assert.AreEqual(51.2, f.Engine.Assessor.CalculateDetectionDelta(64, mods), 1e-9, "O=64");
            Assert.AreEqual(12.5, f.Engine.Assessor.CalculateDetectionDelta(25, mods), 1e-9, "O=25");
            Assert.AreEqual(0.8, f.Engine.Assessor.CalculateDetectionDelta(4, mods), 1e-9, "O=4");
            Assert.AreEqual(0.1, f.Engine.Assessor.CalculateDetectionDelta(1, mods), 1e-9, "O=1");
            // 边界：O ≤ 0 → 0（不产生识破增长）
            Assert.AreEqual(0.0, f.Engine.Assessor.CalculateDetectionDelta(0, mods), 1e-9, "O=0");
            Assert.AreEqual(0.0, f.Engine.Assessor.CalculateDetectionDelta(-5, mods), 1e-9, "O=-5");
        }

        [Test]
        public void DetectionDelta_Formula_Cases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_karma");
            foreach (var tk in (JArray)fx["assessor_delta_formula_cases"])
            {
                var c = (JObject)tk;
                using var f = new SamsaraFixture();
                var mods = new JObject
                {
                    ["detection_coefficient"] = c["coefficient"].Value<double>(),
                    ["detection_alpha"] = c["alpha"].Value<double>(),
                };
                var O = c["overshoot"].Value<double>();
                var label = $"O={O} C={c["coefficient"]} α={c["alpha"]}";
                Assert.AreEqual(c["expected"].Value<double>(),
                    f.Engine.Assessor.CalculateDetectionDelta(O, mods), 1e-9, label);
            }
        }

        // ── 技能对 C 的实际调制（解锁前 vs 解锁后） ─────────────────

        [Test]
        public void SkillTree_Modulates_Coefficient_BeforeAfterUnlock()
        {
            using var f = new SamsaraFixture();

            // 解锁前（清空默认 stealth_t1）：C=0.1 → O=64 → 51.2
            f.SetSkills(new string[0]);
            var before = f.Engine.Detection.CalculateDelta(64);

            // 解锁后（stealth_t1「藏锋」：识破系数 0.1 → 0.07）→ 0.07×512 = 35.84
            f.SetSkills(new[] { "stealth_t1" });
            var after = f.Engine.Detection.CalculateDelta(64);

            Assert.AreEqual(51.2, before, 1e-9, "解锁前 C=0.1");
            Assert.AreEqual(35.84, after, 1e-9, "解锁后 C=0.07");
            Assert.Less(after, before, "同一输入 O=64：解锁隐匿术后识破增长应当更小");
        }

        [Test]
        public void SkillTree_Modulates_Alpha_BeforeAfterUnlock()
        {
            using var f = new SamsaraFixture();
            // karma_capacity_t3a「缓冲」：α 1.5 → 1.3（与 stealth_t1 的 C=0.07 叠加）
            f.SetSkills(new[] { "stealth_t1" });
            var alpha15 = f.Engine.Detection.CalculateDelta(100); // 0.07 × 100^1.5 = 70
            f.SetSkills(new[] { "stealth_t1", "karma_capacity_t1", "karma_capacity_t3a" });
            var alpha13 = f.Engine.Detection.CalculateDelta(100); // 0.07 × 100^1.3

            Assert.AreEqual(70.0, alpha15, 1e-9, "α=1.5 → 70");
            // 独立推导：100^1.3 = 10^(2.6) ≈ 398.10717055349734；×0.07 ≈ 27.867501938744816
            Assert.AreEqual(27.867501938744816, alpha13, 1e-6, "α=1.3");
            Assert.Less(alpha13, alpha15, "α 降低后同一溢出量识破增长更小");
        }

        [Test]
        public void KarmaCapacityT2B_Modulates_RecoverMultiplier()
        {
            using var f = new SamsaraFixture();
            // capture_pawn 基础消业 8；业力潮汐 ×1.3 → int(8×1.3)=10
            f.Set("level_karma", 50);
            f.SetSkills(new[] { "stealth_t1" });
            Assert.AreEqual(8, f.Engine.Karma.Recover("xiangqi", "capture_pawn"), "无加成：8");

            using var f2 = new SamsaraFixture();
            f2.Set("level_karma", 50);
            f2.SetSkills(new[] { "stealth_t1", "karma_capacity_t1", "karma_capacity_t2b" });
            Assert.AreEqual(10, f2.Engine.Karma.Recover("xiangqi", "capture_pawn"), "业力潮汐：int(8×1.3)=10");
        }

        // ── 阈值 / 钳制边界 ─────────────────────────────────────────

        [Test]
        public void Consume_SingleMaxBoundary_IsInclusive()
        {
            using var f = new SamsaraFixture();
            f.Set("level_karma", 0);

            var atMax = f.Engine.Karma.Consume(120); // 恰好等于单次上限 → 允许
            Assert.AreEqual(120, atMax.Item1, "120 是单次上限，应当允许");
            Assert.AreEqual(120, f.Engine.GetKarma(), "业力=120（恰好安全阈值）");
            Assert.IsFalse(atMax.Item2, "120 不溢出（阈值 120）");

            var overMax = f.Engine.Karma.Consume(121); // 超过单次上限 → 拒绝且不改变业力
            Assert.AreEqual(0, overMax.Item1, "121 > 单次上限应被拒绝");
            Assert.AreEqual(120, f.Engine.GetKarma(), "拒绝后业力不变");
        }

        [Test]
        public void Karma_NeverGoesBelowZero()
        {
            using var f = new SamsaraFixture();
            f.Set("level_karma", 5);
            var decreased = f.Engine.State.DecreaseKarma(100);
            Assert.AreEqual(5, decreased, "实际减少量为 5（下限 0）");
            Assert.AreEqual(0, f.Engine.GetKarma(), "业力钳制在 0");
        }

        [Test]
        public void OvershootCarryover_ExactThreshold_NoOvershoot()
        {
            using var f = new SamsaraFixture();
            f.Set("level_karma", 120); // 恰好等于阈值 120
            Assert.AreEqual(0, f.Engine.RecordLevelEnd(), "恰好等于阈值：溢出 0");

            f.Set("level_karma", 121);
            Assert.AreEqual(1, f.Engine.RecordLevelEnd(), "超过阈值 1：溢出 1");
        }

        [Test]
        public void OvershootCarryover_HonorsKarmaMaxBonus()
        {
            using var f = new SamsaraFixture();
            // karma_capacity_t1「安全阈值+20」：阈值 120 → 140
            f.SetSkills(new[] { "stealth_t1", "karma_capacity_t1" });
            f.Set("level_karma", 130);
            Assert.AreEqual(0, f.Engine.RecordLevelEnd(), "阈值 140：130 不溢出");
            f.Set("level_karma", 155);
            Assert.AreEqual(15, f.Engine.RecordLevelEnd(), "阈值 140：155 溢出 15");
        }

        static System.Collections.Generic.List<string> ToStrings(JArray arr)
        {
            var list = new System.Collections.Generic.List<string>();
            if (arr != null)
                foreach (var t in arr) list.Add(t.Value<string>());
            return list;
        }
    }
}
