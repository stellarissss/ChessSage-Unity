using System;

namespace ChessSage.Core.Board
{
    /// <summary>
    /// 矩形棋盘布局：象棋（9×10）/ 动物棋（7×9）/ 围棋（19×19）/ 五子棋（15×15）/ 黑白棋（8×8）。
    ///
    /// 与 M5 之前 <c>BoardView</c> 硬编码的矩形假设<b>完全等价</b>：
    /// 局部坐标即格坐标（1 格 = 1 单位），逆变换四舍五入吸附 + 边界拒绝。
    /// 存在意义是把既有行为固化为可单测的契约，而不是新造一套语义。
    /// </summary>
    public sealed class RectLayout : IBoardLayout
    {
        /// <summary>列数（x 跨度）。</summary>
        public int Width { get; }

        /// <summary>行数（y 跨度）。</summary>
        public int Height { get; }

        public int CellCount => Width * Height;
        public int CellsX => Width;
        public int CellsY => Height;

        /// <summary>非法尺寸钳制到 1，避免 0 宽棋盘导致除零与空包络。</summary>
        public RectLayout(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
        }

        public bool IsValid(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

        public bool TryCellToLocal(int x, int y, out double localX, out double localY)
        {
            if (!IsValid(x, y))
            {
                localX = 0;
                localY = 0;
                return false;
            }
            localX = x;
            localY = y;
            return true;
        }

        public bool TryLocalToCell(double localX, double localY, out int x, out int y)
        {
            x = RoundHalfAwayFromZero(localX);
            y = RoundHalfAwayFromZero(localY);
            return IsValid(x, y);
        }

        public void GetBounds(out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = 0;
            minY = 0;
            maxX = Width - 1;
            maxY = Height - 1;
        }

        /// <summary>
        /// 半格边界的取舍必须确定：.5 一律远离零。
        /// 与 Unity <c>Mathf.RoundToInt</c> 的历史行为一致，保证 M5 前后
        /// 「正好点在两格缝上」的点击落点不发生变化。
        /// </summary>
        internal static int RoundHalfAwayFromZero(double v)
            => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }
}
