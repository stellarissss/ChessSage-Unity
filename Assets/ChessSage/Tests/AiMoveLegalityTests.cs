using System;
using System.Collections.Generic;
using System.Linq;
using ChessSage.Core.Ai;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// AI 走法合法性护栏。
    ///
    /// 背景：玩家视角试玩后从棋局快照里观察到「黑方炮出现在红方底线」这一
    /// 视觉上不可能的局面。快照由渲染层直接读 <see cref="BoardState"/> 得出，
    /// 因此无法区分三种成因：
    ///   (a) 渲染器的坐标 / 阵营映射有误；
    ///   (b) AI 真的生成了规则引擎不认可的走法；
    ///   (c) 走法本身合法，只是视觉上反直觉。
    /// 能区分它们的唯一办法，是让<b>规则引擎独立复核 AI 自己生成的走法</b>：
    /// AI 内部用走法生成器 + 启发式搜索，走子落地则由规则引擎判定；
    /// 一旦这两条路径对「什么算合法」的理解不一致，就会表现为非法棋步被写进真实棋局。
    ///
    /// 本类因此建立三道<b>永久护栏</b>——后续任何改动（AI 性能优化、走法排序、
    /// 浅克隆、时间预算截断、机制系统）一旦破坏「AI 只下合法棋」，测试立刻变红。
    /// </summary>
    public sealed class AiMoveLegalityTests
    {
        /// <summary>走子制（非摆放制）棋类，逐一手工对弈验证。</summary>
        static readonly string[] MoveVariants = { "xiangqi", "tiaoqi", "heibaiqi", "dongwuqi" };

        static GameSession NewSession(string variant, int seed)
        {
            var config = new ConfigLoader(TestEnv.ConfigsRoot).LoadVariant(variant);
            return new GameSession(config, new MechanismEngine(config.Rules), aiSeed: seed);
        }

        /// <summary>
        /// 护栏 1：AI 枚举出的<b>每一条</b>候选走法，都必须被规则引擎的合法走点查询接受。
        /// 这正面覆盖试玩中观察到的那一幕——AI 选出的那一步究竟合不合法。
        /// </summary>
        [Test]
        public void Ai_GeneratedMoves_AreAllAcceptedByRuleEngine()
        {
            foreach (var variant in MoveVariants)
            {
                var session = NewSession(variant, 20240607);
                var state = session.State;
                string side = state.CurrentTurn;

                var moves = session.Ai.GenerateAllMoves(state, side);
                Assert.Greater(moves.Count, 0, $"{variant}: 开局应至少能枚举出一条走法");

                foreach (var m in moves)
                {
                    var piece = state.GetPieceById(m.PieceId);
                    Assert.IsNotNull(piece, $"{variant}: 走法 {m} 指向了不存在的棋子");
                    Assert.AreEqual(side, piece.Side, $"{variant}: 走法 {m} 竟在动对手的棋子");

                    bool legal = false;
                    foreach (int[] t in session.Rules.GetValidMoves(piece, state))
                        if (t[0] == m.ToX && t[1] == m.ToY) { legal = true; break; }

                    Assert.IsTrue(legal, $"{variant}: AI 生成了规则引擎不认可的走法 {m}");
                }
            }
        }

        /// <summary>
        /// 护栏 2：连续对弈。每一步都必须被规则引擎接受，且盘面不变量单调成立
        /// （历史长度递增、棋子数不增、坐标恒在盘内）。
        /// 这条走的正是试玩中的真实路径：玩家走子 → AI 应手 → 再走子 → 再应手。
        /// </summary>
        [Test]
        public void Ai_ContinuousPlay_EveryStepRemainsLegalAndOnBoard()
        {
            foreach (var variant in MoveVariants)
            {
                var session = NewSession(variant, 99991);
                var state = session.State;

                var bounds = BoardBounds(variant, session.Config.Board);

                int prevHistory = state.MoveHistory.Count;
                int prevPieces = state.Pieces.Count;
                int plies = 0;

                while (!session.IsEnded && plies < 24)
                {
                    if (session.IsAiTurn)
                    {
                        var res = session.MakeAiMove();
                        if (!res.Success) break;      // 无子可动 / 和棋 / 终局
                    }
                    else
                    {
                        var options = AllLegalMoves(session);
                        if (options.Count == 0) break;
                        var pick = options[plies % options.Count];
                        var mr = session.MakePlayerMove(pick.Item1, pick.Item2[0], pick.Item2[1]);
                        Assert.IsTrue(mr.Success,
                            $"{variant}: 规则引擎给出的合法走点 {pick.Item1}->" +
                            $"({pick.Item2[0]},{pick.Item2[1]}) 被 MakePlayerMove 拒绝");
                    }

                    plies++;

                    Assert.AreEqual(prevHistory + 1, state.MoveHistory.Count,
                        $"{variant}: 第 {plies} 手历史未 +1，说明存在非法或重复走子");
                    prevHistory = state.MoveHistory.Count;

                    Assert.LessOrEqual(state.Pieces.Count, prevPieces + AllowedGrowth(variant),
                        $"{variant}: 棋子数增长超出该棋类规则允许的上限，说明存在重复棋子");
                    prevPieces = state.Pieces.Count;

                    foreach (var p in state.Pieces)
                    {
                        if (!p.IsAlive) continue;
                        Assert.IsTrue(p.X >= bounds.minX && p.X <= bounds.maxX &&
                                       p.Y >= bounds.minY && p.Y <= bounds.maxY,
                            $"{variant}: {p.Id} ({p.X},{p.Y}) 越出棋盘 {bounds.desc}");
                    }

                    // id 唯一性：重复 id 会让悔棋 / 翻转记录 / 存档回放指向错误棋子。
                    // 这条护栏的由来：黑白棋翻面会把白子改成黑子，而 NextDiscId 曾按
                    // 阵营过滤编号，导致重复发放 {side}_disc_N。
                    Assert.AreEqual(state.Pieces.Count,
                        new HashSet<string>(state.Pieces.Select(p => p.Id)).Count,
                        $"{variant}: 第 {plies} 手后出现重复棋子 id —— " +
                        string.Join(",", state.Pieces.Select(p => p.Id)));
                }

                Assert.Greater(plies, 2, $"{variant}: 连 3 手都走不出去，AI 或规则层有故障");
            }
        }

        /// <summary>
        /// 护栏 3：每一步棋都必须符合<b>独立推导的象棋几何规则</b>。
        ///
        /// 最初这里写的是「开局炮不可能到对方底线」，实测<b>变红了</b>——
        /// 追查后发现是断言本身错了：象棋炮本就允许隔一个炮架吃远子，
        /// 开局 x=1 纵列上 黑炮(1,2) →[炮架:红炮(1,7)]→ 红马(1,9) 是合法杀棋，
        /// 试玩快照里黑炮出现在红方底线正是这一手的结果（红马同时消失）。
        ///
        /// 因此改为<b>独立几何判定</b>：不看规则引擎怎么说，而是直接从盘面
        /// 按象棋走法定义自行判定每一步是否合法。规则引擎若与几何判定不一致，
        /// 立刻暴露。这避免了「用引擎校验引擎」的循环论证。
        /// </summary>
        [Test]
        public void Xiangqi_Opening_EveryPlyMatchesIndependentGeometry()
        {
            var session = NewSession("xiangqi", 31337);
            var state = session.State;
            int width = (int)session.Config.Board["geometry"]["width"];
            int height = (int)session.Config.Board["geometry"]["height"];

            for (int ply = 0; ply < 12 && !session.IsEnded; ply++)
            {
                string mover = state.CurrentTurn;
                int fx, fy, tx, ty;

                if (session.IsAiTurn)
                {
                    var best = session.Ai.GetBestMove(state);
                    if (best == null) break;
                    fx = best.FromX; fy = best.FromY; tx = best.ToX; ty = best.ToY;

                    // 必须先校验再走子：走完后起点已空，GetPieceAt(fx,fy) 会返回 null
                    Assert.IsTrue(GeometryAllows(state, mover, fx, fy, tx, ty),
                        $"{mover} 第 {ply} 手 ({fx},{fy})->({tx},{ty}) 不符合象棋几何规则");

                    if (!session.MakeAiMove().Success) break;
                }
                else
                {
                    var options = AllLegalMoves(session);
                    if (options.Count == 0) break;
                    var pick = options[ply % options.Count];
                    fx = fx0(pick.Item1, state); fy = fy0(pick.Item1, state);
                    tx = pick.Item2[0]; ty = pick.Item2[1];

                    Assert.IsTrue(GeometryAllows(state, mover, fx, fy, tx, ty),
                        $"{mover} 第 {ply} 手 ({fx},{fy})->({tx},{ty}) 不符合象棋几何规则");

                    if (!session.MakePlayerMove(pick.Item1, tx, ty).Success) break;
                }
            }
        }

        static int fx0(string pieceId, BoardState s) => s.GetPieceById(pieceId).X;
        static int fy0(string pieceId, BoardState s) => s.GetPieceById(pieceId).Y;

        /// <summary>
        /// 独立的象棋几何合法性判定：完全依据棋规手工推导，不调用规则引擎。
        /// 覆盖本作象棋配置里实际出现的全部棋子类型。
        /// </summary>
        static bool GeometryAllows(BoardState s, string side, int fx, int fy, int tx, int ty)
        {
            var p = s.GetPieceAt(fx, fy);
            if (p == null || p.Side != side) return false;

            var dest = s.GetPieceAt(tx, ty);
            if (dest != null && dest.Side == side) return false;

            int dx = tx - fx, dy = ty - fy;
            int adx = Math.Abs(dx), ady = Math.Abs(dy);

            // 统计起点与终点之间（不含两端）的棋子数——炮规则的核心量
            int between = CountBetween(s, fx, fy, tx, ty);

            switch (p.Type)
            {
                case "chariot":
                    // 车：直线无阻挡
                    if (!((dx == 0 && dy != 0) || (dy == 0 && dx != 0))) return false;
                    return between == 0;

                case "cannon":
                    // 炮：不吃子时不能隔山；吃子时必须恰有一个炮架
                    if (!((dx == 0 && dy != 0) || (dy == 0 && dx != 0))) return false;
                    return dest == null ? between == 0 : between == 1;

                case "horse":
                    // 马：走日，有蹩腿
                    if (!((adx == 2 && ady == 1) || (adx == 1 && ady == 2))) return false;
                    if (adx == 2)
                        return s.GetPieceAt(fx + dx / 2, fy) == null;   // 横跳：同列蹩腿
                    return s.GetPieceAt(fx, fy + dy / 2) == null;       // 纵跳：同行蹩腿

                case "elephant":
                    // 象：走田，有塞象眼，且不可过河
                    if (!(adx == 2 && ady == 2)) return false;
                    if (s.GetPieceAt(fx + dx / 2, fy + dy / 2) != null) return false;
                    return side == "red" ? ty <= 9 && fy >= 5 : ty >= 0 && fy <= 4;

                case "advisor":
                    // 士：斜走一格，必须在九宫内
                    if (!(adx == 1 && ady == 1)) return false;
                    return InPalace(tx, ty, side);

                case "general":
                    // 将帅：四方向走一格（本作未含老帅长距离将杀，故只走一格），必须在九宫内
                    if (!((adx + ady) == 1)) return false;
                    return InPalace(tx, ty, side);

                case "soldier":
                    // 卒：向前一格；过河后可左右一格；不可后退
                    if (side == "black")
                    {
                        if (dy == 1 && dx == 0) return true;
                        if (fy <= 4 && dy == 0 && adx == 1) return true;
                        return false;
                    }
                    else
                    {
                        if (dy == -1 && dx == 0) return true;
                        if (fy >= 5 && dy == 0 && adx == 1) return true;
                        return false;
                    }

                default:
                    return false;
            }
        }

        static bool InPalace(int x, int y, string side)
        {
            if (x < 3 || x > 5) return false;
            return side == "red" ? (y >= 7 && y <= 9) : (y >= 0 && y <= 2);
        }

        /// <summary>统计两点之间（不含两端）的棋子数，非直线返回 -1。</summary>
        static int CountBetween(BoardState s, int fx, int fy, int tx, int ty)
        {
            int dx = tx - fx, dy = ty - fy;
            if (fx != tx && fy != ty) return -1;
            int sx = Math.Sign(dx), sy = Math.Sign(dy);
            int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
            int n = 0;
            for (int i = 1; i < steps; i++)
                if (s.GetPieceAt(fx + sx * i, fy + sy * i) != null) n++;
            return n;
        }

        /// <summary>
        /// 该棋类每手允许的棋子数增长上限。
        /// 摆放类（黑白棋）每回合落一个新子，故允许 +1；
        /// 走子类棋子只减不增，故为 0。
        /// </summary>
        static int AllowedGrowth(string variant) => variant == "heibaiqi" ? 1 : 0;

        /// <summary>
        /// 取棋盘坐标边界。
        /// 各棋类坐标系<b>不同构</b>：象棋/五子棋/动物棋是矩形 width×height，
        /// 而跳棋是六角星「双倍列坐标」（rows × max_col，坐标可为奇数）。
        /// 因此不能用统一的 width/height 断言越界。
        /// </summary>
        static (int minX, int maxX, int minY, int maxY, string desc) BoardBounds(string variant, JObject board)
        {
            var g = board["geometry"];
            if (g["rows"] != null)
                return (0, (int)g["max_col"], 0, (int)g["rows"],
                        $"{variant} 六角星 rows={g["rows"]} max_col={g["max_col"]}");

            int w = (int)g["width"], h = (int)g["height"];
            return (0, w - 1, 0, h - 1, $"{variant} 矩形 {w}x{h}");
        }

        /// <summary>枚举当前回合所有合法走法，元素为 (棋子 id, (x,y))。</summary>
        static List<(string, int[])> AllLegalMoves(GameSession session)
        {
            var list = new List<(string, int[])>();
            foreach (var p in session.State.Pieces)
            {
                if (!p.IsAlive || p.Side != session.CurrentTurn) continue;
                foreach (int[] t in session.Rules.GetValidMoves(p, session.State))
                    list.Add((p.Id, t));
            }
            return list;
        }
    }
}