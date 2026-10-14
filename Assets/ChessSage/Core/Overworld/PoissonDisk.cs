using System.Collections.Generic;

namespace ChessSage.Core.Overworld
{
    /// <summary>
    /// 确定性伪随机源（xorshift32）。
    ///
    /// <b>为什么不用 <see cref="System.Random"/></b>：<c>System.Random(seed)</c> 的
    /// 数值序列在 Mono（Unity）与 .NET 6（引擎外 L1 测试）上<b>并不一致</b>——
    /// 如果装饰布点依赖它，测试里锁定的坐标就会与 Unity 实机渲染的坐标不同，
    /// 「同一颗种子 → 同一张地图」这条硬约束会被悄悄破坏。因此自带一个
    /// 只依赖 32 位整数运算的序列，跨运行时逐位一致。
    /// </summary>
    public struct DeterministicRng32
    {
        uint _state;

        public DeterministicRng32(int seed)
        {
            // 0 会让 xorshift 退化为恒 0，故做一次混合避免落入吸收态。
            _state = (uint)seed;
            if (_state == 0) _state = 0x9E3779B9u;
        }

        /// <summary>下一个 32 位状态值。</summary>
        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>[0,1) 均匀浮点。</summary>
        public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

        /// <summary>[minInclusive, maxExclusive) 均匀整数。</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            uint span = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % span);
        }

        /// <summary>[0, maxExclusive) 均匀整数。</summary>
        public int Next(int maxExclusive) => Range(0, maxExclusive);

        public float Range(float min, float max) => min + (max - min) * NextFloat();

        public bool Chance(float p) => NextFloat() < p;
    }

    /// <summary>采样点（格坐标，整数）。Core 不引用 UnityEngine，故自带轻量结构。</summary>
    public readonly struct GridPoint
    {
        public readonly int X;
        public readonly int Y;
        public GridPoint(int x, int y) { X = x; Y = y; }
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>
    /// Poisson-disk（蓝噪声）采样器 —— Bridson 算法。
    ///
    /// <b>为什么必须是 Poisson-disk 而不是「每格独立随机」</b>：
    /// 独立随机会出现两株树贴脸、相邻三格空无一物——肉眼一看就是"随机噪点"，
    /// 不是植被。Poisson-disk 保证任意两点间距 ≥ <c>minDist</c>，且分布均匀，
    /// 视觉上接近自然群落。这是"装饰物系统"能成立的前提。
    ///
    /// 复杂度 O(n)，n = 采样点数；本工程全图 9408 格、minDist ≥ 1，实测 &lt; 5ms。
    /// </summary>
    public static class PoissonDisk
    {
        /// <summary>每个活跃点的候选轮数（Bridson 建议 30，此处 24 已足够密实且更快）。</summary>
        const int K = 24;

        /// <summary>
        /// 在矩形区域 [x0..x1]×[y0..y1] 采样。返回点保证：两两间距 ≥ <paramref name="minDist"/>，
        /// 且全部满足 <paramref name="accept"/>（用于地形/物种门控）。
        ///
        /// <paramref name="accept"/> 为 null 表示接受区域内所有点。
        /// </summary>
        public static List<GridPoint> Sample(
            int x0, int y0, int x1, int y1,
            float minDist, int seed,
            System.Func<int, int, bool> accept = null)
        {
            var result = new List<GridPoint>();
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            if (w <= 0 || h <= 0 || minDist <= 0f) return result;

            float cell = minDist / 1.41421356f;      // Bridson：网格边长 = r/√2
            int gw = (int)(w / cell) + 1;
            int gh = (int)(h / cell) + 1;
            var grid = new int[gw * gh];             // 存 result 下标 +1；0 = 空
            var active = new List<int>();

            var rng = new DeterministicRng32(seed);

            // 首个种子点：从区域中心起步。若中心不满足门控，向右下扫描首个可行格，
            // 保证"同一区域同一 seed 必得同一首点"（否则整个分布会随门控漂移）。
            if (!TryFindSeed(x0, y0, x1, y1, accept, out int sx, out int sy)) return result;
            grid[GridIndex(gw, gh, x0, y0, cell, sx, sy)] = 1;
            result.Add(new GridPoint(sx, sy));
            active.Add(0);

            float min2 = minDist * minDist;

            while (active.Count > 0)
            {
                int ai = rng.Next(active.Count);
                var pt = result[active[ai]];
                bool found = false;

                for (int t = 0; t < K; t++)
                {
                    float ang = rng.NextFloat() * 6.2831853f;
                    float rad = minDist * (1f + rng.NextFloat());   // [r, 2r)
                    int cx = (int)System.Math.Round(pt.X + rad * (float)System.Math.Cos(ang));
                    int cy = (int)System.Math.Round(pt.Y + rad * (float)System.Math.Sin(ang));

                    if (cx < x0 || cy < y0 || cx > x1 || cy > y1) continue;
                    if (accept != null && !accept(cx, cy)) continue;
                    if (TooClose(grid, gw, gh, x0, y0, cell, result, cx, cy, min2)) continue;

                    result.Add(new GridPoint(cx, cy));
                    grid[GridIndex(gw, gh, x0, y0, cell, cx, cy)] = result.Count;   // 下标+1
                    active.Add(result.Count - 1);
                    found = true;
                    break;
                }

                if (!found)
                {
                    active[ai] = active[active.Count - 1];
                    active.RemoveAt(active.Count - 1);
                }
            }

            return result;
        }

        static bool TryFindSeed(int x0, int y0, int x1, int y1,
            System.Func<int, int, bool> accept, out int sx, out int sy)
        {
            int mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            if (accept == null || accept(mx, my)) { sx = mx; sy = my; return true; }
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (accept(x, y)) { sx = x; sy = y; return true; }
            sx = sy = 0;
            return false;
        }

        static bool TooClose(int[] grid, int gw, int gh, int x0, int y0, float cell,
            List<GridPoint> pts, int cx, int cy, float min2)
        {
            int gx = (int)((cx - x0) / cell), gy = (int)((cy - y0) / cell);
            const int span = 2;                             // 半径 2 格足够覆盖 2r 邻域
            for (int yy = gy - span; yy <= gy + span; yy++)
            {
                if (yy < 0 || yy >= gh) continue;
                for (int xx = gx - span; xx <= gx + span; xx++)
                {
                    if (xx < 0 || xx >= gw) continue;
                    int idx = grid[yy * gw + xx];
                    if (idx == 0) continue;
                    var q = pts[idx - 1];
                    float dx = q.X - cx, dy = q.Y - cy;
                    if (dx * dx + dy * dy < min2) return true;
                }
            }
            return false;
        }

        static int GridIndex(int gw, int gh, int x0, int y0, float cell, int x, int y)
        {
            int gx = (int)((x - x0) / cell);
            int gy = (int)((y - y0) / cell);
            if (gx < 0) gx = 0; else if (gx >= gw) gx = gw - 1;
            if (gy < 0) gy = 0; else if (gy >= gh) gy = gh - 1;
            return gy * gw + gx;
        }
    }
}
