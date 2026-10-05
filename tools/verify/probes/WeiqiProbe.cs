using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>围棋规则引擎黄金等价性探针（与 WeiqiGoldenTests 同一份夹具）。</summary>
    public static class WeiqiProbe
    {
        public static void Run()
        {
            var board = ProbeEnv.LoadConfig("weiqi", "board");
            var piecesBlack = ProbeEnv.LoadConfig("weiqi", "pieces_black");
            var piecesRed = ProbeEnv.LoadConfig("weiqi", "pieces_red");
            var baseRules = ProbeEnv.LoadConfig("weiqi", "rules");
            var initial = ProbeEnv.LoadConfig("weiqi", "board_state");
            var golden = ProbeEnv.LoadFixture("weiqi_golden");

            ProbeEnv.Check(new WeiqiRuleEngine(board, piecesBlack, piecesRed, baseRules).IsPlacementGame,
                "围棋应为落子类游戏");

            RunScenarios(board, piecesBlack, piecesRed, baseRules, initial, (JArray)golden["scenarios"]);
            RunCases(board, piecesBlack, piecesRed, baseRules, (JArray)golden["cases"]);
            RunLibertyCases(board, piecesBlack, piecesRed, baseRules, (JArray)golden["liberty_cases"]);
        }

        // ── 场景回放：合法落点 / 回合 / 胜负 / 提子 / 打劫标识 / 棋盘快照 ──
        static void RunScenarios(JObject board, JObject piecesBlack, JObject piecesRed,
            JObject baseRules, JObject initial, JArray scenarios)
        {
            foreach (var scenToken in scenarios)
            {
                var scen = (JObject)scenToken;
                var name = scen["name"].Value<string>();
                var rules = DeepMerge(baseRules, scen["rules_override"] as JObject);
                var engine = new WeiqiRuleEngine(board, piecesBlack, piecesRed, rules);

                var state = BoardState.FromJson(initial);
                state.Board = board;

                foreach (var stepToken in (JArray)scen["steps"])
                {
                    var step = (JObject)stepToken;
                    var label = $"{name}/{step["label"].Value<string>()}";

                    ProbeEnv.Equal(step["current_turn"]?.Value<string>(), state.CurrentTurn, label + ": current_turn");

                    var placements = (JObject)step["placements"];
                    ProbeEnv.EqualList(ProbeEnv.Normalize((JArray)placements["black"]),
                        ProbeEnv.Normalize(engine.GetValidPlacements(state, "black")), label + ": placements black");
                    ProbeEnv.EqualList(ProbeEnv.Normalize((JArray)placements["white"]),
                        ProbeEnv.Normalize(engine.GetValidPlacements(state, "white")), label + ": placements white");
                    ProbeEnv.Equal(((JArray)placements[state.CurrentTurn]).Count,
                        engine.GetAllActions(state, state.CurrentTurn).Count, label + ": GetAllActions 数量");

                    var oc = engine.CheckOutcome(state);
                    ProbeEnv.Equal(step["game_over"]?.Value<bool>(), oc.Ended, label + ": game_over");
                    ProbeEnv.Equal(step["winner"]?.Value<string>(), oc.Winner, label + ": winner");
                    ProbeEnv.Equal(step["reason"]?.Value<string>(), oc.Condition, label + ": reason");

                    var move = step["move"] as JObject;
                    if (move == null) break;

                    var side = move["side"].Value<string>();
                    var to = (JArray)move["to"];
                    var action = new GameAction("place", null, to[0].Value<int>(), to[1].Value<int>());
                    var applied = engine.ApplyAction(state, action, side);
                    ProbeEnv.Check(applied.Ok, label + ": 落子应成功");

                    ProbeEnv.Equal(move["placed_id"]?.Value<string>(), state.GetPieceAt(action.X, action.Y)?.Id,
                        label + ": placed_id");
                    ProbeEnv.EqualList(SortedStrings((JArray)move["captured"]), SortedStrings(applied.CapturedIds),
                        label + ": captured");
                    ProbeEnv.EqualList(SnapshotFromJson((JObject)move["board_after"]), Snapshot(state),
                        label + ": board_after");
                    ProbeEnv.Equal(move["captures"]["black"].Value<int>(), CaptureCount(state, "black"), label + ": captures black");
                    ProbeEnv.Equal(move["captures"]["white"].Value<int>(), CaptureCount(state, "white"), label + ": captures white");
                    ProbeEnv.Equal(move["ko_state"]?.Value<string>(), KoState(state), label + ": ko_state");

                    var nextTurn = step["next_turn"]?.Value<string>();
                    if (nextTurn != null) state.CurrentTurn = nextTurn;
                }
            }
        }

        // ── 构造局面的胜负判定 ──
        static void RunCases(JObject board, JObject piecesBlack, JObject piecesRed,
            JObject baseRules, JArray cases)
        {
            var engine = new WeiqiRuleEngine(board, piecesBlack, piecesRed, baseRules);
            foreach (var caseToken in cases)
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
                state.Board = board;

                var oc = engine.CheckOutcome(state);
                var expected = (JObject)c["outcome"];
                ProbeEnv.Equal(expected["ended"].Value<bool>(), oc.Ended, name + ": ended");
                ProbeEnv.Equal(expected["winner"]?.Value<string>(), oc.Winner, name + ": winner");
                ProbeEnv.Equal(expected["condition"]?.Value<string>(), oc.Condition, name + ": condition");
            }
        }

        // ── 气的计算（含限气 liberty_cap 机制原语）──
        static void RunLibertyCases(JObject board, JObject piecesBlack, JObject piecesRed,
            JObject baseRules, JArray cases)
        {
            foreach (var caseToken in cases)
            {
                var c = (JObject)caseToken;
                var name = c["name"].Value<string>();
                var rules = DeepMerge(baseRules, c["rules_override"] as JObject);
                var engine = new WeiqiRuleEngine(board, piecesBlack, piecesRed, rules);

                var bs = new JObject { ["pieces"] = c["pieces"].DeepClone() };
                var state = BoardState.FromJson(bs);
                state.Board = board;

                var at = (JArray)c["at"];
                ProbeEnv.Equal(c["liberties"].Value<int>(),
                    engine.CalculateLiberties(state, at[0].Value<int>(), at[1].Value<int>()), name + ": liberties");
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