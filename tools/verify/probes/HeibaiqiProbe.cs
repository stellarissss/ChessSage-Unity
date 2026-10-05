using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>黑白棋规则引擎黄金等价性探针（与 HeibaiqiGoldenTests 同一份夹具）。</summary>
    public static class HeibaiqiProbe
    {
        public static void Run()
        {
            var board = ProbeEnv.LoadConfig("heibaiqi", "board");
            var piecesBlack = ProbeEnv.LoadConfig("heibaiqi", "pieces_black");
            var piecesWhite = ProbeEnv.LoadConfig("heibaiqi", "pieces_white");
            var rules = ProbeEnv.LoadConfig("heibaiqi", "rules");
            var bs = ProbeEnv.LoadConfig("heibaiqi", "board_state");

            var engine = new HeibaiqiRuleEngine(board, piecesBlack, piecesWhite, rules);
            ProbeEnv.Equal(true, engine.IsPlacementGame, "IsPlacementGame");

            var state = BoardState.FromJson(bs);
            state.Board = board;

            var golden = ProbeEnv.LoadFixture("heibaiqi_golden");
            var steps = (JArray)golden["steps"];

            foreach (var token in steps)
            {
                var step = (JObject)token;
                var label = step["label"]?.Value<string>() ?? "step";

                ProbeEnv.Equal(step["current_turn"]?.Value<string>(), state.CurrentTurn, $"{label}: current_turn");

                var placements = (JObject)step["placements"];
                ProbeEnv.EqualList(ProbeEnv.Normalize((JArray)placements["black"]),
                    ProbeEnv.Normalize(engine.GetValidPlacements(state, "black")), $"{label}: placements black");
                ProbeEnv.EqualList(ProbeEnv.Normalize((JArray)placements["white"]),
                    ProbeEnv.Normalize(engine.GetValidPlacements(state, "white")), $"{label}: placements white");
                ProbeEnv.EqualList(ProbeEnv.Normalize((JArray)placements[state.CurrentTurn]),
                    ProbeEnv.Normalize(ActionMoves(engine.GetAllActions(state, state.CurrentTurn))), $"{label}: actions");

                var outcome = engine.CheckOutcome(state);
                ProbeEnv.Equal(step["game_over"]?.Value<bool>(), outcome.Ended, $"{label}: game_over");
                ProbeEnv.Equal(step["winner"]?.Value<string>(), outcome.Winner, $"{label}: winner");
                ProbeEnv.Equal(step["reason"]?.Value<string>(), outcome.Condition, $"{label}: reason");

                var move = step["move"] as JObject;
                if (move == null) break;

                var side = move["side"]?.Value<string>();
                var to = (JArray)move["to"];
                var action = new GameAction("place", null, to[0].Value<int>(), to[1].Value<int>());
                var applied = engine.ApplyAction(state, action, side);
                ProbeEnv.Check(applied.Ok, $"{label}: 落子应成功");

                ProbeEnv.EqualList(SortedStrings((JArray)move["flipped"]), SortedStrings(applied.FlippedIds), $"{label}: flipped");
                ProbeEnv.EqualList(SnapshotFromJson((JObject)move["board_after"]), Snapshot(state), $"{label}: board_after");
                ProbeEnv.Equal(move["placed_id"]?.Value<string>(), state.GetPieceAt(action.X, action.Y)?.Id, $"{label}: placed_id");

                var nextTurn = step["next_turn"]?.Value<string>();
                if (nextTurn != null) state.CurrentTurn = nextTurn;
            }
        }

        static List<int[]> ActionMoves(List<GameAction> actions)
        {
            var list = new List<int[]>();
            foreach (var a in actions) list.Add(new[] { a.X, a.Y });
            return list;
        }

        static List<string> SortedStrings(JArray arr)
        {
            var list = new List<string>();
            foreach (var t in arr) list.Add(t.Value<string>());
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