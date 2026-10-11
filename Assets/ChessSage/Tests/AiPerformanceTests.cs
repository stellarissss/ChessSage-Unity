using System.Diagnostics;
using ChessSage.Core.Ai;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// AI 决策的性能与行为护栏。
    ///
    /// 背景：迁移初期的实测显示 <c>ChessAi.GetBestMove</c>单次耗时<b>2231 ms</b>，
    /// 意味着 AI 每走一步棋都会让游戏卡死两秒。修复后降到两个数量级以下，
    /// 本类测试把"AI 决策必须是毫秒级、且必须有硬上限"固化为可执行断言，
    /// 防止将来任何改动把它退回去。
    /// </summary>
    public sealed class AiPerformanceTests
    {
        static GameSession NewSession(string variant = "xiangqi", string difficulty = "medium")
        {
            var loader = new ConfigLoader(TestEnv.ConfigsRoot);
            var config = loader.LoadVariant(variant);
            config.Rules["ai_difficulty"]!["current"] = difficulty;
            var session = new GameSession(config, new MechanismEngine(config.Rules), aiSeed: 7);
            session.Ai.SetDifficulty(difficulty);
            return session;
        }

        [Test]
        public void Decision_StaysWithinFrameBudget()
        {
            var s = NewSession();
            var best = s.Ai.GetBestMove(s.State);
            Assert.IsNotNull(best, "开局必须能算出着法");
            Assert.Less(s.Ai.LastSearchMilliseconds, 250d,
                $"AI 决策耗时 {s.Ai.LastSearchMilliseconds:F1}ms，超出可接受预算（卡顿会被玩家直接感知）");
        }

        [Test]
        public void Decision_StaysBoundedAfterSeveralMoves()
        {
            // 真实对局的局面比开局复杂得多（子力变化 + 合法性剪枝变化），
            // 必须确保不会出现"越走越慢"的退化。
            var s = NewSession();
            for (int i = 0; i < 6; i++)
            {
                var m = s.Ai.GetBestMove(s.State);
                if (m == null) break;
                var r = s.MakePlayerMove(m.PieceId, m.ToX, m.ToY);
                if (!r.Success) break;
                if (s.IsEnded) break;
            }
            var sw = Stopwatch.StartNew();
            var best = s.Ai.GetBestMove(s.State);
            sw.Stop();
            Assert.IsNotNull(best);
            Assert.Less(sw.ElapsedMilliseconds, 400L,
                $"中局AI 决策耗时 {sw.ElapsedMilliseconds}ms，超出上限");
        }

        [Test]
        public void Decision_RespectsNodeBudget()
        {
            // 人为把预算压到极低，验证硬截断生效且仍返回合法着法——
            // 这是"永不卡死"的最后一道防线。
            var s = NewSession();
            s.Ai.NodeBudget = 50;
            s.Ai.MaxSearchMilliseconds = 10;
            var best = s.Ai.GetBestMove(s.State);
            Assert.IsNotNull(best, "即使预算耗尽也必须返回合法着法，不能返回 null 让AI 停摆");

            // 返回的着法必须真的合法
            var piece = s.State.GetPieceById(best.PieceId);
            Assert.IsNotNull(piece);
            var legal = s.GetValidMoves(best.PieceId);
            bool ok = false;
            foreach (var m in legal)
                if (m[0] == best.ToX && m[1] == best.ToY) { ok = true; break; }
            Assert.IsTrue(ok, "预算截断后返回的着法仍必须是合法走法");
        }

        [Test]
        public void Decision_IsDeterministicWithFixedSeed()
        {
            // 固定种子下两次搜索必须给出同一着法，否则回放/录像/考试模式都不可靠。
            var a = NewSession();
            var b = NewSession();
            var ma = a.Ai.GetBestMove(a.State);
            var mb = b.Ai.GetBestMove(b.State);
            Assert.AreEqual(ma.PieceId, mb.PieceId);
            Assert.AreEqual(ma.ToX, mb.ToX);
            Assert.AreEqual(ma.ToY, mb.ToY);
        }

        [Test]
        public void AllVariants_CanDecideQuickly()
        {
            // 六种棋类都必须能在毫秒级给出决策（含围棋/五子棋这类大分枝棋种）。
            foreach (var id in new[] { "xiangqi", "dongwuqi", "tiaoqi", "wuziqi", "weiqi", "heibaiqi" })
            {
                var s = NewSession(id);
                var sw = Stopwatch.StartNew();
                var m = s.Ai.GetBestMove(s.State);
                sw.Stop();
                Assert.Less(sw.ElapsedMilliseconds, 400L, $"{id} 决策耗时 {sw.ElapsedMilliseconds}ms 过高");
                if (s.IsPlacementGame) continue;   // 落子类无"当前移动子"，允许 null
                Assert.IsNotNull(m, $"{id} 移动类棋种必须给出走法");
            }
        }

        [Test]
        public void SearchShallowClone_DoesNotCorruptRealBoard()
        {
            // CloneForSearch 与只读 JSON 共享引用，必须验证搜索不会污染真实棋局。
            // 这条断言直接盯住那次性能优化的正确性前提。
            var s = NewSession();
            var before = s.State.Pieces.Find(p => p.Side == "red" && p.Type == "horse");
            Assert.IsNotNull(before);
            int piecesBefore = s.State.Pieces.Count;
            bool aliveBefore = before.IsAlive;

            s.Ai.GetBestMove(s.State);

            Assert.AreEqual(piecesBefore, s.State.Pieces.Count, "搜索不得改变真实棋子数量");
            Assert.AreEqual(aliveBefore, before.IsAlive, "搜索不得改变真实棋子的存活状态");
        }
    }
}