using System;
using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>中国跳棋规则引擎黄金等价性探针（与 Unity 的 TiaoqiGoldenTests 同一份夹具）。</summary>
    public static class TiaoqiProbe
    {
        public static void Run()
        {
            var board = ProbeEnv.LoadConfig("tiaoqi", "board");
            var red = ProbeEnv.LoadConfig("tiaoqi", "pieces_red");
            var black = ProbeEnv.LoadConfig("tiaoqi", "pieces_black");
            var rules = ProbeEnv.LoadConfig("tiaoqi", "rules");

            var engine = new TiaoqiRuleEngine(board, red, black, rules);
            var golden = ProbeEnv.LoadFixture("tiaoqi_golden");

            foreach (var scenarioToken in (JArray)golden["scenarios"])
            {
                var scenario = (JObject)scenarioToken;
                var steps = (JArray)scenario["steps"];
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = (JObject)steps[i];
                    var label = scenario["name"]?.Value<string>() + "/" + (step["label"]?.Value<string>() ?? $"step{i}");
                    var state = BuildState(step);

                    // 每个棋子的合法移动集合
                    var expectedMoves = (JObject)step["moves"];
                    foreach (var prop in expectedMoves.Properties())
                    {
                        var piece = state.GetPieceById(prop.Name);
                        ProbeEnv.Check(piece != null, $"{label}: 缺少棋子 {prop.Name}");
                        ProbeEnv.EqualList(
                            ProbeEnv.Normalize((JArray)prop.Value),
                            ProbeEnv.Normalize(engine.GetValidMoves(piece, state)),
                            $"{label}: moves for {prop.Name}");
                    }

                    // 胜负：all_in_camp 优先，其次困毙当前方
                    var outcome = engine.CheckOutcome(state);
                    var winner = step["winner"]?.Value<string>();
                    if (winner != null)
                    {
                        ProbeEnv.Equal(true, outcome.Ended, $"{label}: ended");
                        ProbeEnv.Equal(winner, outcome.Winner, $"{label}: winner");
                        ProbeEnv.Equal("all_in_camp", outcome.Condition, $"{label}: condition");
                    }
                    else
                    {
                        var turn = step["current_turn"]?.Value<string>();
                        bool hasMoves = step["side_has_moves"][turn]?.Value<bool>() ?? false;
                        if (!hasMoves)
                        {
                            ProbeEnv.Equal(true, outcome.Ended, $"{label}: ended(stalemate)");
                            ProbeEnv.Equal(turn == "red" ? "black" : "red", outcome.Winner, $"{label}: stalemate winner");
                            ProbeEnv.Equal("stalemate", outcome.Condition, $"{label}: condition");
                        }
                        else
                        {
                            ProbeEnv.Equal(false, outcome.Ended, $"{label}: ongoing");
                        }
                    }

                    // 首个局面校验动作枚举与执行（跳棋不吃子）
                    if (i == 0)
                    {
                        var actions = engine.GetAllActions(state, state.CurrentTurn);
                        int expectedActions = 0;
                        foreach (var p in state.Pieces)
                            if (p.IsAlive && p.Side == state.CurrentTurn && expectedMoves[p.Id] is JArray a)
                                expectedActions += a.Count;
                        ProbeEnv.Equal(expectedActions, actions.Count, $"{label}: GetAllActions 数量");

                        if (actions.Count > 0)
                        {
                            var clone = state.Clone();
                            int aliveBefore = clone.Pieces.Count;
                            var res = engine.ApplyAction(clone, actions[0], clone.CurrentTurn);
                            ProbeEnv.Check(res.Ok, $"{label}: ApplyAction 应成功");
                            ProbeEnv.Equal(actions[0].X, clone.GetPieceById(actions[0].PieceId).X, $"{label}: ApplyAction X");
                            ProbeEnv.Equal(actions[0].Y, clone.GetPieceById(actions[0].PieceId).Y, $"{label}: ApplyAction Y");
                            ProbeEnv.Equal(aliveBefore, clone.Pieces.Count, $"{label}: 跳棋不吃子");
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
    }
}