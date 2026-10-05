using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>五子棋规则引擎黄金等价性探针（与 Unity 的 WuziqiGoldenTests 同一份夹具）。</summary>
    public static class WuziqiProbe
    {
        public static void Run()
        {
            var board = ProbeEnv.LoadConfig("wuziqi", "board");
            var piecesBlack = ProbeEnv.LoadConfig("wuziqi", "pieces_black");
            var piecesWhite = ProbeEnv.LoadConfig("wuziqi", "pieces_red");
            var rules = ProbeEnv.LoadConfig("wuziqi", "rules");
            var initial = ProbeEnv.LoadConfig("wuziqi", "board_state");
            var golden = ProbeEnv.LoadFixture("wuziqi_golden");

            var engine = new WuziqiRuleEngine(board, piecesBlack, piecesWhite, rules);
            ProbeEnv.Check(engine.IsPlacementGame, "五子棋应为落子类游戏");

            foreach (var scenToken in (JArray)golden["scenarios"])
            {
                var scen = (JObject)scenToken;
                var name = scen["name"].Value<string>();
                var moves = (JArray)scen["moves"];
                var steps = (JArray)scen["steps"];

                ProbeEnv.Equal(moves.Count + 1, steps.Count, $"{name}: steps 数量");

                var state = BoardState.FromJson(initial);
                state.Board = board;

                for (int i = 0; i < steps.Count; i++)
                {
                    var step = (JObject)steps[i];
                    var label = $"{name}/{step["label"].Value<string>()}";

                    ProbeEnv.Equal(step["current_turn"]?.Value<string>(), state.CurrentTurn, label + ": current_turn");
                    ProbeEnv.EqualList(
                        ProbeEnv.Normalize((JArray)step["placements"]),
                        ProbeEnv.Normalize(engine.GetValidPlacements(state, state.CurrentTurn)),
                        label + ": placements");
                    CompareOutcome(label, (JObject)step["outcome"], engine.CheckOutcome(state));

                    if (i == 0)
                    {
                        var actions = engine.GetAllActions(state, state.CurrentTurn);
                        ProbeEnv.Equal(((JArray)step["placements"]).Count, actions.Count, label + ": GetAllActions 数量");
                        foreach (var a in actions) ProbeEnv.Equal("place", a.Kind, label + ": action kind");
                    }

                    if (i < moves.Count)
                    {
                        var mv = (JArray)moves[i];
                        int x = mv[0].Value<int>(), y = mv[1].Value<int>();
                        var ao = engine.ApplyAction(state, new GameAction("place", null, x, y), state.CurrentTurn);
                        ProbeEnv.Check(ao.Ok, label + ": ApplyAction 应成功");

                        var placed = state.Pieces[state.Pieces.Count - 1];
                        var expectedPiece = (JObject)steps[i + 1]["last_piece"];
                        ProbeEnv.Check(JToken.DeepEquals(expectedPiece, placed.ToJson()),
                            label + $": 落子结构不符 {placed.ToJson().ToString(Newtonsoft.Json.Formatting.None)}");

                        if (!engine.CheckOutcome(state).Ended)
                            state.CurrentTurn = state.CurrentTurn == "black" ? "white" : "black";
                    }
                }
            }

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
                state.Board = board;
                CompareOutcome(name, (JObject)c["outcome"], engine.CheckOutcome(state));
            }
        }

        static void CompareOutcome(string label, JObject expected, GameOutcome actual)
        {
            ProbeEnv.Equal(expected["ended"].Value<bool>(), actual.Ended, label + ": ended");
            ProbeEnv.Equal(expected["winner"]?.Value<string>(), actual.Winner, label + ": winner");
            ProbeEnv.Equal(expected["condition"]?.Value<string>(), actual.Condition, label + ": condition");
            ProbeEnv.Equal(expected["draw"].Value<bool>(), actual.Draw, label + ": draw");
        }
    }
}