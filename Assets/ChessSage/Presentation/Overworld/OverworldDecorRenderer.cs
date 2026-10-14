using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 装饰物分块渲染器：按块实例化 <see cref="DecorPlanner"/> 的布点结果，
    /// 与地形层共用同一套窗口规则与对象池。
    ///
    /// 【性能】装饰是全图数量最大的图层，因此：
    ///   · 每块只在进入窗口时实例化，离开即回池；
    ///   · 全部共享 <see cref="DecorAtlas"/>（单材质 → 合批）；
    ///   · 布点已在 Core 侧用 Poisson 限制最小间距（≥2 格），从源头限制数量。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldDecorRenderer : MonoBehaviour
    {
        [Header("数据")]
        public OverworldMap Map;

        [Header("视图")]
        [Range(8, 40)] public int LoadMarginCells = 18;
        public float TileScale = 0.96f;

        [Tooltip("是否为高大装饰绘制落地投影（增强立体感，代价是少量额外精灵）。")]
        public bool EnableShadows = true;

        [Tooltip("装饰整体尺寸系数。")]
        public float BaseScale = 1.7f;

        ChunkManager _chunks;
        List<DecorInstance> _plan;
        readonly Dictionary<long, List<int>> _byChunk = new Dictionary<long, List<int>>();
        readonly Dictionary<long, List<SpriteRenderer>> _live = new Dictionary<long, List<SpriteRenderer>>();
        readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>();
        readonly List<long> _unloadScratch = new List<long>();

        Transform _root;

        public int ActiveDecorCount { get; private set; }

        public void Initialize(OverworldMap map, List<DecorInstance> plan)
        {
            Map = map;
            _plan = plan ?? new List<DecorInstance>();
            _chunks = new ChunkManager(map);
            BuildChunkIndex();

            if (_root == null)
            {
                var go = new GameObject("Decor");
                go.transform.SetParent(transform, false);
                _root = go.transform;
            }
        }

        /// <summary>把布点按块归并，之后每块渲染只看自己的下标，O(装饰数) 只算一次。</summary>
        void BuildChunkIndex()
        {
            _byChunk.Clear();
            for (int i = 0; i < _plan.Count; i++)
            {
                var d = _plan[i];
                long key = ChunkManager.Key(ChunkManager.ChunkOf(d.X), ChunkManager.ChunkOf(d.Y));
                if (!_byChunk.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    _byChunk[key] = list;
                }
                list.Add(i);
            }
        }

        public void SyncViewport(int cellX, int cellY)
        {
            if (Map == null) return;
            if (_chunks == null) Initialize(Map, _plan);

            OverworldWindow.Compute(Map, _chunks.ChunksX, _chunks.ChunksY,
                cellX, cellY, LoadMarginCells, out int minCx, out int minCy, out int maxCx, out int maxCy);

            _unloadScratch.Clear();
            foreach (var key in _live.Keys)
            {
                int cx = (int)(key >> 32), cy = (int)(key & 0xFFFFFFFF);
                if (cx < minCx || cx > maxCx || cy < minCy || cy > maxCy) _unloadScratch.Add(key);
            }
            for (int i = 0; i < _unloadScratch.Count; i++) UnloadChunk(_unloadScratch[i]);

            for (int cy = minCy; cy <= maxCy; cy++)
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    long key = ChunkManager.Key(cx, cy);
                    if (!_live.ContainsKey(key) && _byChunk.ContainsKey(key)) BuildChunk(key);
                }

            Recount();
        }

        void UnloadChunk(long key)
        {
            if (!_live.TryGetValue(key, out var list)) return;
            for (int i = 0; i < list.Count; i++)
            {
                var sr = list[i];
                if (sr == null) continue;
                sr.transform.SetParent(_poolParent(), false);
                sr.gameObject.SetActive(false);
                _pool.Push(sr);
            }
            _live.Remove(key);
        }

        void BuildChunk(long key)
        {
            var idxs = _byChunk[key];
            var list = new List<SpriteRenderer>(idxs.Count * 2);

            for (int i = 0; i < idxs.Count; i++)
            {
                var d = _plan[idxs[i]];
                var kind = Map.GetTerrain(d.X, d.Y);
                bool raised = TerrainPalette.IsRaised(kind);
                var centre = IsoGrid.CellToWorld(d.X, d.Y, Map.GetHeight(d.X, d.Y), raised) * TileScale;

                float scale = BaseScale * (d.ScalePct / 100f);

                // 落地投影（仅高大物件）
                if (EnableShadows && IsTall(d.Kind))
                {
                    var sh = Rent();
                    sh.gameObject.name = "decor_shadow";
                    sh.sprite = DecorAtlas.Shadow();
                    sh.transform.localPosition = new Vector3(centre.x, centre.y - 0.02f, 0f);
                    sh.transform.localScale = new Vector3(scale * TallShadowW(d.Kind), scale * 0.5f, 1f);
                    sh.color = new Color(1f, 1f, 1f, 0.55f);
                    sh.sortingOrder = OverworldSort.Base(d.X, d.Y) + 2;
                    list.Add(sh);
                }

                var sr = Rent();
                sr.gameObject.name = "decor";
                sr.sprite = DecorAtlas.Get(d.Kind, d.Variant);
                // 锚点在底部（见 DecorAtlas 的 pivot），故直接落在格心即可。
                sr.transform.localPosition = centre;
                sr.transform.localScale = new Vector3(d.FlipX ? -scale : scale, scale, 1f);

                // 色调抖动：同种装饰不再是一个颜色。用 TintSeed 派生 ±10% 明度 + 轻微暖冷偏移。
                float b = 0.90f + (d.TintSeed / 255f) * 0.22f;
                float warm = ((d.TintSeed % 23) - 11) / 11f * 0.05f;
                sr.color = new Color(
                    Mathf.Clamp01(b + warm), Mathf.Clamp01(b), Mathf.Clamp01(b - warm), 1f);

                sr.sortingOrder = OverworldSort.Base(d.X, d.Y) + 3;
                list.Add(sr);
            }

            _live[key] = list;
        }

        static bool IsTall(DecorKind k) => k switch
        {
            DecorKind.BroadTree or DecorKind.PineTree or DecorKind.DeadTree => true,
            DecorKind.Boulder or DecorKind.RuinPillar or DecorKind.Cactus => true,
            DecorKind.Banner or DecorKind.Crystal or DecorKind.Stump => true,
            _ => false,
        };

        static float TallShadowW(DecorKind k) => k switch
        {
            DecorKind.BroadTree or DecorKind.Banner => 1.6f,
            DecorKind.PineTree or DecorKind.DeadTree or DecorKind.RuinPillar or DecorKind.Cactus => 1.3f,
            _ => 1.1f,
        };

        SpriteRenderer Rent()
        {
            SpriteRenderer sr;
            if (_pool.Count > 0) { sr = _pool.Pop(); sr.transform.SetParent(_root, false); }
            else { var go = new GameObject("d"); sr = go.AddComponent<SpriteRenderer>(); }
            return sr;
        }

        Transform _poolParentObj;
        Transform _poolParent()
        {
            if (_poolParentObj == null)
            {
                var go = new GameObject("PooledDecor");
                go.transform.SetParent(transform, false);
                _poolParentObj = go.transform;
            }
            return _poolParentObj;
        }

        void Recount()
        {
            int n = 0;
            foreach (var kv in _live) n += kv.Value.Count;
            ActiveDecorCount = n;
        }
    }
}
