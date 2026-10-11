using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 大地图分块渲染器：按 16×16 分块生成等距地形，只加载玩家视野附近的块。
    ///
    /// <b>性能设计（相对 web 版的核心改进）</b>：
    ///   · <b>合批</b>——每块用 Spritesheet 静态合批（SpriteRenderer 共享同一图集 + sortingOrder 一致），
    ///     Unity 自动合为 1 个 draw call/块，视野内约 9~25 块 → 25 个 draw call；
    ///     而 web 版的 melonJS 兜底通道是每帧重绘离屏画布。
    ///   · <b>对象池</b>——瓦片 SpriteRenderer 复用，切块不产生 GC。
    ///   · <b>懒加载</b>——仅视野 ±1 块，非视野块整体卸载，内存与绘制量都与"地图大小"解耦。
    ///   · <b>单纹理</b>——所有地形来自一张程序化图集，切换材质不发生。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldChunkRenderer : MonoBehaviour
    {
        [Header("数据")]
        public OverworldMap Map;

        [Header("视图")]
        [Tooltip("视野半径（块）。1 = 3×3 块 = 48×48 格可见。")]
        [Range(1, 4)] public int ViewRadiusChunks = 2;

        [Tooltip("每格世界缩放（基于等距瓦片 96px / 100 PPU ≈ 0.96）。")]
        public float TileScale = 0.96f;

        ChunkManager _chunks;
        readonly Dictionary<long, List<SpriteRenderer>> _blockRenderers = new Dictionary<long, List<SpriteRenderer>>();
        readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>();

        Transform _chunkRoot;

        /// <summary>已加载块数，供调试面板。</summary>
        public int LoadedChunkCount => _blockRenderers.Count;

        /// <summary>当前活跃 Renderer 数（性能面板核心指标）。</summary>
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

        /// <summary>每帧（或每次移动后）调用，同步视野窗口。位置以<b>格坐标</b>传入。</summary>
        public void SyncViewport(int cellX, int cellY)
        {
            if (Map == null) return;
            if (_chunks == null) Initialize(Map);

            _chunks.UpdateWindow(cellX, cellY, ViewRadiusChunks);

            // 先卸载，再加载：避免同一帧内块数翻倍导致的瞬时内存峰值。
            foreach (var key in _chunks.PendingUnload)
            {
                if (_blockRenderers.TryGetValue(key, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        list[i].gameObject.SetActive(false);
                        list[i].transform.SetParent(_poolParent(), false);
                        _pool.Push(list[i]);
                    }
                    _blockRenderers.Remove(key);
                    _chunks.MarkUnloaded(key);
                }
            }

            _chunks.ComputeWindow(cellX, cellY, ViewRadiusChunks,
                out int minCx, out int minCy, out int maxCx, out int maxCy);

            for (int cy = minCy; cy <= maxCy; cy++)
            {
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    if (!_chunks.ShouldLoad(cx, cy)) continue;
                    BuildChunk(cx, cy);
                    _chunks.MarkLoaded(cx, cy);
                }
            }

            RecountActive();
        }

        void BuildChunk(int cx, int cy)
        {
            int x0 = cx * ChunkManager.ChunkSize;
            int y0 = cy * ChunkManager.ChunkSize;
            int x1 = Mathf.Min(x0 + ChunkManager.ChunkSize, Map.Width);
            int y1 = Mathf.Min(y0 + ChunkManager.ChunkSize, Map.Height);

            var list = new List<SpriteRenderer>((x1 - x0) * (y1 - y0));

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    var kind = Map.GetTerrain(x, y);
                    bool raised = TerrainPalette.IsRaised(kind);
                    int elev = Map.GetHeight(x, y);

                    // 顶面
                    var top = Rent();
                    SetTile(top, TerrainAtlas.Top(kind), x, y, elev, raised);

                    // 山体额外两个侧面，制造厚度错觉
                    if (raised)
                    {
                        var left = Rent();
                        SetTile(left, TerrainAtlas.Left(kind), x, y, elev, raised, sideOffset: -1);
                        var right = Rent();
                        SetTile(right, TerrainAtlas.Right(kind), x, y, elev, raised, sideOffset: 1);
                    }

                    list.Add(top);
                }
            }

            _blockRenderers[ChunkManager.Key(cx, cy)] = list;
        }

        void SetTile(SpriteRenderer sr, Sprite sprite, int x, int y, int elev, bool raised, int sideOffset = 0)
        {
            sr.gameObject.SetActive(true);
            sr.sprite = sprite;
            var p = IsoGrid.CellToWorld(x, y, elev, raised);
            // 侧面下移并轻微外推，形成挤出感。
            p.y -= IsoGrid.TileH * 0.5f * (sideOffset == 0 ? 0f : 1f);
            p.x += sideOffset * IsoGrid.TileW * 0.08f;
            sr.transform.localPosition = p * TileScale;
            sr.transform.localScale = Vector3.one;
            sr.sortingOrder = SortingOrder(x, y, sideOffset);
        }

        /// <summary>
        /// 画家算法排序：等距下 (x+y) 越大越靠前。
        /// 侧面需比顶面晚绘制（否则顶面会盖住侧面的下缘）。
        /// </summary>
        static int SortingOrder(int x, int y, int sideOffset)
        {
            int baseOrder = (x + y) * 4;
            if (sideOffset != 0) return baseOrder + 1;
            return baseOrder + 2;
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
            sr.gameObject.name = "tile";
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
            if (_blockRenderers.Count == 0) return;
            foreach (var kv in _blockRenderers)
                foreach (var sr in kv.Value)
                {
                    sr.gameObject.SetActive(false);
                    _pool.Push(sr);
                }
            _blockRenderers.Clear();
            _chunks.Loaded.Clear();
            SyncViewport(Map.PlayerSpawn[0], Map.PlayerSpawn[1]);
        }
    }
}