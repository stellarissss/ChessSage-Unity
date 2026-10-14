using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 大地图分块渲染器：按 16×16 分块生成等距地形，只加载玩家视野附近的块。
    ///
    /// <b>性能设计</b>：
    ///   · <b>合批</b>——共享同一图集（单材质），相邻排序序号的瓦片自然合批；
    ///   · <b>对象池</b>——瓦片 SpriteRenderer 复用，切块不产生 GC；
    ///   · <b>懒加载</b>——仅视野 ±margin 格，非视野块整体卸载；
    ///   · <b>单纹理</b>——所有地形来自一张程序化图集，切换不产生材质切换。
    ///
    /// 【本轮升级】
    ///   · <b>瓦片变体</b>：同种地形按格子哈希取 4 个变体之一，消除"整齐划一"；
    ///   · <b>逐格明暗抖动</b>：±6% 亮度，进一步打破重复感；
    ///   · <b>地形过渡</b>：与邻居地形分组不同的边，叠加一层"邻居色羽化楔形"，
    ///     草地/水域/雪线之间不再硬切；
    ///   · <b>自适应窗口</b>：按"可见格矩形 + margin"决定加载块，而非对称块半径——
    ///     既保证 1920×1080 等距视野无空洞，又不像半径 2 那样把 5×5 块的角落也建出来
    ///     （那些角落根本不在屏幕内，纯属浪费软渲染的 CPU）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldChunkRenderer : MonoBehaviour
    {
        [Header("数据")]
        public OverworldMap Map;

        [Header("视图")]
        [Tooltip("加载窗口在玩家四周额外多出的格数（覆盖屏幕 + 安全边距）。")]
        [Range(8, 40)] public int LoadMarginCells = 18;

        [Tooltip("每格世界缩放（基于等距瓦片 96px / 100 PPU ≈ 0.96）。")]
        public float TileScale = 0.96f;

        [Header("表现开关")]
        [Tooltip("是否绘制地形过渡（邻接处的颜色羽化）。")]
        public bool EnableTransitions = true;

        /// <summary>排序序号量化：每个 (x+y) 对角线占 6 个序号（侧/顶/过渡/装饰/角色/地标）。</summary>
        public const int OrderStride = 6;

        ChunkManager _chunks;
        readonly Dictionary<long, List<SpriteRenderer>> _blockRenderers = new Dictionary<long, List<SpriteRenderer>>();
        readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>();
        readonly List<long> _unloadScratch = new List<long>();

        Transform _chunkRoot;

        public int LoadedChunkCount => _blockRenderers.Count;
        public int ActiveRendererCount { get; private set; }

        public void Initialize(OverworldMap map)
        {
            Map = map;
            _chunks = new ChunkManager(map);
            EnsureRoot();
        }

        void EnsureRoot()
        {
            if (_chunkRoot != null) return;
            var go = new GameObject("Chunks");
            go.transform.SetParent(transform, false);
            _chunkRoot = go.transform;
        }

        /// <summary>每帧（或每次移动后）调用，同步视野窗口。位置以格坐标传入。</summary>
        public void SyncViewport(int cellX, int cellY)
        {
            if (Map == null) return;
            if (_chunks == null) Initialize(Map);

            ComputeWindow(cellX, cellY, out int minCx, out int minCy, out int maxCx, out int maxCy);

            // ① 卸载离开窗口的块（先卸载再加载，避免同帧对象数翻倍）。
            _unloadScratch.Clear();
            foreach (var key in _chunks.Loaded)
            {
                int cx = (int)(key >> 32), cy = (int)(key & 0xFFFFFFFF);
                if (cx < minCx || cx > maxCx || cy < minCy || cy > maxCy) _unloadScratch.Add(key);
            }
            for (int i = 0; i < _unloadScratch.Count; i++)
                UnloadChunk(_unloadScratch[i]);

            // ② 加载窗口内尚未加载的块。
            for (int cy = minCy; cy <= maxCy; cy++)
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    if (!_chunks.ShouldLoad(cx, cy)) continue;
                    BuildChunk(cx, cy);
                    _chunks.MarkLoaded(cx, cy);
                }

            RecountActive();
        }

        /// <summary>可见格矩形（玩家 ±margin）→ 覆盖它的块范围。</summary>
        void ComputeWindow(int cellX, int cellY, out int minCx, out int minCy, out int maxCx, out int maxCy)
            => OverworldWindow.Compute(Map, _chunks.ChunksX, _chunks.ChunksY,
                cellX, cellY, LoadMarginCells, out minCx, out minCy, out maxCx, out maxCy);

        void UnloadChunk(long key)
        {
            if (!_blockRenderers.TryGetValue(key, out var list)) { _chunks.MarkUnloaded(key); return; }
            for (int i = 0; i < list.Count; i++)
            {
                var sr = list[i];
                if (sr == null) continue;
                sr.transform.SetParent(_poolParent(), false);
                sr.gameObject.SetActive(false);
                _pool.Push(sr);
            }
            _blockRenderers.Remove(key);
            _chunks.MarkUnloaded(key);
        }

        void BuildChunk(int cx, int cy)
        {
            int x0 = cx * ChunkManager.ChunkSize;
            int y0 = cy * ChunkManager.ChunkSize;
            int x1 = Mathf.Min(x0 + ChunkManager.ChunkSize, Map.Width);
            int y1 = Mathf.Min(y0 + ChunkManager.ChunkSize, Map.Height);

            var list = new List<SpriteRenderer>((x1 - x0) * (y1 - y0) * 2);

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    var kind = Map.GetTerrain(x, y);
                    bool raised = TerrainPalette.IsRaised(kind);
                    int elev = Map.GetHeight(x, y);
                    int variant = VariantOf(x, y);
                    var tint = CellTint(x, y);

                    if (raised)
                    {
                        // 先侧面、再顶面，形成挤出感。
                        var left = Rent();
                        SetTile(left, TerrainAtlas.Left(kind, variant), x, y, elev, true, tint, sideOffset: -1);
                        var right = Rent();
                        SetTile(right, TerrainAtlas.Right(kind, variant), x, y, elev, true, tint, sideOffset: 1);
                        var top = Rent();
                        SetTile(top, TerrainAtlas.Top(kind, variant), x, y, elev, true, tint);
                        list.Add(left); list.Add(right); list.Add(top);
                    }
                    else
                    {
                        var top = Rent();
                        SetTile(top, TerrainAtlas.Top(kind, variant), x, y, elev, false, tint);
                        list.Add(top);
                    }

                    if (EnableTransitions) AddTransitions(list, x, y, kind, elev, raised);
                }
            }

            _blockRenderers[ChunkManager.Key(cx, cy)] = list;
        }

        /// <summary>与四个邻居逐向比较；分组不同则在公共边上叠加邻居色羽化楔形。</summary>
        void AddTransitions(List<SpriteRenderer> list, int x, int y, TerrainKind kind, int elev, bool raised)
        {
            var own = TerrainGroups.Of(kind);
            // 0=N(x,y-1) 1=E(x+1,y) 2=S(x,y+1) 3=W(x-1,y)
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 1 ? 1 : d == 3 ? -1 : 0);
                int ny = y + (d == 0 ? -1 : d == 2 ? 1 : 0);
                if (!Map.InBounds(nx, ny)) continue;
                var nk = Map.GetTerrain(nx, ny);
                if (TerrainGroups.Of(nk) == own) continue;

                var overlay = Rent();
                overlay.gameObject.name = "transition";
                overlay.transform.localPosition = IsoGrid.CellToWorld(x, y, elev, raised) * TileScale;
                overlay.transform.localScale = Vector3.one;
                overlay.sprite = OverworldEdgeAtlas.Wedge(d);

                var nc = TerrainPalette.Get(nk);
                bool waterEdge = TerrainGroups.Of(nk) == TerrainGroup.Water;
                var col = waterEdge ? Color.Lerp(nc, Color.white, 0.45f) : nc;
                float a = waterEdge ? 0.62f : 0.5f;
                overlay.color = new Color(col.r, col.g, col.b, a);
                overlay.sortingOrder = OverworldSort.Base(x, y) + 2;
                list.Add(overlay);
            }
        }

        void SetTile(SpriteRenderer sr, Sprite sprite, int x, int y, int elev, bool raised,
            Color tint, int sideOffset = 0)
        {
            sr.gameObject.name = "tile";
            sr.gameObject.SetActive(true);
            sr.sprite = sprite;
            var p = IsoGrid.CellToWorld(x, y, elev, raised);
            p.y -= IsoGrid.TileH * 0.5f * (sideOffset == 0 ? 0f : 1f);
            p.x += sideOffset * IsoGrid.TileW * 0.08f;
            sr.transform.localPosition = p * TileScale;
            sr.transform.localScale = Vector3.one;
            sr.color = tint;
            sr.sortingOrder = SortingOrder(x, y, sideOffset);
        }

        /// <summary>
        /// 画家算法排序：等距下 (x+y) 越大越靠前。每个对角线留 6 个序号：
        /// 0 侧面 / 1 顶面 / 2 过渡 / 3 装饰 / 4 角色 / 5 地标。
        /// </summary>
        static int SortingOrder(int x, int y, int sideOffset)
        {
            int baseOrder = OverworldSort.Base(x, y);
            return sideOffset != 0 ? baseOrder : baseOrder + 1;
        }

        /// <summary>每格一个稳定变体（同格每次重建一致，不会闪变）。</summary>
        static int VariantOf(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093 ^ y * 19349663);
                h ^= h >> 13;
                return (int)(h % (uint)TerrainAtlas.Variants);
            }
        }

        /// <summary>逐格明暗抖动 ±6%：让大片同种地形不再是一块平板（静态，不随时间闪变）。</summary>
        static Color CellTint(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 83492791 ^ y * 2654435761u);
                float j = ((h >> 9) & 0x7F) / 127f;      // [0,1]
                float mul = 0.90f + j * 0.20f;           // 明度 ±10%
                // 轻微冷暖偏移：让邻近同种地形在色相上也不同，而不是只有明暗差异。
                float warm = (((h >> 3) & 0x1F) / 31f - 0.5f) * 0.07f;
                return new Color(mul + warm, mul, mul - warm, 1f);
            }
        }

        SpriteRenderer Rent()
        {
            SpriteRenderer sr;
            if (_pool.Count > 0)
            {
                sr = _pool.Pop();
                sr.transform.SetParent(_chunkRoot, false);
            }
            else
            {
                var go = new GameObject("t");
                sr = go.AddComponent<SpriteRenderer>();
            }
            return sr;
        }

        Transform _poolParentObj;
        Transform _poolParent()
        {
            if (_poolParentObj == null)
            {
                var go = new GameObject("PooledTiles");
                go.transform.SetParent(transform, false);
                _poolParentObj = go.transform;
            }
            return _poolParentObj;
        }

        void RecountActive()
        {
            int n = 0;
            foreach (var kv in _blockRenderers) n += kv.Value.Count;
            ActiveRendererCount = n;
        }

        /// <summary>强制整图重建（切换地图或调试用）。</summary>
        public void RebuildAll()
        {
            foreach (var kv in _blockRenderers)
                foreach (var sr in kv.Value)
                {
                    if (sr == null) continue;
                    sr.gameObject.SetActive(false);
                    _pool.Push(sr);
                }
            _blockRenderers.Clear();
            if (_chunks != null) _chunks.Loaded.Clear();
            SyncViewport(Map.PlayerSpawn[0], Map.PlayerSpawn[1]);
        }
    }

    /// <summary>
    /// 等距画家排序的**统一口径**。
    ///
    /// 【为什么必须倒过来排】等距投影下 <c>screenY = (x+y)*TileH</c>，而 Unity 的 y 轴朝上，
    /// 因此 <b>(x+y) 越大 = 屏幕越靠上 = 离相机越远</b>。画家算法要求"先画远的、后画近的"，
    /// 所以排序序号必须随 (x+y) <b>递减</b>。
    ///
    /// 历史实现写成了随 (x+y) 递增 —— 等于"远的盖住近的"。因为地形是平铺的、
    /// 相互不重叠，这个错误<b>长期被掩盖</b>；一旦出现"高个子"物体（玩家立绘、建筑、
    /// 树木）立刻暴露：角色下半身会被身后（更远）的草地瓦片盖住，看起来"只剩一个脑袋"。
    ///
    /// 这里集中一处，避免各渲染层各写一份、再次漂移。
    /// </summary>
    public static class OverworldSort
    {
        /// <summary>最大的 (x+y)。由组合根在加载地图后写入（=Width+Height 即可覆盖全图）。</summary>
        public static int MaxDiag = 256;

        /// <summary>该格的基础排序序号（远小近大）。</summary>
        public static int Base(int x, int y) => (MaxDiag - (x + y)) * OverworldChunkRenderer.OrderStride;
    }
}
