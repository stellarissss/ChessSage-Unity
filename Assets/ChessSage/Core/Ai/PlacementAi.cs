using System.Collections.Generic;
using ChessSage.Core.Model;
using ChessSage.Core.Rules;
using ChessSage.Core.Util;

namespace ChessSage.Core.Ai
{
    /// <summary>
    /// 落子类棋种（五子棋 / 围棋 / 黑白棋）的通用落子 AI。
    /// 轻量启发式：向已有己方棋子聚拢、封堵对方，叠加少量随机以增加变化。
    /// </summary>
    public static class PlacementAi
    {
        static readonly (int dx, int dy)[] Neighbors =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
        };

        public static int[] Choose(VariantRuleBase engine, BoardState state, string side,
            List<int[]> placements, DeterministicRng rng)
        {
            if (placements == null || placements.Count == 0) return null;

            // 空棋盘时直接占中，避免开局退化为随机。
            if (state.Pieces.Count == 0)
            {
                int cx = engine.Width / 2, cy = engine.Height / 2;
                foreach (var p in placements)
                    if (p[0] == cx && p[1] == cy) return p;
            }

            string opponent = engine.Opposite(side);
            double bestScore = double.NegativeInfinity;
            int[] best = placements[0];

            foreach (var p in placements)
            {
                double score = 0;
                double centerX = (engine.Width - 1) / 2.0;
                double centerY = (engine.Height - 1) / 2.0;
                score -= (System.Math.Abs(p[0] - centerX) + System.Math.Abs(p[1] - centerY)) * 0.15;

                foreach (var (dx, dy) in Neighbors)
                {
                    var n = state.GetPieceAt(p[0] + dx, p[1] + dy);
                    if (n == null) continue;
                    if (n.Side == side) score += 3.0;
                    else if (n.Side == opponent) score += 2.0;
                }

                score += rng.NextDouble() * 0.6;
                if (score > bestScore) { bestScore = score; best = p; }
            }
            return best;
        }
    }
}