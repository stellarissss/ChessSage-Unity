using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 五子棋规则引擎等价性测试：以 legacy-web/wuziqi 的 Python 引擎生成的黄金数据为准，
    /// 回放固定落子序列并逐局面比较合法落点 / 回合 / 胜负判定，另对构造局面校验胜负条件。
    /// </summary>
    public sealed class WuziqiGoldenTests
    {
        WuziqiRuleEngine _engine;
        JObject _board;

        [SetUp]
        public void SetUp()
        {
            _board = TestEnv.LoadConfig("wuziqi", "board");
            var piecesBlack = TestEnv.LoadConfig("wuziqi", "pieces_black");
            var piecesWhite = TestEnv.LoadConfig("wuziqi", "pieces_red");
            var rules = TestEnv.LoadConfig("wuziqi", "rules");
            _engine = new WuziqiRuleEngine(_board, piecesBlack, piecesWhite, rules);
        }

        [Test]
        public void ScenariosMatchPythonGolden()
        {
            var initial = TestEnv.LoadConfig("wuziqi", "board_state");
            var golden = TestEnv.LoadFixture("wuziqi_golden");

            Assert.IsTrue(_engine.IsPlacementGame, "五子棋应为落子类游戏");

            foreach (var scenToken in (JArray)golden["scenarios"])
            {
                var scen = (JObject)scenToken;
                var name = scen["name"].Value<string>();
                var moves = (JArray)scen["moves"];
                var steps = (JArray)scen["steps"];

                Assert.AreEqual(moves.Count + 1, steps.Count, $"{name}: steps 数量");

                var state = BoardState.FromJson(initial);
                state.Board = _board;

                for (int i = 0; i < steps.Count; i++)
                {
                    var step = (JObject)steps[i];
                    var label = $"{name}/{step["label"].Value<string>()}";

                    Assert.AreEqual(step["current_turn"]?.Value<string>(), state.CurrentTurn, $"{label}: current_turn");
                    CollectionAssert.AreEqual(
                        Normalize((JArray)step["placements"]),
                        Normalize(_engine.GetValidPlacements(state, state.CurrentTurn)),
                        $"{label}: placements");
                    AssertOutcome(label, (JObject)step["outcome"], _engine.CheckOutcome(state));

                    if (i == 0)
                    {
                        var actions = _engine.GetAllActions(state, state.CurrentTurn);
                        Assert.AreEqual(((JArray)step["placements"]).Count, actions.Count, $"{label}: GetAllActions 数量");
                    }

                    if (i < moves.Count)
                    {
                        var mv = (JArray)moves[i];
                        int x = mv[0].Value<int>(), y = mv[1].Value<int>();
                        var ao = _engine.ApplyAction(state, new GameAction("place", null, x, y), state.CurrentTurn);
                        Assert.IsTrue(ao.Ok, $"{label}: ApplyAction 应成功");

                        var placed = state.Pieces[state.Pieces.Count - 1];
                        var expectedPiece = (JObject)steps[i + 1]["last_piece"];
                        Assert.IsTrue(JToken.DeepEquals(expectedPiece, placed.ToJson()), $"{label}: 落子结构不符");

                        if (!_engine.CheckOutcome(state).Ended)
                            state.CurrentTurn = state.CurrentTurn == "black" ? "white" : "black";
                    }
                }
            }
        }

        [Test]
        public void OutcomeCasesMatchPythonGolden()
        {
            var golden = TestEnv.LoadFixture("wuziqi_golden");

            foreach (var caseToken in (JArray)golden["cases"])
            {
                var c = (JObject)caseToken;
                var name = c["name"].Value<string>();
                var bsObj = new JObject
                {
                    ["pieces"] = c["pieces"].DeepClone(),
                    ["current_turn"] = c["current_turn"].Value<string>(),
                };
                var state = BoardState.FromJson(bsObj);
                state.Board = _board;
                AssertOutcome(name, (JObject)c["outcome"], _engine.CheckOutcome(state));
            }
        }

        static void AssertOutcome(string label, JObject expected, GameOutcome actual)
        {
            Assert.AreEqual(expected["ended"].Value<bool>(), actual.Ended, $"{label}: ended");
            Assert.AreEqual(expected["winner"]?.Value<string>(), actual.Winner, $"{label}: winner");
            Assert.AreEqual(expected["condition"]?.Value<string>(), actual.Condition, $"{label}: condition");
            Assert.AreEqual(expected["draw"].Value<bool>(), actual.Draw, $"{label}: draw");
        }

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
    }
}