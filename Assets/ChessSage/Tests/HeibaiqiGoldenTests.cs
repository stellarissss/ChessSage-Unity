using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 黑白棋规则引擎等价性测试：以 legacy-web/heibaiqi 的 Python 引擎生成的黄金数据为准，
    /// 逐步比较合法落子、翻转结果、棋盘快照与胜负判定。
    /// </summary>
    public sealed class HeibaiqiGoldenTests
    {
        HeibaiqiRuleEngine _engine;
        BoardState _state;

        [SetUp]
        public void SetUp()
        {
            var board = TestEnv.LoadConfig("heibaiqi", "board");
            var piecesBlack = TestEnv.LoadConfig("heibaiqi", "pieces_black");
            var piecesWhite = TestEnv.LoadConfig("heibaiqi", "pieces_white");
            var rules = TestEnv.LoadConfig("heibaiqi", "rules");
            var bs = TestEnv.LoadConfig("heibaiqi", "board_state");
            _engine = new HeibaiqiRuleEngine(board, piecesBlack, piecesWhite, rules);
            _state = BoardState.FromJson(bs);
            _state.Board = board;
        }

        [Test]
        public void PlacementsFlipsAndOutcomeMatchPythonGolden()
        {
            Assert.IsTrue(_engine.IsPlacementGame, "IsPlacementGame");

            var golden = TestEnv.LoadFixture("heibaiqi_golden");
            var steps = (JArray)golden["steps"];

            foreach (var token in steps)
            {
                var step = (JObject)token;
                var label = step["label"]?.Value<string>() ?? "step";

                Assert.AreEqual(step["current_turn"]?.Value<string>(), _state.CurrentTurn, $"{label}: current_turn");

                var placements = (JObject)step["placements"];
                Assert.AreEqual(Normalize((JArray)placements["black"]), Normalize(_engine.GetValidPlacements(_state, "black")), $"{label}: placements black");
                Assert.AreEqual(Normalize((JArray)placements["white"]), Normalize(_engine.GetValidPlacements(_state, "white")), $"{label}: placements white");
                Assert.AreEqual(Normalize((JArray)placements[_state.CurrentTurn]), Normalize(ActionMoves(_engine.GetAllActions(_state, _state.CurrentTurn))), $"{label}: actions");

                var outcome = _engine.CheckOutcome(_state);
                Assert.AreEqual(step["game_over"]?.Value<bool>(), outcome.Ended, $"{label}: game_over");
                Assert.AreEqual(step["winner"]?.Value<string>(), outcome.Winner, $"{label}: winner");
                Assert.AreEqual(step["reason"]?.Value<string>(), outcome.Condition, $"{label}: reason");

                var move = step["move"] as JObject;
                if (move == null) break;

                var side = move["side"]?.Value<string>();
                var to = (JArray)move["to"];
                var action = new GameAction("place", null, to[0].Value<int>(), to[1].Value<int>());
                var applied = _engine.ApplyAction(_state, action, side);
                Assert.IsTrue(applied.Ok, $"{label}: apply");

                Assert.AreEqual(SortedStrings((JArray)move["flipped"]), SortedStrings(applied.FlippedIds), $"{label}: flipped");
                Assert.AreEqual(SnapshotFromJson((JObject)move["board_after"]), Snapshot(_state), $"{label}: board_after");
                Assert.AreEqual(move["placed_id"]?.Value<string>(), _state.GetPieceAt(action.X, action.Y)?.Id, $"{label}: placed_id");

                var nextTurn = step["next_turn"]?.Value<string>();
                if (nextTurn != null) _state.CurrentTurn = nextTurn;
            }
        }

        static List<int[]> ActionMoves(List<GameAction> actions)
        {
            var list = new List<int[]>();
            foreach (var a in actions) list.Add(new[] { a.X, a.Y });
            return list;
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
            foreach (var m in moves)
                if (m is JArray a) list.Add(a[0].Value<int>() + "," + a[1].Value<int>());
            list.Sort();
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