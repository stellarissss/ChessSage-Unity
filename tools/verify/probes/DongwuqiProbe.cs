using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>动物棋规则引擎黄金等价性探针（与 Unity 的 DongwuqiGoldenTests 同一份夹具）。</summary>
    public static class DongwuqiProbe
    {
        public static void Run()
        {
            var board = ProbeEnv.LoadConfig("dongwuqi", "board");
            var red = ProbeEnv.LoadConfig("dongwuqi", "pieces_red");
            var black = ProbeEnv.LoadConfig("dongwuqi", "pieces_black");
            var rules = ProbeEnv.LoadConfig("dongwuqi", "rules");

            var engine = new DongwuqiRuleEngine(board, red, black, rules);
            var golden = ProbeEnv.LoadFixture("dongwuqi_golden");
            var scenarios = (JArray)golden["scenarios"];

            foreach (var st in scenarios)
            {
                var sc = (JObject)st;
                var scLabel = sc["label"]?.Value<string>() ?? "scenario";
                var state = BoardState.FromJson((JObject)sc["board_state"]);
                state.Board = board;
                var steps = (JArray)sc["steps"];

                for (int i = 0; i < steps.Count; i++)
                {
                    var step = (JObject)steps[i];
                    var label = step["label"]?.Value<string>() ?? $"{scLabel}#{i}";

                    ProbeEnv.Equal(step["current_turn"]?.Value<string>(), state.CurrentTurn, $"{label}: current_turn");
                    ProbeEnv.Equal(step["winner"]?.Value<string>(), engine.CheckOutcome(state).Winner, $"{label}: winner");
                    ProbeEnv.EqualList(ExpectedAlive(step), ActualAlive(state), $"{label}: alive");
                    ProbeEnv.EqualList(ExpectedTraps(step), ActualTraps(state), $"{label}: consumed_traps");

                    var moves = (JObject)step["moves"];
                    foreach (var prop in moves.Properties())
                    {
                        var piece = state.GetPieceById(prop.Name);
                        ProbeEnv.Check(piece != null, $"{label}: 缺少棋子 {prop.Name}");
                        ProbeEnv.EqualList(
                            ProbeEnv.Normalize((JArray)prop.Value),
                            ProbeEnv.Normalize(engine.GetValidMoves(piece, state)),
                            $"{label}: moves for {prop.Name}");
                    }

                    if (i + 1 >= steps.Count) continue;
                    var mv = (JObject)steps[i + 1]["move"];
                    if (mv == null) continue;

                    var pid = mv["piece_id"]?.Value<string>();
                    var to = (JArray)mv["to"];
                    var moving = state.GetPieceById(pid);
                    var outcome = engine.ApplyAction(
                        state, new GameAction("move", pid, to[0].Value<int>(), to[1].Value<int>()), moving?.Side);
                    ProbeEnv.Check(outcome.Ok, $"{label}: 应用走子失败 {outcome.Message}");

                    if (engine.CheckOutcome(state).Winner == null)
                        state.CurrentTurn = BoardState.Opposite(state.CurrentTurn);
                }
            }
        }

        static List<string> ExpectedAlive(JObject step)
        {
            var list = new List<string>();
            if (step["alive"] is JArray arr)
                foreach (var t in arr) list.Add(t.Value<string>());
            list.Sort();
            return list;
        }

        static List<string> ActualAlive(BoardState state)
        {
            var list = new List<string>();
            foreach (var p in state.Pieces)
                if (p.IsAlive && p.Type != "trap") list.Add(p.Id);
            list.Sort();
            return list;
        }

        static List<string> ExpectedTraps(JObject step)
        {
            var list = new List<string>();
            if (step["consumed_traps"] is JArray arr)
                foreach (var t in arr)
                    if (t is JArray a) list.Add(a[0].Value<int>() + "," + a[1].Value<int>());
            list.Sort();
            return list;
        }

        static List<string> ActualTraps(BoardState state)
        {
            var list = new List<string>();
            if (state.Extra["consumed_traps"] is JArray arr)
                foreach (var t in arr)
                    if (t is JArray a) list.Add(a[0].Value<int>() + "," + a[1].Value<int>());
            list.Sort();
            return list;
        }
    }
}