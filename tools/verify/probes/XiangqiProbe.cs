using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>象棋规则引擎黄金等价性探针（与 Unity 的 RuleEngineGoldenTests 同一份夹具）。</summary>
    public static class XiangqiProbe
    {
        static readonly (string pieceId, int x, int y)[] Sequence =
        {
            ("r_cannon_2", 4, 7),
            ("b_chariot_1", 0, 1),
            ("r_cannon_2", 4, 0),
            ("b_advisor_1", 4, 1),
            ("r_chariot_1", 0, 8),
            ("b_soldier_1", 0, 4),
        };

        public static void Run()
        {
            var board = ProbeEnv.LoadConfig("xiangqi", "board");
            var red = ProbeEnv.LoadConfig("xiangqi", "pieces_red");
            var black = ProbeEnv.LoadConfig("xiangqi", "pieces_black");
            var rules = ProbeEnv.LoadConfig("xiangqi", "rules");
            var bs = ProbeEnv.LoadConfig("xiangqi", "board_state");

            var engine = new XiangqiRuleEngine(board, red, black, rules);
            var state = BoardState.FromJson(bs);
            state.Board = board;

            var golden = ProbeEnv.LoadFixture("xiangqi_golden");
            var steps = (JArray)golden["steps"];

            for (int i = 0; i < steps.Count; i++)
            {
                var step = (JObject)steps[i];
                var expected = (JObject)step["state"];
                var label = step["label"]?.Value<string>() ?? $"step{i}";

                ProbeEnv.Equal(expected["current_turn"]?.Value<string>(), state.CurrentTurn, $"{label}: current_turn");
                ProbeEnv.Equal(expected["check_red"]?.Value<bool>(), engine.IsInCheck("red", state), $"{label}: check_red");
                ProbeEnv.Equal(expected["check_black"]?.Value<bool>(), engine.IsInCheck("black", state), $"{label}: check_black");
                ProbeEnv.Equal(expected["checkmate_red"]?.Value<bool>(), engine.IsCheckmate("red", state), $"{label}: checkmate_red");
                ProbeEnv.Equal(expected["checkmate_black"]?.Value<bool>(), engine.IsCheckmate("black", state), $"{label}: checkmate_black");
                ProbeEnv.Equal(expected["general_captured"]?.Value<string>(), engine.IsGeneralCaptured(state), $"{label}: general_captured");

                var expectedMoves = (JObject)expected["moves"];
                foreach (var prop in expectedMoves.Properties())
                {
                    var piece = state.GetPieceById(prop.Name);
                    ProbeEnv.Check(piece != null, $"{label}: 缺少棋子 {prop.Name}");
                    ProbeEnv.EqualList(
                        ProbeEnv.Normalize((JArray)prop.Value),
                        ProbeEnv.Normalize(engine.GetValidMoves(piece, state)),
                        $"{label}: moves for {prop.Name}");
                }

                if (i < Sequence.Length)
                {
                    var (pid, x, y) = Sequence[i];
                    var piece = state.GetPieceById(pid);
                    var target = engine.GetPieceAt(x, y, state);
                    piece.X = x; piece.Y = y;
                    if (target != null && !engine.IsInvulnerable(target)) target.IsAlive = false;
                    state.CurrentTurn = BoardState.Opposite(state.CurrentTurn);
                }
            }
        }
    }
}