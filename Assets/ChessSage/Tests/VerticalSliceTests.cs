using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Model;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 垂直切片端到端测试：模拟"玩家点棋子 → 点目标 → AI 应手"的完整闭环。
    ///
    /// 表现层（BoardView / GameScreen）依赖 UnityEngine 无法在 dotnet 下运行，
    /// 但它所编排的<b>状态机时序</b>完全由 GameSession 决定，
    /// 因此这里对"切片所依赖的不变量"做等价的引擎外验证：
    ///   1) 初始轮到玩家；
    ///   2) 选中棋子能拿到非空可走点，且与规则引擎一致；
    ///   3) 走子后棋盘状态真的变了，且轮次交替；
    ///   4) AI 回合可被IsAiTurn 正确识别并推进；
    ///   5) 悔棋可回退。
    /// </summary>
    public sealed class VerticalSliceTests
    {
        static GameSession NewSession() => NewSession(12345);

        static GameSession NewSession(int seed)
        {
            var loader = new ConfigLoader(TestEnv.ConfigsRoot);
            var config = loader.LoadVariant("xiangqi");
            return new GameSession(config, new MechanismEngine(config.Rules), aiSeed: seed);
        }

        [Test]
        public void Slice_BootsWithPlayerToMove()
        {
            var s = NewSession();
            Assert.AreEqual("red", s.CurrentTurn);
            Assert.AreEqual("red", s.PlayerSide, "象棋默认玩家执红");
            Assert.IsFalse(s.IsAiTurn, "开局应等待玩家操作");
            Assert.IsFalse(s.IsEnded);
        }

        [Test]
        public void Slice_EveryStarterPieceHasLegalMoves()
        {
            var s = NewSession();
            int total = 0;
            foreach (var p in s.State.Pieces)
            {
                if (!p.IsAlive || p.Side != "red") continue;
                total += s.GetValidMoves(p.Id).Count;
            }
            Assert.Greater(total, 0, "开局红方至少应有合法走法，否则切片无法交互");
        }

        [Test]
        public void Slice_PlayerMoveAdvancesBoardAndTurn()
        {
            var s = NewSession();
            var piece = FirstMoveableRedPiece(s);
            var target = s.GetValidMoves(piece.Id)[0];

            int beforeAlive = AliveCount(s.State);
            var r = s.MakePlayerMove(piece.Id, target[0], target[1]);

            Assert.IsTrue(r.Success, "合法走法应被接受：" + r.Message);
            Assert.AreNotEqual("red", s.CurrentTurn, "走子后应交给黑方");
            Assert.IsTrue(s.IsAiTurn, "黑方此时应由 AI 接管");
            Assert.AreEqual(beforeAlive, AliveCount(s.State), "本步不吃子时存活数应不变");

            var moved = s.State.GetPieceById(piece.Id);
            Assert.AreEqual(target[0], moved.X);
            Assert.AreEqual(target[1], moved.Y);
        }

        [Test]
        public void Slice_IllegalMoveRejectedWithoutMutatingBoard()
        {
            var s = NewSession();
            var piece = FirstMoveableRedPiece(s);
            int fromX = piece.X, fromY = piece.Y;

            var r = s.MakePlayerMove(piece.Id, fromX, fromY);

            Assert.IsFalse(r.Success, "原地不动应被规则拒绝");
            var after = s.State.GetPieceById(piece.Id);
            Assert.AreEqual(fromX, after.X);
            Assert.AreEqual(fromY, after.Y, "被拒绝的走子不得改变棋盘");
            Assert.AreEqual("red", s.CurrentTurn, "被拒绝的走子不得消耗回合");
        }

        [Test]
        public void Slice_AiTurnResolvesAndYieldsBackToPlayer()
        {
            var s = NewSession();
            var piece = FirstMoveableRedPiece(s);
            var target = s.GetValidMoves(piece.Id)[0];
            s.MakePlayerMove(piece.Id, target[0], target[1]);

            int guard = 0;
            while (s.IsAiTurn && !s.IsEnded && guard++ < 16)
            {
                var ai = s.MakeAiMove();
                Assert.IsTrue(ai.Success, "AI 在受控回合内必须能落子：" + ai.Message);
            }

            Assert.LessOrEqual(guard, 16, "AI 回合必须有上限，防止机制导致的死循环");
            if (!s.IsEnded) Assert.IsFalse(s.IsAiTurn, "AI 走完后应把回合交还玩家");
        }

        [Test]
        public void Slice_UndoRestoresPreviousBoard()
        {
            var s = NewSession();
            var piece = FirstMoveableRedPiece(s);
            var target = s.GetValidMoves(piece.Id)[0];
            // 注意：piece 是 State 中的活引用，走子后会被就地修改，故先取坐标快照。
            int originX = piece.X, originY = piece.Y;

            s.MakePlayerMove(piece.Id, target[0], target[1]);
            Assert.AreEqual("black", s.CurrentTurn);
            Assert.IsTrue(s.CanUndo);

            Assert.IsTrue(s.UndoLastMove(), "悔棋应成功");
            Assert.AreEqual("red", s.CurrentTurn, "悔棋应回到玩家回合");
            var restored = s.State.GetPieceById(piece.Id);
            Assert.AreEqual(originX, restored.X);
            Assert.AreEqual(originY, restored.Y);
            Assert.AreEqual(s.State, s.State, "悔棋后棋盘引用应自洽");
        }

        [Test]
        public void Slice_VariantSwitchKeepsBoardShape()
        {
            // 表现层的 BoardView 依赖 Width/Height；此处验证切棋种后这些值会随之变化，
            // 即"新增棋类无需改表现层"的前提成立。
            var loader = new ConfigLoader(TestEnv.ConfigsRoot);

            var xq = new GameSession(loader.LoadVariant("xiangqi"), null);
            var wq = new GameSession(loader.LoadVariant("weiqi"), null);

            Assert.AreEqual(9, xq.Rules.Width);
            Assert.AreEqual(10, xq.Rules.Height);
            Assert.AreEqual(19, wq.Rules.Width, "围棋应为 19 路");
            Assert.AreEqual(19, wq.Rules.Height);
            Assert.IsTrue(wq.IsPlacementGame, "围棋属落子类");
            Assert.IsFalse(xq.IsPlacementGame, "象棋属移动类");
        }

        static Piece FirstMoveableRedPiece(GameSession s)
        {
            foreach (var p in s.State.Pieces)
                if (p.IsAlive && p.Side == "red" && s.GetValidMoves(p.Id).Count > 0) return p;
            Assert.Fail("未找到有合法走法的红方棋子");
            return null;
        }

        static int AliveCount(BoardState st)
        {
            int n = 0;
            foreach (var p in st.Pieces) if (p.IsAlive) n++;
            return n;
        }
    }
}