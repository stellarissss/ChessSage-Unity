using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 中国跳棋规则引擎等价性测试：以 legacy-web/tiaoqi 的 Python 引擎生成的黄金数据为准，
    /// 逐局面比较 step/hop（含连跳）合法移动集合与胜负判定。
    /// </summary>
    public sealed class TiaoqiGoldenTests
    {
        TiaoqiRuleEngine _engine;
        JObject _golden;

        [SetUp]
        public void SetUp()
        {
            var board = TestEnv.LoadConfig("tiaoqi", "board");
            var red = TestEnv.LoadConfig("tiaoqi", "pieces_red");
            var black = TestEnv.LoadConfig("tiaoqi", "pieces_black");
            var rules = TestEnv.LoadConfig("tiaoqi", "rules");
            _engine = new TiaoqiRuleEngine(board, red, black, rules);
            _golden = TestEnv.LoadFixture("tiaoqi_golden");
        }

        [Test]
        public void ScenariosMatchPythonGolden()
        {
            foreach (var scenarioToken in (JArray)_golden["scenarios"])
            {
                var scenario = (JObject)scenarioToken;
                var steps = (JArray)scenario["steps"];
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = (JObject)steps[i];
                    var label = scenario["name"]?.Value<string>() + "/" + (step["label"]?.Value<string>() ?? $"step{i}");
                    var state = BuildState(step);

                    var expectedMoves = (JObject)step["moves"];
                    foreach (var prop in expectedMoves.Properties())
                    {
                        var piece = state.GetPieceById(prop.Name);
                        Assert.IsNotNull(piece, $"{label}: 缺少棋子 {prop.Name}");
                        Assert.AreEqual(Normalize((JArray)prop.Value), Normalize(_engine.GetValidMoves(piece, state)),
                            $"{label}: moves for {prop.Name}");
                    }

                    var outcome = _engine.CheckOutcome(state);
                    var winner = step["winner"]?.Value<string>();
                    if (winner != null)
                    {
                        Assert.IsTrue(outcome.Ended, $"{label}: ended");
                        Assert.AreEqual(winner, outcome.Winner, $"{label}: winner");
                        Assert.AreEqual("all_in_camp", outcome.Condition, $"{label}: condition");
                    }
                    else
                    {
                        var turn = step["current_turn"]?.Value<string>();
                        bool hasMoves = step["side_has_moves"][turn]?.Value<bool>() ?? false;
                        if (!hasMoves)
                        {
                            Assert.IsTrue(outcome.Ended, $"{label}: ended(stalemate)");
                            Assert.AreEqual(turn == "red" ? "black" : "red", outcome.Winner, $"{label}: stalemate winner");
                            Assert.AreEqual("stalemate", outcome.Condition, $"{label}: condition");
                        }
                        else
                        {
                            Assert.IsFalse(outcome.Ended, $"{label}: ongoing");
                        }
                    }

                    if (i == 0)
                    {
                        var actions = _engine.GetAllActions(state, state.CurrentTurn);
                        int expectedActions = 0;
                        foreach (var p in state.Pieces)
                            if (p.IsAlive && p.Side == state.CurrentTurn && expectedMoves[p.Id] is JArray a)
                                expectedActions += a.Count;
                        Assert.AreEqual(expectedActions, actions.Count, $"{label}: GetAllActions 数量");

                        if (actions.Count > 0)
                        {
                            var clone = state.Clone();
                            int aliveBefore = clone.Pieces.Count;
                            var res = _engine.ApplyAction(clone, actions[0], clone.CurrentTurn);
                            Assert.IsTrue(res.Ok, $"{label}: ApplyAction 应成功");
                            Assert.AreEqual(actions[0].X, clone.GetPieceById(actions[0].PieceId).X, $"{label}: ApplyAction X");
                            Assert.AreEqual(actions[0].Y, clone.GetPieceById(actions[0].PieceId).Y, $"{label}: ApplyAction Y");
                            Assert.AreEqual(aliveBefore, clone.Pieces.Count, $"{label}: 跳棋不吃子");
                        }
                    }
                }
            }
        }

        static BoardState BuildState(JObject step)
        {
            var o = new JObject
            {
                ["current_turn"] = step["current_turn"]?.Value<string>() ?? "red",
            };
            var arr = new JArray();
            foreach (var t in (JArray)step["pieces"])
            {
                var p = (JObject)t;
                arr.Add(new JObject
                {
                    ["id"] = p["id"]?.Value<string>(),
                    ["type"] = p["type"]?.Value<string>(),
                    ["side"] = p["side"]?.Value<string>(),
                    ["position"] = new JArray(p["position"][0].Value<int>(), p["position"][1].Value<int>()),
                    ["is_alive"] = p["is_alive"]?.Value<bool>() ?? true,
                });
            }
            o["pieces"] = arr;
            return BoardState.FromJson(o);
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