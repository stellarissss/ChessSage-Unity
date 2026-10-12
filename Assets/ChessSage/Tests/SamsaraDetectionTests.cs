using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 识破系统测试（DetectionModel）。
    ///
    /// 核心机制：识破概率随业障溢出累积 Δ = C × O^α；每关用 random()*100 &lt; detection 结算。
    /// 本类覆盖：
    ///  1) 公式 Δ = C × O^α 的独立推导期望值（硬编码字面量）；
    ///  2) **单调性**（可证伪的强断言）：同一 C/α 下 O 越大 Δ 越大；
    ///  3) 技能调制后系数/指数变化对 Δ 的影响；
    ///  4) HandleOverdraft 的确定性分支（非正溢出 / 锁死 / 首次透支豁免）；
    ///  5) 判词文案来源（story.json.detection）。
    /// </summary>
    public sealed class SamsaraDetectionTests
    {
        [Test]
        public void DeltaCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_detection");
            foreach (var tk in (JArray)fx["delta_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.SetSkills(ToStrings((JArray)c["skills"]));
                var actual = f.Engine.Detection.CalculateDelta(c["overshoot"].Value<double>());
                Assert.AreEqual(c["expected"].Value<double>(), actual, 1e-6, name + ": Δ");
            }
        }

        [Test]
        public void Delta_IsStrictlyMonotonic_InOvershoot()
        {
            var fx = SamsaraFixture.Fixture("samsara_detection");
            using var f = new SamsaraFixture();
            f.SetSkills(new string[0]); // C=0.1, α=1.5

            double prev = -1;
            foreach (var tk in (JArray)fx["monotonic_points"])
            {
                var c = (JObject)tk;
                var O = c["overshoot"].Value<double>();
                var delta = f.Engine.Detection.CalculateDelta(O);
                Assert.AreEqual(c["delta"].Value<double>(), delta, 1e-9, $"O={O}: 与 Python 期望一致");
                Assert.Greater(delta, prev, $"单调性：O 从较小值增至 {O} 时 Δ 必须严格增大");
                prev = delta;
            }
        }

        [Test]
        public void Delta_Formula_HandDerived_AtDetectionModel()
        {
            using var f = new SamsaraFixture();
            f.SetSkills(new string[0]); // C=0.1, α=1.5
            // 独立推导：Δ = C × O^α
            Assert.AreEqual(100.0, f.Engine.Detection.CalculateDelta(100), 1e-9, "0.1×100^1.5");
            Assert.AreEqual(51.2, f.Engine.Detection.CalculateDelta(64), 1e-9, "0.1×64^1.5");
            Assert.AreEqual(0.0, f.Engine.Detection.CalculateDelta(0), 1e-9, "O=0 → 0");
            Assert.AreEqual(0.0, f.Engine.Detection.CalculateDelta(-1), 1e-9, "O<0 → 0");
        }

        [Test]
        public void HandleOverdraft_DeterministicBranches_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_detection");
            var fixedCases = (JObject)fx["handle_overdraft_fixed"];

            // (a) 非正溢出
            using (var f = new SamsaraFixture())
            {
                var r = f.Engine.Detection.HandleOverdraft(0.0);
                Assert.AreEqual(fixedCases["nonpositive"]["detected"].Value<bool>(), r["detected"].Value<bool>(), "非正溢出: detected");
                Assert.AreEqual(fixedCases["nonpositive"]["delta"].Value<double>(), r["delta"].Value<double>(), 1e-9, "非正溢出: delta");
            }

            // (b) 锁死状态：不累积、不判定
            using (var f = new SamsaraFixture())
            {
                f.Obj("detection_state")["detection_locked"] = true;
                var r = f.Engine.Detection.HandleOverdraft(50.0);
                Assert.IsFalse(r["detected"].Value<bool>(), "锁死: detected=false");
                Assert.IsTrue(r["locked"].Value<bool>(), "锁死: locked=true");
                Assert.AreEqual(0.0, r["current"].Value<double>(), 1e-9, "锁死: current=0");
            }

            // (c) 首次透支豁免（stealth_t2a 每关一次）
            using (var f = new SamsaraFixture())
            {
                f.SetSkills(new[] { "stealth_t1", "stealth_t2a" });
                var r = f.Engine.Detection.HandleOverdraft(50.0);
                Assert.IsTrue(r["skip"].Value<bool>(), "首次透支: skip=true");
                Assert.IsTrue(f.Engine.State.IsOneTimeSkillUsed("stealth_t2a"), "首次透支: 应消耗 stealth_t2a");
                Assert.AreEqual(fixedCases["first_skip"]["current"].Value<double>(), r["current"].Value<double>(), 1e-9, "首次透支: current 不变");
            }
        }

        [Test]
        public void LockedDetection_NeverTriggers()
        {
            using var f = new SamsaraFixture();
            f.Obj("detection_state")["detection_locked"] = true;
            Assert.IsFalse(f.Engine.Detection.Check(100.0), "锁死状态下 Check 恒为 false");
            Assert.IsFalse(f.Engine.Detection.Check(50.0), "锁死状态下 Check 恒为 false");
        }

        [Test]
        public void ZeroDetection_NeverTriggers()
        {
            using var f = new SamsaraFixture();
            Assert.IsFalse(f.Engine.Detection.Check(0.0), "detection=0 永不命中");
            Assert.IsFalse(f.Engine.Detection.Check(-1.0), "detection<0 永不命中");
        }

        [Test]
        public void ResetOnDetection_LocksAndClearsRealmDetections()
        {
            using var f = new SamsaraFixture();
            f.Engine.SetDetection(60.0);
            Assert.AreEqual(60.0, f.Engine.GetDetection(), 1e-9, "识别概率已设置");

            f.Engine.State.ResetOnDetection();

            Assert.IsTrue(f.Engine.IsDetectionLocked(), "被识破后概率锁死");
            Assert.AreEqual(0.0, f.Engine.GetDetection(), 1e-9, "被识破后本道识破清零");
            Assert.IsFalse(f.Engine.Detection.Check(100.0), "锁死后永不命中");
            Assert.IsTrue(f.Engine.State.IsExposurePathTriggered(), "标记识破路径");
        }

        [Test]
        public void JudgmentText_ComesFromStoryJson()
        {
            var fx = SamsaraFixture.Fixture("samsara_detection");
            using var f = new SamsaraFixture();
            Assert.AreEqual(fx["judgment_text"].Value<string>(), f.Engine.Detection.GetJudgmentText(), "识破判词");
            Assert.AreEqual(fx["reset_message"].Value<string>(), f.Engine.Detection.GetResetMessage(), "重置判词");
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
