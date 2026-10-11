using System.Collections.Generic;

namespace ChessSage.Core.Overworld
{
    /// <summary>
    /// 地图分块（chunk）管理器：112×84 全图 9408 格一次性实例化会产生近万个 Renderer，
    /// 在移动端直接掉帧。这里按 16×16 分块（7×6 = 42 块），按需加载/卸载，
    /// 配合对象池使稳态 DrawCall 与节点数处于可控范围。
    ///
    /// 这是相对 web 版（一次性预渲染整张 3584×2688 画布）的关键改进：
    /// 内存从"整图常驻"降到"视野内常驻"。
    /// </summary>
    public sealed class ChunkManager
    {
        public const int ChunkSize = 16;

        readonly OverworldMap _map;
        public int ChunksX { get; }
        public int ChunksY { get; }

        /// <summary>已加载的块坐标集合，供渲染层判断是否需要加载/卸载。</summary>
        public readonly HashSet<long> Loaded = new HashSet<long>();
        public readonly HashSet<long> PendingUnload = new HashSet<long>();

        public ChunkManager(OverworldMap map)
        {
            _map = map;
            ChunksX = (map.Width + ChunkSize - 1) / ChunkSize;
            ChunksY = (map.Height + ChunkSize - 1) / ChunkSize;
        }

        public static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public static int ChunkOf(int cell) => cell / ChunkSize;

        /// <summary>
        /// 计算以 (cx,cy) 为中心、半径 <paramref name="radiusChunks"/> 的块窗口（视野加载）。
        /// </summary>
        public void ComputeWindow(int cellX, int cellY, int radiusChunks,
            out int minCx, out int minCy, out int maxCx, out int maxCy)
        {
            minCx = System.Math.Max(0, ChunkOf(cellX) - radiusChunks);
            maxCx = System.Math.Min(ChunksX - 1, ChunkOf(cellX) + radiusChunks);
            minCy = System.Math.Max(0, ChunkOf(cellY) - radiusChunks);
            maxCy = System.Math.Min(ChunksY - 1, ChunkOf(cellY) + radiusChunks);
        }

        /// <summary>
        /// 标记需要加载/卸载的块。<b>不直接实例化</b>——渲染层决定实际加载时机，
        /// 以便把"数据窗口"与"GPU 上传"解耦（避免一次性上传造成卡顿）。
        /// </summary>
        public void UpdateWindow(int cellX, int cellY, int radiusChunks)
        {
            ComputeWindow(cellX, cellY, radiusChunks, out int minCx, out int minCy, out int maxCx, out int maxCy);

            PendingUnload.Clear();
            foreach (var k in Loaded)
            {
                var cx = (int)(k >> 32);
                var cy = (int)(k & 0xFFFFFFFF);
                if (cx < minCx || cx > maxCx || cy < minCy || cy > maxCy) PendingUnload.Add(k);
            }
        }

        public bool ShouldLoad(int cx, int cy) => !Loaded.Contains(Key(cx, cy));

        public void MarkLoaded(int cx, int cy) => Loaded.Add(Key(cx, cy));

        public void MarkUnloaded(long key)
        {
            Loaded.Remove(key);
            PendingUnload.Remove(key);
        }

        /// <summary>统计当前窗口内的总格数，用于调试与性能面板。</summary>
        public int WindowCellCount(int cellX, int cellY, int radius)
        {
            ComputeWindow(cellX, cellY, radius, out int a, out int b, out int c, out int d);
            return (c - a + 1) * (d - b + 1) * ChunkSize * ChunkSize;
        }
    }

    /// <summary>
    /// 探索迷雾：记录已探索格与当前可见格。
    ///
    /// 用<b>位图</b>（每格 1 bit）而非 HashSet，112×84 仅 1176 字节，
    /// 可直接整体上传为 GPU 纹理（见OverworldFogRenderer），使迷雾渲染为一次 draw call。
    /// </summary>
    public sealed class FogOfWar
    {
        readonly OverworldMap _map;
        readonly ulong[] _explored;

        /// <summary>底层地图引用（只读），供渲染层对齐像素网格。</summary>
        public OverworldMap MapRef => _map;

        public FogOfWar(OverworldMap map)
        {
            _map = map;
            _explored = new ulong[(map.Width * map.Height + 63) / 64];
        }

        public bool IsExplored(int x, int y)
        {
            if (!_map.InBounds(x, y)) return false;
            int i = y * _map.Width + x;
            return (_explored[i >> 6] & (1UL << (i & 63))) != 0;
        }

        public void MarkExplored(int x, int y)
        {
            if (!_map.InBounds(x, y)) return;
            int i = y * _map.Width + x;
            _explored[i >> 6] |= 1UL << (i & 63);
        }

        /// <summary>以某点为中心、半径 <paramref name="radius"/> 格标记视野（圆形，含 0.5 边缘羽化由渲染层处理）。</summary>
        public void RevealAround(int cx, int cy, int radius)
        {
            int r2 = radius * radius;
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                if (y < 0 || y >= _map.Height) continue;
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    if (x < 0 || x >= _map.Width) continue;
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r2) MarkExplored(x, y);
                }
            }
        }

        /// <summary>已探索比例 0~1，用于成就与统计。</summary>
        public float ExploredRatio()
        {
            long n = 0;
            foreach (var w in _explored) n += PopCount(w);
            return (float)((double)n / (_map.Width * _map.Height));
        }

        /// <summary>64 位整数置位数。不用 System.Numerics.BitOperations 是因为
        /// Unity 的 netstandard 2.1 轮廓中不存在该类型。</summary>
        static int PopCount(ulong v)
        {
            // SWAR _popcount64，分支少且无需查表。
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0f0f0f0f0f0f0f0fUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }

        /// <summary>导出为逐格 0/1 数组，供迷雾纹理使用。</summary>
        public byte[] ToByteArray()
        {
            var r = new byte[_map.Width * _map.Height];
            for (int y = 0; y < _map.Height; y++)
                for (int x = 0; x < _map.Width; x++)
                    r[y * _map.Width + x] = IsExplored(x, y) ? (byte)1 : (byte)0;
            return r;
        }
    }

    /// <summary>
    /// 四向流场寻路（BFS）。地图 9408 格，全图 BFS 仅需毫秒级，
    /// 且天然契合"四向移动、非等距"的大地图规则。
    ///
    /// 相比 A*：大地图上目标是"走到某处"而非精确避障，流场的
    /// 「一次计算、多点复用」特性更优——同一目标可同时驱动玩家与 NPC。
    /// </summary>
    public sealed class FlowField
    {
        public readonly int Width;
        public readonly int Height;
        readonly int[] _dist;
        readonly OverworldMap _map;

        public FlowField(OverworldMap map)
        {
            _map = map;
            Width = map.Width;
            Height = map.Height;
            _dist = new int[Width * Height];
        }

        public int DistanceAt(int x, int y) => _map.InBounds(x, y) ? _dist[y * Width + x] : int.MaxValue;

        /// <summary>从目标点反向 BFS，生成到达该点的距离场。</summary>
        public void Build(int targetX, int targetY)
        {
            for (int i = 0; i < _dist.Length; i++) _dist[i] = int.MaxValue;
            if (!_map.InBounds(targetX, targetY) || !_map.IsWalkable(targetX, targetY)) return;

            var queue = new Queue<int>(Width * Height);
            int start = targetY * Width + targetX;
            _dist[start] = 0;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % Width, cy = cur / Width;
                int nd = _dist[cur] + 1;

                TryVisit(cur - 1, cx - 1, cy, nd, queue);
                TryVisit(cur + 1, cx + 1, cy, nd, queue);
                TryVisit(cur - Width, cx, cy - 1, nd, queue);
                TryVisit(cur + Width, cx, cy + 1, nd, queue);
            }
        }

        void TryVisit(int idx, int nx, int ny, int nd, Queue<int> queue)
        {
            if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) return;
            if (_dist[idx] <= nd) return;
            if (!_map.IsWalkable(nx, ny)) return;
            _dist[idx] = nd;
            queue.Enqueue(idx);
        }

        /// <summary>沿梯度下降求出朝向目标的下一步格；已在目标格返回 null。</summary>
        public int[] StepToward(int fromX, int fromY)
        {
            int cur = _dist.Length == 0 ? 0 : fromY * Width + fromX;
            int best = cur, bestD = DistanceAt(fromX, fromY);
            if (bestD == int.MaxValue || bestD == 0) return null;

            int cx = fromX, cy = fromY;
            Consider(cur - 1, cx - 1, cy, ref best, ref bestD);
            Consider(cur + 1, cx + 1, cy, ref best, ref bestD);
            Consider(cur - Width, cx, cy - 1, ref best, ref bestD);
            Consider(cur + Width, cx, cy + 1, ref best, ref bestD);

            if (best == cur) return null;
            return new[] { best % Width, best / Width };
        }

        void Consider(int idx, int nx, int ny, ref int best, ref int bestD)
        {
            if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) return;
            int d = _dist[idx];
            if (d < bestD) { bestD = d; best = idx; }
        }
    }
}