using System.Collections.Generic;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// Boss 系统测试（BossSystem 守道者技能 + HeavenBossSystem 天道 Boss 战）。
    ///
    /// 覆盖：六道守道者机械字段（含 story.json 名字覆盖）、技能触发判定
    /// （确定性分支：take_more 计数 / rank_lock / purify 次数 / 人道无技能 /
    /// 减益后 flip·chaos 概率归零）、天道 Boss 进入条件、机械配置与胜负处理。
    /// </summary>
    public sealed class SamsaraBossTests
    {
        static readonly string[] Realms = { "hell", "hungry", "animal", "human", "asura", "heaven" };

        [Test]
        public void BossDefinitions_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_boss");
            foreach (var tk in ((JObject)fx["definitions"]).Properties())
            {
                var realm = tk.Name;
                var exp = (JObject)tk.Value;
                using var f = new SamsaraFixture();
                f.Set("current_realm", realm);
                var boss = f.Engine.Bosses.GetCurrentBoss();

                Assert.AreEqual(exp["id"].Value<string>(), boss["id"].Value<string>(), realm + ": id");
                Assert.AreEqual(exp["name"].Value<string>(), boss["name"].Value<string>(), realm + ": name（story 覆盖）");
                Assert.AreEqual(exp["title"].Value<string>(), boss["title"].Value<string>(), realm + ": title");
                Assert.AreEqual(exp["ai_personality"].Value<string>(), boss["ai_personality"].Value<string>(), realm + ": ai_personality");
                Assert.AreEqual(exp["icon"].Value<string>(), boss["icon"].Value<string>(), realm + ": icon");
                var expSkill = exp["skill"].Type == JTokenType.Null ? null : exp["skill"].Value<string>();
                var actSkill = boss["skill"] == null || boss["skill"].Type == JTokenType.Null ? null : boss["skill"].Value<string>();
                Assert.AreEqual(expSkill, actSkill, realm + ": skill");
            }
        }

        [Test]
        public void TriggerBossSkill_Deterministic_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_boss");
            foreach (var tk in (JArray)fx["trigger_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.Set("current_realm", c["realm"].Value<string>());
                f.SetSkills(ToStrings((JArray)c["skills"]));

                var res = f.Engine.Bosses.TriggerBossSkill(c["cheat_count"].Value<int>());

                Assert.AreEqual(c["expected_triggered"].Value<bool>(), res["triggered"].Value<bool>(), name + ": triggered");
                var expSkill = c["expected_skill"].Type == JTokenType.Null ? null : c["expected_skill"].Value<string>();
                var actSkill = res["skill"] == null || res["skill"].Type == JTokenType.Null ? null : res["skill"].Value<string>();
                Assert.AreEqual(expSkill, actSkill, name + ": skill");
                var expEffect = c["expected_effect"].Type == JTokenType.Null ? null : c["expected_effect"].Value<string>();
                var actEffect = res["effect"] == null || res["effect"].Type == JTokenType.Null ? null : res["effect"].Value<string>();
                Assert.AreEqual(expEffect, actEffect, name + ": effect");
            }
        }

        [Test]
        public void TriggerBossSkill_RandomSkills_ReportCorrectSkillId()
        {
            // flip / chaos 为概率技能（不可断言 triggered），但 skill 字段必须正确。
            var fx = SamsaraFixture.Fixture("samsara_boss");
            foreach (var tk in (JArray)fx["nondeterministic_skills"])
            {
                var c = (JObject)tk;
                using var f = new SamsaraFixture();
                f.Set("current_realm", c["realm"].Value<string>());
                var res = f.Engine.Bosses.TriggerBossSkill(1);
                Assert.AreEqual(c["expected_skill"].Value<string>(), res["skill"].Value<string>(), c["realm"].Value<string>() + ": skill");
                Assert.IsTrue(res["triggered"].Type == JTokenType.Boolean, "triggered 应为布尔");
            }
        }

        [Test]
        public void Purify_LimitedUses_PerLevel()
        {
            var fx = SamsaraFixture.Fixture("samsara_boss");
            var exp = (JObject)fx["purify_sequence"];
            using var f = new SamsaraFixture();
            f.Set("current_realm", exp["realm"].Value<string>());

            var expected = new List<bool>();
            foreach (var t in (JArray)exp["expected"]) expected.Add(t.Value<bool>());

            for (var i = 0; i < expected.Count; i++)
            {
                var res = f.Engine.Bosses.TriggerBossSkill(1);
                Assert.AreEqual(expected[i], res["triggered"].Value<bool>(), $"禅定者第 {i + 1} 次技能");
            }

            f.Engine.Bosses.ResetBossSkills();
            var afterReset = f.Engine.Bosses.TriggerBossSkill(1);
            Assert.AreEqual(exp["after_reset"].Value<bool>(), afterReset["triggered"].Value<bool>(), "ResetBossSkills 后恢复一次");
        }

        // ── 天道 Boss ───────────────────────────────────────────────

        [Test]
        public void HeavenBoss_Info_Rules_Pieces_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_boss");
            var hb = (JObject)fx["heaven_boss"];
            using var f = new SamsaraFixture();

            var info = f.Engine.HeavenBoss.GetBossInfo();
            var expInfo = (JObject)hb["info"];
            foreach (var key in new[] { "boss_id", "boss_name", "chess_type", "difficulty", "bgm", "background", "background_vortex", "note" })
                Assert.AreEqual(expInfo[key].Value<string>(), info[key].Value<string>(), "boss_info." + key);
            Assert.IsTrue(JToken.DeepEquals(expInfo["victory_condition"], info["victory_condition"]), "victory_condition");
            Assert.IsTrue(JToken.DeepEquals(expInfo["defeat_condition"], info["defeat_condition"]), "defeat_condition");

            Assert.IsTrue(JToken.DeepEquals((JObject)hb["rules"], f.Engine.HeavenBoss.GetRules()), "rules 深比较");
            Assert.IsTrue(JToken.DeepEquals((JObject)hb["pieces"], f.Engine.HeavenBoss.GetPiecesConfig()), "pieces 深比较");
            Assert.IsTrue(JToken.DeepEquals((JObject)hb["initial_board"], f.Engine.HeavenBoss.GetInitialBoard()), "initial_board 深比较");
        }

        [Test]
        public void HeavenBoss_CanEnter_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_boss");
            foreach (var tk in (JArray)((JObject)fx["heaven_boss"])["can_enter_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.Set("prayer_count", c["prayer"].Value<int>());
                if (c["realms_all"].Value<bool>())
                    foreach (var r in Realms) f.Obj("realm_progress")[r]["completed"] = true;
                if (c["defeated"].Value<bool>())
                    f.Obj("tiandao_boss_state")["defeated"] = true;

                var res = f.Engine.HeavenBoss.CanEnter();
                Assert.AreEqual(c["expected_can_enter"].Value<bool>(), res["can_enter"].Value<bool>(), name + ": can_enter");
                Assert.AreEqual(c["expected_reason"].Value<string>(), res["reason"].Value<string>(), name + ": reason");
                var expDefeated = c["expected_already_defeated"].Value<bool>();
                var actDefeated = res["already_defeated"] != null && res["already_defeated"].Value<bool>();
                Assert.AreEqual(expDefeated, actDefeated, name + ": already_defeated");
            }
        }

        [Test]
        public void HeavenBoss_EnterAndWin_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_boss");
            var c = (JObject)((JObject)fx["heaven_boss"])["enter_case"];
            using var f = new SamsaraFixture();
            foreach (var r in Realms) f.Obj("realm_progress")[r]["completed"] = true;
            f.Set("prayer_count", 1);

            var e1 = f.Engine.HeavenBoss.EnterBattle();
            var e2 = f.Engine.HeavenBoss.EnterBattle();
            Assert.IsTrue(e1["success"].Value<bool>(), "首次进入成功");
            Assert.AreEqual(c["first_attempt_count"].Value<int>(), e1["attempt_count"].Value<int>(), "首次尝试次数");
            Assert.AreEqual(c["second_attempt_count"].Value<int>(), e2["attempt_count"].Value<int>(), "二次尝试次数");

            var win = f.Engine.HeavenBoss.OnWin();
            Assert.AreEqual(c["on_win_defeated"].Value<bool>(), win["defeated"].Value<bool>(), "胜利后 defeated");
            Assert.AreEqual(c["on_win_next"].Value<string>(), win["next"].Value<string>(), "胜利触发结局");
            Assert.IsTrue(f.Engine.State.Get("tiandao_boss_state")["defeated"].Value<bool>(), "状态已落盘 defeated");
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
