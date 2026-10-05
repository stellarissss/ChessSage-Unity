using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 规则引擎等价性测试：以 legacy-web/xiangqi 的 Python 引擎生成的黄金数据为准，
    /// 逐局面比较 XML 合法移动 / 将军 / 将死 / 将帅被吃。
    /// </summary>
    public sealed class RuleEngineGoldenTests
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

        XiangqiRuleEngine _engine;
        BoardState _state;

        [SetUp]
        public void SetUp()
        {
            var board = TestEnv.LoadConfig("xiangqi", "board");
            var red = TestEnv.LoadConfig("xiangqi", "pieces_red");
            var black = TestEnv.LoadConfig("xiangqi", "pieces_black");
            var rules = TestEnv.LoadConfig("xiangqi", "rules");
            var bs = TestEnv.LoadConfig("xiangqi", "board_state");
            _engine = new XiangqiRuleEngine(board, red, black, rules);
            _state = BoardState.FromJson(bs);
            _state.Board = board;
        }

        [Test]
        public void InitialAndSequenceMatchPythonGolden()
        {
            var golden = TestEnv.LoadFixture("xiangqi_golden");
            var steps = (JArray)golden["steps"];

            for (int i = 0; i < steps.Count; i++)
            {
                var step = (JObject)steps[i];
                var expectedState = (JObject)step["state"];
                var label = step["label"]?.Value<string>() ?? $"step{i}";

                Assert.AreEqual(expectedState["current_turn"]?.Value<string>(), _state.CurrentTurn, $"{label}: current_turn");
                Assert.AreEqual(expectedState["check_red"]?.Value<bool>(), _engine.IsInCheck("red", _state), $"{label}: check_red");
                Assert.AreEqual(expectedState["check_black"]?.Value<bool>(), _engine.IsInCheck("black", _state), $"{label}: check_black");
                Assert.AreEqual(expectedState["checkmate_red"]?.Value<bool>(), _engine.IsCheckmate("red", _state), $"{label}: checkmate_red");
                Assert.AreEqual(expectedState["checkmate_black"]?.Value<bool>(), _engine.IsCheckmate("black", _state), $"{label}: checkmate_black");
                Assert.AreEqual(expectedState["general_captured"]?.Value<string>(), _engine.IsGeneralCaptured(_state), $"{label}: general_captured");

                var expectedMoves = (JObject)expectedState["moves"];
                foreach (var prop in expectedMoves.Properties())
                {
                    var piece = _state.GetPieceById(prop.Name);
                    Assert.IsNotNull(piece, $"{label}: piece {prop.Name} should exist");
                    var actual = Normalize(_engine.GetValidMoves(piece, _state));
                    var expected = Normalize((JArray)prop.Value);
                    Assert.AreEqual(expected, actual, $"{label}: moves for {prop.Name}");
                }

                if (i < Sequence.Length)
                {
                    var (pid, x, y) = Sequence[i];
                    ApplyMove(pid, x, y);
                }
            }
        }

        void ApplyMove(string pieceId, int x, int y)
        {
            var piece = _state.GetPieceById(pieceId);
            var target = _engine.GetPieceAt(x, y, _state);
            piece.X = x; piece.Y = y;
            if (target != null && !_engine.IsInvulnerable(target)) target.IsAlive = false;
            _state.CurrentTurn = BoardState.Opposite(_state.CurrentTurn);
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
    }
}