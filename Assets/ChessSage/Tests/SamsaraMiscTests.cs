using System.Collections.Generic;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 补充覆盖：目标系统（ObjectiveSystem）、记忆碎片（MemoryFragmentSystem）、
    /// 选择系统（ChoiceSystem）。三者均为 Samsara 引擎的表现–逻辑桥。
    /// </summary>
    public sealed class SamsaraMiscTests
    {
        [Test]
        public void ObjectiveCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_misc");
            using var f = new SamsaraFixture();
            foreach (var tk in (JArray)fx["objective_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                f.Set("current_turn", c["current_turn"].Value<int>());

                var res = f.Engine.Objectives.Check((JObject)c["objective"], (JObject)c["game_state"]);
                var exp = (JObject)c["expected"];

                Assert.AreEqual(exp["completed"].Value<bool>(), res["completed"].Value<bool>(), name + ": completed");
                Assert.AreEqual(exp["progress"].Value<double>(), res["progress"].Value<double>(), 1e-9, name + ": progress");
                Assert.AreEqual(exp["message"].Value<string>(), res["message"].Value<string>(), name + ": message");
            }
        }

        [Test]
        public void MemoryFragmentCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_misc");
            using var f = new SamsaraFixture();
            var mf = f.Engine.MemoryFragments;
            var cases = (JArray)fx["memory_cases"];

            // 1) 条件未满足：拒绝
            var exp0 = (JObject)cases[0]["expected"];
            var r1 = mf.TryUnlock("hell");
            Assert.AreEqual(exp0["success"].Value<bool>(), r1["success"].Value<bool>(), "条件未满足: success");
            Assert.AreEqual(exp0["reason"].Value<string>(), r1["reason"].Value<string>(), "条件未满足: reason");

            // 2) 标记无作弊通关后：解锁
            f.Obj("realm_progress")["hell"]["no_cheat_full_clear"] = true;
            var exp1 = (JObject)cases[1]["expected"];
            var r2 = mf.TryUnlock("hell");
            Assert.AreEqual(exp1["success"].Value<bool>(), r2["success"].Value<bool>(), "解锁: success");
            Assert.AreEqual(exp1["already_unlocked"].Value<bool>(), r2["already_unlocked"].Value<bool>(), "解锁: already_unlocked");

            // 3) 重复解锁：already_unlocked
            var exp2 = (JObject)cases[2]["expected"];
            var r3 = mf.TryUnlock("hell");
            Assert.AreEqual(exp2["success"].Value<bool>(), r3["success"].Value<bool>(), "重复: success");
            Assert.AreEqual(exp2["already_unlocked"].Value<bool>(), r3["already_unlocked"].Value<bool>(), "重复: already_unlocked");
        }

        [Test]
        public void MemoryFragmentStatus_HasRequiredKeys()
        {
            var fx = SamsaraFixture.Fixture("samsara_misc");
            using var f = new SamsaraFixture();
            var status = f.Engine.MemoryFragments.GetAllFragmentsStatus();
            Assert.AreEqual(6, status.Count, "六道记忆碎片");
            foreach (var item in status)
                foreach (var k in (JArray)fx["memory_status_keys"])
                    Assert.IsNotNull(item[k.Value<string>()], "碎片状态缺字段: " + k.Value<string>());
        }

        [Test]
        public void ChoiceCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_misc");
            foreach (var tk in (JArray)fx["choice_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();

                var res = f.Engine.Choices.ApplyChoice(
                    c["realm"].Value<string>(), c["level"].Value<string>(),
                    c["choice_index"].Value<int>(), c["option_index"].Value<int>());
                var exp = (JObject)c["expected"];

                Assert.AreEqual(exp["success"].Value<bool>(), res["success"].Value<bool>(), name + ": success");
                AssertStr(exp["option_text"], res["option_text"], name + ": option_text");
                AssertStr(exp["hint"], res["hint"], name + ": hint");
                AssertToken(exp["is_final"], res["is_final"], name + ": is_final");

                Assert.IsTrue(JToken.DeepEquals((JObject)c["expected_alignment"], f.Engine.GetAlignment()), name + ": alignment");
                Assert.AreEqual(c["expected_karma_after"].Value<int>(), f.Engine.GetKarma(), name + ": 业力（含 karma_delta）");
                Assert.AreEqual(c["expected_choices_made"].Value<int>(), f.Engine.State.GetChoicesMade().Count, name + ": 选择历史条数");
            }
        }

        [Test]
        public void LevelWithoutChoices_ReturnsEmptyPanel()
        {
            var fx = SamsaraFixture.Fixture("samsara_misc");
            using var f = new SamsaraFixture();
            var panels = f.Engine.Choices.GetChoicesForLevel("hell", "1");
            Assert.AreEqual(fx["no_choice_level_count"].Value<int>(), panels.Count, "hell/1 无选择面板");
        }

        static void AssertStr(JToken expected, JToken actual, string label)
        {
            var exp = (expected == null || expected.Type == JTokenType.Null) ? null : expected.Value<string>();
            var act = (actual == null || actual.Type == JTokenType.Null) ? null : actual.Value<string>();
            Assert.AreEqual(exp, act, label);
        }

        static void AssertToken(JToken expected, JToken actual, string label)
        {
            if (expected == null || expected.Type == JTokenType.Null)
            {
                Assert.IsTrue(actual == null || actual.Type == JTokenType.Null, label + " 期望 null");
                return;
            }
            if (expected.Type == JTokenType.Boolean)
                Assert.AreEqual(expected.Value<bool>(), actual.Value<bool>(), label);
            else
                AssertStr(expected, actual, label);
        }
    }
}
