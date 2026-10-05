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
            public override string ToString() => $"{PieceId}: ({FromX},{FromY})->({ToX},{ToY})";
        }

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

            Move bestMove = null;
            double bestScore = double.NegativeInfinity;
            double alpha = double.NegativeInfinity, beta = double.PositiveInfinity;

            var moves = GenerateAllMoves(boardState, side);
            if (moves.Count == 0) return null;

            moves.Sort((a, b) => MoveScore(b, boardState).CompareTo(MoveScore(a, boardState)));

            foreach (var move in moves)
            {
                var newState = SimulateMove(boardState, move);
                double score = Minimax(newState, Depth - 1, alpha, beta, false, side);
                if (score > bestScore) { bestScore = score; bestMove = move; }
                alpha = Math.Max(alpha, score);
            }
            return bestMove;
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
            var newState = boardState.Clone();
            foreach (var p in newState.Pieces)
                if (p.Id == move.PieceId) { p.X = move.ToX; p.Y = move.ToY; break; }

            if (!string.IsNullOrEmpty(move.Captured))
                foreach (var p in newState.Pieces)
                    if (p.Id == move.Captured)
                    {
                        if (!Engine.IsInvulnerable(p)) p.IsAlive = false;
                        break;
                    }

            newState.CurrentTurn = Engine.Opposite(newState.CurrentTurn);
            return newState;
        }

        int MoveScore(Move move, BoardState boardState)
        {
            int score = 0;
            if (!string.IsNullOrEmpty(move.Captured))
                foreach (var p in boardState.Pieces)
                    if (p.Id == move.Captured)
                    {
                        if (PieceValues.TryGetValue(p.Type, out var v)) score += v;
                        else if (_customPieceValues.TryGetValue(p.Type, out var cv)) score += cv;
                        else
                        {
                            foreach (var cp in _customPieces)
                                if (cp["type"]?.Value<string>() == p.Type) { score += HeuristicCustomPieceValue(cp); break; }
                        }
                        break;
                    }
            return score;
        }

        double Minimax(BoardState boardState, int depth, double alpha, double beta, bool isMax, string aiSide)
        {
            var winner = Engine.IsGeneralCaptured(boardState);
            if (winner != null) return winner == aiSide ? 10000 : -10000;
            if (depth == 0) return Evaluate(boardState, aiSide);

            string currentSide = boardState.CurrentTurn;
            var moves = GenerateAllMoves(boardState, currentSide);
            if (moves.Count == 0) return currentSide == aiSide ? -10000 : 10000;

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
                    var generalPos = FindGeneralPosition(boardState, p.Side);
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