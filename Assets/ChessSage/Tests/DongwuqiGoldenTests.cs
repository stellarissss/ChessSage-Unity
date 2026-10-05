using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 动物棋规则引擎等价性测试：以 legacy-web/dongwuqi 的 Python 引擎生成的黄金数据为准，
    /// 逐场景比较合法移动 / 存活棋子 / 陷阱吞噬 / 胜负判定，并用 ApplyAction 复现走子序列。
    /// </summary>
    public sealed class DongwuqiGoldenTests
    {
        DongwuqiRuleEngine _engine;
        JObject _board;

        [SetUp]
        public void SetUp()
        {
            _board = TestEnv.LoadConfig("dongwuqi", "board");
            var red = TestEnv.LoadConfig("dongwuqi", "pieces_red");
            var black = TestEnv.LoadConfig("dongwuqi", "pieces_black");
            var rules = TestEnv.LoadConfig("dongwuqi", "rules");
            _engine = new DongwuqiRuleEngine(_board, red, black, rules);
        }

        [Test]
        public void ScenariosMatchPythonGolden()
        {
            var golden = TestEnv.LoadFixture("dongwuqi_golden");
            var scenarios = (JArray)golden["scenarios"];

            foreach (var st in scenarios)
            {
                var sc = (JObject)st;
                var scLabel = sc["label"]?.Value<string>() ?? "scenario";
                var state = BoardState.FromJson((JObject)sc["board_state"]);
                state.Board = _board;
                var steps = (JArray)sc["steps"];

                for (int i = 0; i < steps.Count; i++)
                {
                    var step = (JObject)steps[i];
                    var label = step["label"]?.Value<string>() ?? $"{scLabel}#{i}";

                    Assert.AreEqual(step["current_turn"]?.Value<string>(), state.CurrentTurn, $"{label}: current_turn");
                    Assert.AreEqual(step["winner"]?.Value<string>(), _engine.CheckOutcome(state).Winner, $"{label}: winner");
                    Assert.AreEqual(ExpectedAlive(step), ActualAlive(state), $"{label}: alive");
                    Assert.AreEqual(ExpectedTraps(step), ActualTraps(state), $"{label}: consumed_traps");

                    var moves = (JObject)step["moves"];
                    foreach (var prop in moves.Properties())
                    {
                        var piece = state.GetPieceById(prop.Name);
                        Assert.IsNotNull(piece, $"{label}: piece {prop.Name} should exist");
                        Assert.AreEqual(
                            Normalize((JArray)prop.Value),
                            Normalize(_engine.GetValidMoves(piece, state)),
                            $"{label}: moves for {prop.Name}");
                    }

                    if (i + 1 >= steps.Count) continue;
                    var mv = (JObject)steps[i + 1]["move"];
                    if (mv == null) continue;

                    var pid = mv["piece_id"]?.Value<string>();
                    var to = (JArray)mv["to"];
                    var moving = state.GetPieceById(pid);
                    var outcome = _engine.ApplyAction(
                        state, new GameAction("move", pid, to[0].Value<int>(), to[1].Value<int>()), moving?.Side);
                    Assert.IsTrue(outcome.Ok, $"{label}: apply failed {outcome.Message}");

                    if (_engine.CheckOutcome(state).Winner == null)
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