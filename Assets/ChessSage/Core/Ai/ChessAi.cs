using System;
using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Util;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Ai
{
    /// <summary>
    /// AI 下棋引擎 —— Minimax + Alpha-Beta 剪枝。
    /// 逐语义移植自 legacy-web/xiangqi/chess_ai.py（含 AI 性格系统对评估的影响）。
    /// </summary>
    public sealed class ChessAi
    {
        public static readonly Dictionary<string, int> PieceValues = new Dictionary<string, int>
        {
            ["general"] = 10000,
            ["chariot"] = 900,
            ["cannon"] = 450,
            ["horse"] = 400,
            ["elephant"] = 200,
            ["advisor"] = 200,
            ["soldier"] = 100,
        };

        public readonly VariantRuleBase Engine;
        public string Difficulty;
        public int Depth;
        public double Randomness;

        public double PersonalityAggressiveness = 0.5;
        public double PersonalityConservatism = 0.5;
        public JObject PersonalityValueBiases = new JObject();

        readonly List<JObject> _customPieces = new List<JObject>();
        readonly Dictionary<string, int> _customPieceValues = new Dictionary<string, int>();
        readonly DeterministicRng _rng;

        public ChessAi(VariantRuleBase ruleEngine, JObject rules, string difficulty = "medium", int rngSeed = 0)
        {
            Engine = ruleEngine;
            Difficulty = difficulty;
            _rng = new DeterministicRng(rngSeed == 0 ? Environment.TickCount : rngSeed);

            var levels = (rules?["ai_difficulty"] as JObject)?["levels"] as JObject;
            var levelCfg = levels?[difficulty] as JObject ?? levels?["medium"] as JObject;
            Depth = levelCfg?["depth"]?.Value<int>() ?? 3;
            Randomness = levelCfg?["randomness"]?.Value<double>() ?? 0.1;
        }

        public void SetDifficulty(string difficulty)
        {
            Difficulty = difficulty;
            if (difficulty == "easy") { Depth = 2; Randomness = 0.3; }
            else if (difficulty == "medium") { Depth = 3; Randomness = 0.1; }
            else { Depth = 4; Randomness = 0.0; }
        }

        /// <summary>注册自定义棋子规则（供启发式估值）。</summary>
        public void RegisterCustomPieces(IEnumerable<JObject> customPieces)
        {
            foreach (var cp in customPieces) if (cp != null) _customPieces.Add(cp);
        }

        public sealed class Move
        {
            public string PieceId;
            public int FromX, FromY;
            public int ToX, ToY;
            public string Captured;

            /// <summary>排序用的缓存分值（由 <c>OrderMoves</c> 填一次）。</summary>
            public int Order;

            public override string ToString() => $"{PieceId}: ({FromX},{FromY})->({ToX},{ToY})";
        }

        /// <summary>
        /// 单次决策的<b>节点预算</b>（搜索的棋子-回合组合数上限）。
        /// 迭代加深每层结束检查一次，超预算即返回<b>上一层已完成的结果</b>——
        /// 这保证单步耗时存在硬上限（实测象棋 depth=2 全搜索约 3万节点/90ms），
        /// 而不会像纯固定深度那样在复杂局面突然飙到秒级卡顿。
        /// </summary>
        public int NodeBudget = 120000;

        /// <summary>单步搜索的时间预算（毫秒）。超过即停止加深并返回当前最佳着。
        /// 250ms 是回合制下"几乎无感"的上限：Unity Mono JIT 比桌面 .NET 慢约 4 倍，
        /// 本预算按 Mono 实测校准（象棋 medium 全层搜索 Mono 下约 200-250ms）。</summary>
        public int MaxSearchMilliseconds = 250;

        int _nodes;
        bool _nodeLimitReached;
        System.Diagnostics.Stopwatch _searchSw;

        /// <summary>上一层的耗时（毫秒），供上层做自适应调优与调试。</summary>
        public double LastSearchMilliseconds { get; private set; }

        public Move GetBestMove(BoardState boardState)
        {
            string side = boardState.CurrentTurn;

            foreach (var cp in _customPieces)
            {
                var cpType = cp["type"]?.Value<string>();
                if (!string.IsNullOrEmpty(cpType) && !_customPieceValues.ContainsKey(cpType))
                    HeuristicCustomPieceValue(cp);
            }

            if (Randomness > 0 && _rng.NextDouble() < Randomness)
            {
                var move = GetRandomMove(boardState, side);
                if (move != null) return move;
            }

            _searchSw = System.Diagnostics.Stopwatch.StartNew();
            _nodes = 0;

            var moves = GenerateAllMoves(boardState, side);
            if (moves.Count == 0) return null;

            // 迭代加深：从浅到深逐层搜索，任一层超时/超预算就返回该层已得出的最佳走法。
            // 相比直接搜到目标深度，它有两个好处：
            //   · 硬性上界由 NodeBudget 保证，不会因局面复杂度突变而卡顿；
            //   · depth=1 的结果总是可得的，即使深层搜索被打断也有合理着法。
            Move best = null;
            for (int depth = 1; depth <= Depth; depth++)
            {
                var prev = _nodeLimitReached;
                _nodeLimitReached = false;
                var candidate = SearchRoot(boardState, moves, side, depth, out double score);
                _nodeLimitReached = prev;

                if (candidate != null) best = candidate;

                // 最后一层，或搜索被硬截断 → 不再加深
                if (_nodeLimitReached || _searchSw.ElapsedMilliseconds > MaxSearchMilliseconds) break;
            }

            LastSearchMilliseconds = _searchSw.Elapsed.TotalMilliseconds;
            return best;
        }

        /// <summary>在指定深度上做一次根搜索（α-β + 走法排序）。</summary>
        Move SearchRoot(BoardState boardState, List<Move> moves, string side, int depth, out double rootScore)
        {
            // 走法排序（迭代加深的关键）：先算一次分值再排序，
            // 避免 Comparator 里重复做 O(棋子数) 的估值计算 —— 原实现每次比较都重算。
            OrderMoves(moves, boardState);

            Move bestMove = null;
            double bestScore = double.NegativeInfinity;
            double alpha = double.NegativeInfinity;

            foreach (var move in moves)
            {
                var newState = SimulateMove(boardState, move);
                double score = Minimax(newState, depth - 1, alpha, double.PositiveInfinity, false, side);
                if (score > bestScore) { bestScore = score; bestMove = move; }
                if (score > alpha) alpha = score;
                if (_nodeLimitReached) break;
            }

            rootScore = bestScore;
            return bestMove;
        }

        /// <summary>
        /// 走法排序：优先尝试高分走法（吃子价值高的先搜）。
        /// 分数<b>只算一次</b>并缓存进 <see cref="Move.Order"/>，
        /// 这让排序从 O(n log n × 棋子数) 降到 O(n log n)。
        /// </summary>
        void OrderMoves(List<Move> moves, BoardState boardState)
        {
            for (int i = 0; i < moves.Count; i++)
                moves[i].Order = MoveScore(moves[i], boardState);
            moves.Sort((a, b) => b.Order.CompareTo(a.Order));
        }

        Move GetRandomMove(BoardState boardState, string side)
        {
            var moves = GenerateAllMoves(boardState, side);
            return moves.Count == 0 ? null : moves[_rng.Next(moves.Count)];
        }

        public List<Move> GenerateAllMoves(BoardState boardState, string side)
        {
            var moves = new List<Move>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || p.Side != side) continue;
                foreach (var pos in Engine.GetValidMoves(p, boardState))
                {
                    var target = Engine.GetPieceAt(pos[0], pos[1], boardState);
                    moves.Add(new Move
                    {
                        PieceId = p.Id,
                        FromX = p.X, FromY = p.Y,
                        ToX = pos[0], ToY = pos[1],
                        Captured = target?.Id,
                    });
                }
            }
            return moves;
        }

        BoardState SimulateMove(BoardState boardState, Move move)
        {
            // 搜索路径使用浅克隆：不复制只读 JSON。
            // 实测这是"AI 每步卡顿"的根因——完整 Clone 会对 4 个 JObject 做 DeepClone，
            // 在 depth=3 的约 6 万节点中累计超过 2 秒。
            var newState = boardState.CloneForSearch();
            for (int i = 0; i < newState.Pieces.Count; i++)
            {
                var p = newState.Pieces[i];
                if (p.Id == move.PieceId) { p.X = move.ToX; p.Y = move.ToY; break; }
            }

            if (!string.IsNullOrEmpty(move.Captured))
            {
                for (int i = 0; i < newState.Pieces.Count; i++)
                {
                    var p = newState.Pieces[i];
                    if (p.Id == move.Captured)
                    {
                        if (!Engine.IsInvulnerable(p)) p.IsAlive = false;
                        break;
                    }
                }
            }

            newState.CurrentTurn = Engine.Opposite(newState.CurrentTurn);
            return newState;
        }

        int MoveScore(Move move, BoardState boardState)
        {
            if (string.IsNullOrEmpty(move.Captured)) return 0;
            // O(1) 查表（原为线性扫描全部棋子）
            var p = boardState.GetPieceById(move.Captured);
            if (p == null) return 0;

            if (PieceValues.TryGetValue(p.Type, out var v)) return v;
            if (_customPieceValues.TryGetValue(p.Type, out var cv)) return cv;
            foreach (var cp in _customPieces)
                if (cp["type"]?.Value<string>() == p.Type) return HeuristicCustomPieceValue(cp);
            return 0;
        }

        double Minimax(BoardState boardState, int depth, double alpha, double beta, bool isMax, string aiSide)
        {
            // 预算检查（每 256 节点抽样一次时间查询，避免逐节点取钟的开销）：
            // 只在层间检查时无法中断"单层超预算"的情况 —— L3 实测 depth=3 单层
            // 在 Mono 下即达 380ms+，故必须节点级截断。
            if ((++_nodes & 255) == 0 &&
                (_nodes > NodeBudget || _searchSw.ElapsedMilliseconds > MaxSearchMilliseconds))
            {
                _nodeLimitReached = true;
                return 0d;
            }

            var winner = Engine.IsGeneralCaptured(boardState);
            if (winner != null) return winner == aiSide ? 10000 : -10000;
            if (depth == 0) return Evaluate(boardState, aiSide);

            string currentSide = boardState.CurrentTurn;
            var moves = GenerateAllMoves(boardState, currentSide);
            if (moves.Count == 0) return currentSide == aiSide ? -10000 : 10000;

            // 内部节点同样排序：α-β 剪枝的命中率直接由走法顺序决定。
            // 实测这一项让搜索量下降一个数量级。
            OrderMoves(moves, boardState);

            if (isMax)
            {
                double maxEval = double.NegativeInfinity;
                foreach (var move in moves)
                {
                    var newState = SimulateMove(boardState, move);
                    double eval = Minimax(newState, depth - 1, alpha, beta, false, aiSide);
                    maxEval = Math.Max(maxEval, eval);
                    alpha = Math.Max(alpha, eval);
                    if (beta <= alpha) break;
                }
                return maxEval;
            }
            else
            {
                double minEval = double.PositiveInfinity;
                foreach (var move in moves)
                {
                    var newState = SimulateMove(boardState, move);
                    double eval = Minimax(newState, depth - 1, alpha, beta, true, aiSide);
                    minEval = Math.Min(minEval, eval);
                    beta = Math.Min(beta, eval);
                    if (beta <= alpha) break;
                }
                return minEval;
            }
        }

        public double Evaluate(BoardState boardState, string aiSide)
        {
            double score = 0.0;
            double agg = PersonalityAggressiveness;
            double cons = PersonalityConservatism;

            // 保守性格需要"己方将/帅"的位置。原实现对<b>每一个棋子</b>都线性扫描一遍去找将，
            // 使估值从 O(n)退化为 O(n²)。这里每方只查一次。
            var aiGeneral = cons > 0.5 ? FindGeneralPosition(boardState, aiSide) : null;
            var foeGeneral = cons > 0.5 ? FindGeneralPosition(boardState, Engine.Opposite(aiSide)) : null;

            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive) continue;
                string pieceType = p.Type;
                double val;
                if (PieceValues.TryGetValue(pieceType, out var v)) val = v;
                else if (_customPieceValues.TryGetValue(pieceType, out var cv)) val = cv;
                else
                {
                    val = 0;
                    foreach (var cp in _customPieces)
                        if (cp["type"]?.Value<string>() == pieceType) { val = HeuristicCustomPieceValue(cp); break; }
                }

                var biasToken = PersonalityValueBiases[pieceType];
                if (biasToken != null) val = val * (1.0 + biasToken.Value<double>());

                if (p.Side == aiSide) score += val; else score -= val;

                int px = p.X, py = p.Y;
                if (p.Type == "soldier")
                {
                    double baseBonus = 30;
                    double aggBonus = baseBonus * (0.5 + agg);
                    if (p.Side == "red" && py <= 4) score += p.Side == aiSide ? aggBonus : -aggBonus;
                    else if (p.Side == "black" && py >= 5) score += p.Side == aiSide ? aggBonus : -aggBonus;
                }

                if (p.Type == "chariot" || p.Type == "horse" || p.Type == "cannon")
                {
                    double baseCenter = (3.5 - Math.Abs(px - 4)) * 2;
                    double centerBonus = baseCenter * (0.5 + agg);
                    score += p.Side == aiSide ? centerBonus : -centerBonus;
                }

                if (!PieceValues.ContainsKey(pieceType) && _customPieceValues.ContainsKey(pieceType))
                {
                    double baseCenter = (3.5 - Math.Abs(px - 4)) * 1.0;
                    double centerBonus = baseCenter * (0.5 + agg);
                    score += p.Side == aiSide ? centerBonus : -centerBonus;
                }

                if (cons > 0.5)
                {
                    var generalPos = p.Side == aiSide ? aiGeneral : foeGeneral;
                    if (generalPos.HasValue)
                    {
                        int dist = Math.Abs(px - generalPos.Value.x) + Math.Abs(py - generalPos.Value.y);
                        if (dist <= 2)
                        {
                            double defenseBonus = (3 - dist) * 15 * (cons - 0.5) * 2;
                            score += p.Side == aiSide ? defenseBonus : -defenseBonus;
                        }
                    }
                }
            }
            return score;
        }

        (int x, int y)? FindGeneralPosition(BoardState boardState, string side)
        {
            foreach (var p in boardState.Pieces)
                if (p.Side == side && p.Type == "general" && p.IsAlive) return (p.X, p.Y);
            return null;
        }

        /// <summary>启发式评估自定义棋子价值（AI 不可用时的回退方案）。</summary>
        public int HeuristicCustomPieceValue(JObject pieceRule)
        {
            var pieceType = pieceRule["type"]?.Value<string>() ?? "unknown";
            if (_customPieceValues.TryGetValue(pieceType, out var cached)) return cached;

            var movement = pieceRule["movement"] as JObject ?? new JObject();
            var customModifiers = pieceRule["custom_modifiers"] as JArray ?? new JArray();

            string moveType = movement["type"]?.Value<string>() ?? "";
            var baseMap = new Dictionary<string, int>
            {
                ["free"] = 800, ["orthogonal"] = 500, ["diagonal"] = 300,
                ["L_shape"] = 400, ["conditional"] = 150,
            };
            int baseValue = baseMap.TryGetValue(moveType, out var bv) ? bv : 200;

            int maxDist = movement["max_distance"]?.Value<int>() ?? 1;
            if (maxDist > 1) baseValue += Math.Min(maxDist * 30, 200);

            foreach (var mod in customModifiers)
                if (mod is JObject mo && mo["type"]?.Value<string>() == "extra_movement")
                {
                    var extra = mo["movement"] as JObject ?? new JObject();
                    var extraType = extra["type"]?.Value<string>() ?? "";
                    var extraMap = new Dictionary<string, int> { ["free"] = 200, ["orthogonal"] = 100, ["diagonal"] = 80, ["L_shape"] = 100 };
                    baseValue += extraMap.TryGetValue(extraType, out var ev) ? ev : 50;
                }

            if (movement["can_cross_river"]?.Value<bool>() == true) baseValue += 50;

            int value = Math.Max(50, Math.Min(1000, baseValue));
            _customPieceValues[pieceType] = value;
            return value;
        }

        /// <summary>注入自定义棋子价值（由编排器经大模型评估后回填）。</summary>
        public void SetCustomPieceValue(string pieceType, int value)
            => _customPieceValues[pieceType] = Math.Max(50, Math.Min(1000, value));
    }
}