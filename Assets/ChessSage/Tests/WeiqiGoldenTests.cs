using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 围棋规则引擎等价性测试：以 legacy-web/weiqi 的 Python 引擎生成的黄金数据为准，
    /// 回放固定落子场景（提子、多子提、打劫、禁自杀、黑棋禁手、机制原语），
    /// 逐步比较合法落点 / 提子 / 打劫标识 / 棋盘快照 / 胜负判定，并校验构造局面与气的计算。
    /// </summary>
    public sealed class WeiqiGoldenTests
    {
        JObject _board;
        JObject _piecesBlack;
        JObject _piecesRed;
        JObject _rules;

        [SetUp]
        public void SetUp()
        {
            _board = TestEnv.LoadConfig("weiqi", "board");
            _piecesBlack = TestEnv.LoadConfig("weiqi", "pieces_black");
            _piecesRed = TestEnv.LoadConfig("weiqi", "pieces_red");
            _rules = TestEnv.LoadConfig("weiqi", "rules");
        }

        [Test]
        public void ScenariosMatchPythonGolden()
        {
            var initial = TestEnv.LoadConfig("weiqi", "board_state");
            var golden = TestEnv.LoadFixture("weiqi_golden");

            Assert.IsTrue(new WeiqiRuleEngine(_board, _piecesBlack, _piecesRed, _rules).IsPlacementGame,
                "围棋应为落子类游戏");

            foreach (var scenToken in (JArray)golden["scenarios"])
            {
                var scen = (JObject)scenToken;
                var name = scen["name"].Value<string>();
                var engine = new WeiqiRuleEngine(_board, _piecesBlack, _piecesRed,
                    DeepMerge(_rules, scen["rules_override"] as JObject));

                var state = BoardState.FromJson(initial);
                state.Board = _board;

                foreach (var stepToken in (JArray)scen["steps"])
                {
                    var step = (JObject)stepToken;
                    var label = $"{name}/{step["label"].Value<string>()}";

                    Assert.AreEqual(step["current_turn"]?.Value<string>(), state.CurrentTurn, $"{label}: current_turn");

                    var placements = (JObject)step["placements"];
                    CollectionAssert.AreEqual(Normalize((JArray)placements["black"]),
                        Normalize(engine.GetValidPlacements(state, "black")), $"{label}: placements black");
                    CollectionAssert.AreEqual(Normalize((JArray)placements["white"]),
                        Normalize(engine.GetValidPlacements(state, "white")), $"{label}: placements white");
                    Assert.AreEqual(((JArray)placements[state.CurrentTurn]).Count,
                        engine.GetAllActions(state, state.CurrentTurn).Count, $"{label}: GetAllActions 数量");

                    var oc = engine.CheckOutcome(state);
                    Assert.AreEqual(step["game_over"]?.Value<bool>(), oc.Ended, $"{label}: game_over");
                    Assert.AreEqual(step["winner"]?.Value<string>(), oc.Winner, $"{label}: winner");
                    Assert.AreEqual(step["reason"]?.Value<string>(), oc.Condition, $"{label}: reason");

                    var move = step["move"] as JObject;
                    if (move == null) break;

                    var side = move["side"].Value<string>();
                    var to = (JArray)move["to"];
                    var action = new GameAction("place", null, to[0].Value<int>(), to[1].Value<int>());
                    var applied = engine.ApplyAction(state, action, side);
                    Assert.IsTrue(applied.Ok, $"{label}: 落子应成功");

                    Assert.AreEqual(move["placed_id"]?.Value<string>(), state.GetPieceAt(action.X, action.Y)?.Id,
                        $"{label}: placed_id");
                    CollectionAssert.AreEqual(SortedStrings((JArray)move["captured"]), SortedStrings(applied.CapturedIds),
                        $"{label}: captured");
                    CollectionAssert.AreEqual(SnapshotFromJson((JObject)move["board_after"]), Snapshot(state),
                        $"{label}: board_after");
                    Assert.AreEqual(move["captures"]["black"].Value<int>(), CaptureCount(state, "black"), $"{label}: captures black");
                    Assert.AreEqual(move["captures"]["white"].Value<int>(), CaptureCount(state, "white"), $"{label}: captures white");
                    Assert.AreEqual(move["ko_state"]?.Value<string>(), KoState(state), $"{label}: ko_state");

                    var nextTurn = step["next_turn"]?.Value<string>();
                    if (nextTurn != null) state.CurrentTurn = nextTurn;
                }
            }
        }

        [Test]
        public void OutcomeCasesMatchPythonGolden()
        {
            var golden = TestEnv.LoadFixture("weiqi_golden");
            var engine = new WeiqiRuleEngine(_board, _piecesBlack, _piecesRed, _rules);

            foreach (var caseToken in (JArray)golden["cases"])
            {
                var c = (JObject)caseToken;
                var name = c["name"].Value<string>();
                var bs = new JObject
                {
                    ["pieces"] = c["pieces"].DeepClone(),
                    ["captures"] = c["captures"].DeepClone(),
                    ["current_turn"] = c["current_turn"].Value<string>(),
                };
                var state = BoardState.FromJson(bs);
                state.Board = _board;

                var oc = engine.CheckOutcome(state);
                var expected = (JObject)c["outcome"];
                Assert.AreEqual(expected["ended"].Value<bool>(), oc.Ended, $"{name}: ended");
                Assert.AreEqual(expected["winner"]?.Value<string>(), oc.Winner, $"{name}: winner");
                Assert.AreEqual(expected["condition"]?.Value<string>(), oc.Condition, $"{name}: condition");
            }
        }

        [Test]
        public void LibertiesMatchPythonGolden()
        {
            var golden = TestEnv.LoadFixture("weiqi_golden");

            foreach (var caseToken in (JArray)golden["liberty_cases"])
            {
                var c = (JObject)caseToken;
                var name = c["name"].Value<string>();
                var engine = new WeiqiRuleEngine(_board, _piecesBlack, _piecesRed,
                    DeepMerge(_rules, c["rules_override"] as JObject));

                var bs = new JObject { ["pieces"] = c["pieces"].DeepClone() };
                var state = BoardState.FromJson(bs);
                state.Board = _board;

                var at = (JArray)c["at"];
                Assert.AreEqual(c["liberties"].Value<int>(),
                    engine.CalculateLiberties(state, at[0].Value<int>(), at[1].Value<int>()), $"{name}: liberties");
            }
        }

        static JObject DeepMerge(JObject baseObj, JObject over)
        {
            var result = (JObject)baseObj.DeepClone();
            if (over == null) return result;
            foreach (var prop in over.Properties())
            {
                if (prop.Value is JObject ov && result[prop.Name] is JObject bv)
                    result[prop.Name] = DeepMerge(bv, ov);
                else
                    result[prop.Name] = prop.Value.DeepClone();
            }
            return result;
        }

        static int CaptureCount(BoardState state, string side)
            => ((state.Extra["captures"] as JObject)?[side]?.Value<int>()) ?? 0;

        static string KoState(BoardState state)
            => state.Extra["ko_state"]?.Type == JTokenType.String ? state.Extra["ko_state"].Value<string>() : null;

        static List<string> Normalize(IEnumerable<int[]> moves)
        {
            var list = new List<string>();
            foreach (var m in moves) list.Add(m[0] + "," + m[1]);
            list.Sort();
            return list;
        }

        static List<string> Normalize(JArray moves)
        {
            var list = new List<string>();
            if (moves != null)
                foreach (var m in moves)
                    if (m is JArray a) list.Add(a[0].Value<int>() + "," + a[1].Value<int>());
            list.Sort();
            return list;
        }

        static List<string> SortedStrings(JArray arr)
        {
            var list = new List<string>();
            if (arr != null) foreach (var t in arr) list.Add(t.Value<string>());
            list.Sort();
            return list;
        }

        static List<string> SortedStrings(List<string> items)
        {
            var list = new List<string>(items);
            list.Sort();
            return list;
        }

        static List<string> Snapshot(BoardState state)
        {
            var list = new List<string>();
            foreach (var p in state.Pieces)
                if (p.IsAlive) list.Add(p.X + "," + p.Y + ":" + p.Side);
            list.Sort();
            return list;
        }

        static List<string> SnapshotFromJson(JObject snapshot)
        {
            var list = new List<string>();
            foreach (var prop in snapshot.Properties()) list.Add(prop.Name + ":" + prop.Value.Value<string>());
            list.Sort();
            return list;
        }
    }
}