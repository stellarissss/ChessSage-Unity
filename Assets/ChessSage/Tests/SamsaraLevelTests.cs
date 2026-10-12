using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 关卡与回合上限测试（LevelSystem + TurnLimitSystem）。
    ///
    /// 覆盖：关卡池抽取合法（每道关卡列表 / game_type / 残局 puzzle_data）、
    /// 沙盒配置、关卡推进、回合上限触发与进度。
    /// </summary>
    public sealed class SamsaraLevelTests
    {
        static readonly string[] Realms = { "hell", "hungry", "animal", "human", "asura", "heaven" };

        [Test]
        public void RealmMeta_MatchesPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_levels");
            using var f = new SamsaraFixture();
            foreach (var tk in ((JObject)fx["realm_meta"]).Properties())
            {
                var realm = tk.Name;
                var exp = (JObject)tk.Value;
                var actual = f.Engine.Levels.GetRealmProgress(realm);

                Assert.AreEqual(exp["name"].Value<string>(), actual["name"].Value<string>(), realm + ": name");
                Assert.AreEqual(exp["game_type"].Value<string>(), actual["game_type"].Value<string>(), realm + ": game_type");
                Assert.AreEqual(exp["icon"].Value<string>(), actual["icon"].Value<string>(), realm + ": icon");
                Assert.AreEqual(exp["total_levels"].Value<int>(), actual["total_levels"].Value<int>(), realm + ": total_levels");
                Assert.AreEqual(exp["total_levels"].Value<int>(), f.Engine.Levels.GetTotalLevels(realm), realm + ": GetTotalLevels");
            }
        }

        [Test]
        public void AllLevels_LoadLegally_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_levels");
            using var f = new SamsaraFixture();
            foreach (var tk in (JArray)fx["levels"])
            {
                var c = (JObject)tk;
                var realm = c["realm"].Value<string>();
                var index = c["index"].Value<int>();
                var level = f.Engine.Levels.LoadLevel(realm, index);
                var label = $"{realm}[{index}]";

                Assert.IsNotNull(level, label + ": 关卡应可加载");
                Assert.AreEqual(c["id"].Value<string>(), level["id"].Value<string>(), label + ": id");
                Assert.AreEqual(c["name"].Value<string>(), level["name"].Value<string>(), label + ": name");
                Assert.AreEqual(c["type"].Value<string>(), level["type"].Value<string>(), label + ": type");
                Assert.AreEqual(c["objective_type"].Value<string>(), level["objective"]["type"].Value<string>(), label + ": objective.type");
                Assert.AreEqual(index, level["level_index"].Value<int>(), label + ": level_index");

                var hasPuzzle = level["puzzle_data"] != null && level["puzzle_data"].Type != JTokenType.Null;
                Assert.AreEqual(c["has_puzzle_data"].Value<bool>(), hasPuzzle, label + ": puzzle_data 存在性");
            }
        }

        [Test]
        public void OutOfRangeLevel_ReturnsNull()
        {
            using var f = new SamsaraFixture();
            Assert.IsNull(f.Engine.Levels.LoadLevel("hell", 99), "越界关卡应返回 null");
            Assert.IsNull(f.Engine.Levels.LoadLevel("no_such_realm", 0), "未知道应返回 null");
            Assert.AreEqual(0, f.Engine.Levels.GetTotalLevels("no_such_realm"), "未知道关卡数 0");
        }

        [Test]
        public void AdvanceToNextLevel_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_levels");
            foreach (var tk in (JArray)fx["advance_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.Set("current_realm", c["realm"].Value<string>());
                f.Set("current_level", c["current_level"].Value<int>());

                var res = f.Engine.Levels.AdvanceToNextLevel();
                Assert.AreEqual(c["expected_success"].Value<bool>(), res["success"].Value<bool>(), name + ": success");
                if (c["expected_success"].Value<bool>())
                {
                    Assert.AreEqual(c["expected_new_index"].Value<int>(), res["new_level"]["level_index"].Value<int>(), name + ": new_level.level_index");
                    Assert.AreEqual(c["expected_new_index"].Value<int>(), f.Engine.GetCurrentLevel(), name + ": state.current_level");
                }
            }
        }

        [Test]
        public void Sandbox_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_levels");
            using var f = new SamsaraFixture();
            foreach (var tk in (JArray)fx["sandbox_cases"])
            {
                var c = (JObject)tk;
                var realm = c["realm"].Value<string>();
                var sb = f.Engine.Levels.LoadSandbox(realm);
                Assert.AreEqual(c["expected_turn_limit"].Value<int>(), sb["turn_limit"].Value<int>(), realm + ": turn_limit");
                Assert.AreEqual(c["expected_ai_depth"].Value<int>(), sb["ai_depth"].Value<int>(), realm + ": ai_depth");
                Assert.AreEqual(c["expected_name"].Value<string>(), sb["name"].Value<string>(), realm + ": name");
                Assert.AreEqual(c["expected_objective"].Value<string>(), sb["objective"]["type"].Value<string>(), realm + ": objective");
            }
        }

        [Test]
        public void RealmLevelsStatus_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_levels");
            var c = (JObject)fx["realm_levels_status"];
            using var f = new SamsaraFixture();
            f.Obj("realm_progress")[c["realm"].Value<string>()]["levels_passed"] = c["levels_passed"].Value<int>();

            var rl = f.Engine.Levels.GetRealmLevels(c["realm"].Value<string>());
            var expected = (JArray)c["expected"];
            var levels = (JArray)rl["levels"];
            Assert.AreEqual(expected.Count, levels.Count, "关卡数量");
            for (var i = 0; i < expected.Count; i++)
                Assert.AreEqual(expected[i].Value<string>(), levels[i]["status"].Value<string>(), $"关卡 {i} 状态");
        }

        // ── 回合上限 ────────────────────────────────────────────────

        [Test]
        public void TurnLimitCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_levels");
            foreach (var tk in (JArray)fx["turn_limit_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.Set("turn_limit", c["limit"].Value<int>());
                f.Set("current_turn", c["current"].Value<int>());

                Assert.AreEqual(c["expected_remaining"].Value<int>(), f.Engine.TurnLimit.GetRemaining(), name + ": remaining");
                Assert.AreEqual(c["expected_over"].Value<bool>(), f.Engine.TurnLimit.IsOver(), name + ": is_over");
                Assert.AreEqual(c["expected_progress"].Value<double>(), f.Engine.TurnLimit.GetProgress(), 1e-9, name + ": progress");

                if (c["reset_to"] != null)
                {
                    f.Engine.TurnLimit.Reset(c["reset_to"].Value<int>());
                    Assert.AreEqual(c["expected_after_reset_remaining"].Value<int>(), f.Engine.TurnLimit.GetRemaining(), name + ": reset remaining");
                    Assert.AreEqual(c["expected_after_reset_over"].Value<bool>(), f.Engine.TurnLimit.IsOver(), name + ": reset over");
                }
            }
        }

        [Test]
        public void TurnTick_ReachesLimit()
        {
            var fx = SamsaraFixture.Fixture("samsara_levels");
            var c = (JObject)fx["turn_tick_case"];
            using var f = new SamsaraFixture();
            f.Set("turn_limit", c["limit"].Value<int>());
            f.Set("current_turn", c["current"].Value<int>());

            var over = f.Engine.TurnLimit.Tick();
            Assert.AreEqual(c["expected_after_tick_over"].Value<bool>(), over, "tick 后是否超限");
            Assert.AreEqual(c["expected_after_tick_current"].Value<int>(), f.Engine.State.GetCurrentTurn(), "tick 后 current_turn");
            Assert.AreEqual(c["expected_after_tick_remaining"].Value<int>(), f.Engine.TurnLimit.GetRemaining(), "tick 后 remaining");
        }
    }
}
