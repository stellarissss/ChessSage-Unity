using System.Collections.Generic;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 落子类端到端（引擎外）：固化 M5-A「落子类全可玩」所依赖的 GameSession 契约。
    ///
    /// 表现层的落子分支（<c>GameScreen.OnPlacementCellClicked</c>）只是这些 Core 不变量的投影：
    ///   1) <see cref="GameSession.IsPlacementGame"/> 正确识别三大落子棋种；
    ///   2) 合法落点可落、已占点被拒且不消耗回合/不记历史；
    ///   3) 落子提子进入 <see cref="MoveResult.Captured"/>，黑白棋翻转进入 move_history；
    ///   4) AI 能应手且把回合交还玩家；
    ///   5) 悔棋可回退到落子前。
    /// </summary>
    public sealed class PlacementGameTests
    {
        static readonly string[] PlacementVariants = { "wuziqi", "heibaiqi", "weiqi" };

        static GameSession NewSession(string variant, int seed = 777)
        {
            var loader = new ConfigLoader(TestEnv.ConfigsRoot);
            var config = loader.LoadVariant(variant);
            return new GameSession(config, new MechanismEngine(config.Rules), aiSeed: seed);
        }

        [TestCase("wuziqi")]
        [TestCase("heibaiqi")]
        [TestCase("weiqi")]
        public void PlacementGame_PlayerPlaceAdvancesTurnAndAiAnswers(string variant)
        {
            var s = NewSession(variant);
            Assert.IsTrue(s.IsPlacementGame, variant + " 应为落子类");
            Assert.IsFalse(s.IsEnded);
            Assert.IsFalse(s.IsAiTurn, "开局应等待玩家");

            var valid = s.GetValidPlacements();
            Assert.Greater(valid.Count, 0, variant + " 开局应有合法落点");
            int historyBefore = s.State.MoveHistory.Count;

            var r = s.MakePlayerPlace(valid[0][0], valid[0][1]);
            Assert.IsTrue(r.Success, variant + " 合法落点应被接受：" + r.Message);
            Assert.AreEqual(historyBefore + 1, s.State.MoveHistory.Count, "落子应记入历史");
            Assert.AreNotEqual(s.PlayerSide, s.CurrentTurn, "落子后应换手");

            int guard = 0;
            while (s.IsAiTurn && !s.IsEnded && guard++ < 8)
                Assert.IsTrue(s.MakeAiMove().Success, variant + " AI 应手必须成功");
            Assert.Less(guard, 8, "AI 回合必须有上限");
            if (!s.IsEnded)
            {
                Assert.IsFalse(s.IsAiTurn, "AI 走完应交还玩家");
                Assert.AreEqual(s.PlayerSide, s.CurrentTurn, "应回到玩家回合");
            }
        }

        [TestCase("wuziqi")]
        [TestCase("heibaiqi")]
        [TestCase("weiqi")]
        public void PlacementGame_OccupiedPointRejectedWithoutConsumingTurn(string variant)
        {
            var s = NewSession(variant);
            var valid = s.GetValidPlacements();
            Assert.Greater(valid.Count, 0, variant + " 开局应有合法落点");

            int px = valid[0][0];
            int py = valid[0][1];
            Assert.IsTrue(s.MakePlayerPlace(px, py).Success, variant + " 首落应成功");

            // 推进到玩家再次回合（AI 应手后）
            int guard = 0;
            while (s.IsAiTurn && !s.IsEnded && guard++ < 8)
                Assert.IsTrue(s.MakeAiMove().Success, variant + " AI 应手必须成功");
            Assert.IsFalse(s.IsEnded, "两三步内不应终局");
            Assert.IsFalse(s.IsAiTurn, "应回到玩家回合");

            int history = s.State.MoveHistory.Count;
            var r = s.MakePlayerPlace(px, py);
            Assert.IsFalse(r.Success, variant + " 已占点必须被拒");
            Assert.AreEqual(s.PlayerSide, s.CurrentTurn, "被拒落子不得换手");
            Assert.AreEqual(history, s.State.MoveHistory.Count, "被拒落子不得记历史");
        }

        [Test]
        public void PlacementGame_WeiqiCaptureReportedInResultAndHistory()
        {
            // 人造提子局面：白 (1,1) 三面被黑 (0,1)(2,1)(1,0) 围住，黑落 (1,2) 提之。
            var s = NewSession("weiqi");
            s.ApplyConfigUpdate(new JObject
            {
                ["board_state"] = new JObject
                {
                    ["current_turn"] = "black",
                    ["pieces"] = new JArray
                    {
                        Stone("b1", "black", 0, 1),
                        Stone("b2", "black", 2, 1),
                        Stone("b3", "black", 1, 0),
                        Stone("w1", "white", 1, 1),
                    },
                    ["move_history"] = new JArray(),
                    ["game_status"] = new JObject(),
                },
            });

            var r = s.MakePlayerPlace(1, 2);
            Assert.IsTrue(r.Success, "提子落点应合法：" + r.Message);
            Assert.AreEqual(1, r.Captured.Count, "应提到 1 枚白子");
            Assert.That(r.Captured, Does.Contain("w1"), "被提子应出现在 Captured 列表");
            Assert.IsFalse(s.State.GetPieceById("w1").IsAlive, "被提子应置为死亡");
        }

        [Test]
        public void PlacementGame_HeibaiqiFlipRecordedInHistory()
        {
            // 标准开局：黑 (3,4)(4,3)、白 (3,3)(4,4)。黑落 (2,3) 沿 +x 夹吃白 (3,3)。
            var s = NewSession("heibaiqi");
            var r = s.MakePlayerPlace(2, 3);
            Assert.IsTrue(r.Success, "夹吃落点应合法：" + r.Message);

            var history = s.State.MoveHistory;
            var last = (JObject)history[history.Count - 1];
            var flipped = (JArray)last["flipped"];
            Assert.GreaterOrEqual(flipped.Count, 1, "黑白棋落子应产生翻转");
            Assert.That(flipped.Values<string>(), Does.Contain("white_disc_1"), "应翻转 (3,3) 的白子");
            Assert.AreEqual("black", s.State.GetPieceById("white_disc_1").Side, "被翻面棋子应改阵营");
        }

        [Test]
        public void PlacementGame_UndoRestoresBoardBeforePlace()
        {
            var s = NewSession("wuziqi");
            var valid = s.GetValidPlacements();
            int before = s.State.Pieces.Count;

            Assert.IsTrue(s.MakePlayerPlace(valid[0][0], valid[0][1]).Success);
            Assert.AreEqual(before + 1, s.State.Pieces.Count);

            Assert.IsTrue(s.CanUndo, "落子后应可悔棋");
            Assert.IsTrue(s.UndoLastMove());
            Assert.AreEqual(before, s.State.Pieces.Count, "悔棋应恢复到落子前");
            Assert.AreEqual(s.PlayerSide, s.CurrentTurn, "悔棋后应回到玩家回合");
        }

        [Test]
        public void PlacementGame_MoveGameNotAffected()
        {
            // 走子类不得被误判为落子类（分流的前提）。
            var s = NewSession("xiangqi");
            Assert.IsFalse(s.IsPlacementGame, "象棋应为走子类");
            var piece = s.State.Pieces.Find(p => p.IsAlive && p.Side == s.PlayerSide && s.GetValidMoves(p.Id).Count > 0);
            Assert.IsNotNull(piece);
            var target = s.GetValidMoves(piece.Id)[0];
            Assert.IsTrue(s.MakePlayerMove(piece.Id, target[0], target[1]).Success);
        }

        static JObject Stone(string id, string side, int x, int y) => new JObject
        {
            ["id"] = id,
            ["type"] = "stone",
            ["name"] = side == "black" ? "●" : "○",
            ["side"] = side,
            ["position"] = new JArray(x, y),
            ["is_alive"] = true,
        };
    }
}
