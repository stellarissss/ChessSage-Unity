using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 道路渲染器：把原版设计中"贯通六道的官道"画成一条踩踏出的土路。
    ///
    /// 【为什么值得画】上一版大地图只有地形色块，玩家看不出"路在哪里、该往哪走"。
    /// 道路是**天然的引导线**——沿着路走就能到达六道入口，这本身就是一种探索动机。
    ///
    /// 实现上走"分块 + 对象池"（与装饰同构）：道路只占全图一小部分格，
    /// 视野内通常只有几十格，开销可忽略。道路图元单张、共享材质，利于合批。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldRoadRenderer : MonoBehaviour
    {
        [Header("数据")]
        public OverworldMap Map;
        [Range(8, 40)] public int LoadMarginCells = 18;
        public float TileScale = 0.96f;

        ChunkManager _chunks;
        readonly Dictionary<long, List<int>> _byChunk = new Dictionary<long, List<int>>();
        readonly Dictionary<long, List<SpriteRenderer>> _live = new Dictionary<long, List<SpriteRenderer>>();
        readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>();
        readonly List<long> _unloadScratch = new List<long>();
        readonly List<GridPoint> _cells = new List<GridPoint>();
        readonly HashSet<long> _cellSet = new HashSet<long>();

        Transform _root;

        public int ActiveRoadCount { get; private set; }

        public void Initialize(OverworldMap map)
        {
            Map = map;
            _chunks = new ChunkManager(map);
            CollectCells();
            BuildChunkIndex();

            if (_root == null)
            {
                var go = new GameObject("Roads");
                go.transform.SetParent(transform, false);
                _root = go.transform;
            }
        }

        static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

        void CollectCells()
        {
            _cells.Clear();
            _cellSet.Clear();
            foreach (var seg in Map.RoadSegments)
            {
                if (seg.Horizontal)
                {
                    for (int x = seg.From; x <= seg.To; x++) Add(x, seg.Axis);
                }
                else
                {
                    for (int y = seg.From; y <= seg.To; y++) Add(seg.Axis, y);
                }
            }
            // 折线道路（若有）也纳入。
            foreach (var road in Map.Roads)
                for (int i = 0; i + 1 < road.Points.Length; i += 2)
                    Add(road.Points[i], road.Points[i + 1]);
        }

        void Add(int x, int y)
        {
            if (!Map.InBounds(x, y)) return;
            if (Map.GetTerrain(x, y) == TerrainKind.Wall) return;   // 官道不翻边界群山
            if (!_cellSet.Add(CellKey(x, y))) return;
            _cells.Add(new GridPoint(x, y));
        }

        void BuildChunkIndex()
        {
            _byChunk.Clear();
            for (int i = 0; i < _cells.Count; i++)
            {
                long key = ChunkManager.Key(ChunkManager.ChunkOf(_cells[i].X), ChunkManager.ChunkOf(_cells[i].Y));
                if (!_byChunk.TryGetValue(key, out var list)) { list = new List<int>(); _byChunk[key] = list; }
                list.Add(i);
            }
        }

        public void SyncViewport(int cellX, int cellY)
        {
            if (Map == null) return;
            if (_chunks == null) Initialize(Map);

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
            var list = new List<SpriteRenderer>(idxs.Count);
            for (int i = 0; i < idxs.Count; i++)
            {
                var c = _cells[idxs[i]];
                var kind = Map.GetTerrain(c.X, c.Y);
                bool raised = TerrainPalette.IsRaised(kind);
                var centre = IsoGrid.CellToWorld(c.X, c.Y, Map.GetHeight(c.X, c.Y), raised) * TileScale;

                var sr = Rent();
                sr.gameObject.name = "road";
                sr.sprite = RoadArt.Diamond();
                sr.transform.localPosition = centre;
                sr.transform.localScale = Vector3.one;
                sr.color = new Color(0.70f, 0.60f, 0.42f, 0.55f);
                sr.sortingOrder = OverworldSort.Base(c.X, c.Y) + 2;
                list.Add(sr);
            }
            _live[key] = list;
        }

        SpriteRenderer Rent()
        {
            SpriteRenderer sr;
            if (_pool.Count > 0) { sr = _pool.Pop(); sr.transform.SetParent(_root, false); }
            else { var go = new GameObject("r"); sr = go.AddComponent<SpriteRenderer>(); }
            return sr;
        }

        Transform _poolParentObj;
        Transform _poolParent()
        {
            if (_poolParentObj == null)
            {
                var go = new GameObject("PooledRoads");
                go.transform.SetParent(transform, false);
                _poolParentObj = go.transform;
            }
            return _poolParentObj;
        }

        void Recount()
        {
            int n = 0;
            foreach (var kv in _live) n += kv.Value.Count;
            ActiveRoadCount = n;
        }
    }

    /// <summary>道路图元：一块带磨损噪声的土色菱形（与地形瓦片同几何，可无缝拼接）。</summary>
    internal static class RoadArt
    {
        static Sprite _diamond;

        public static Sprite Diamond()
        {
            if (_diamond != null) return _diamond;
            const int cell = 96;
            var tex = new Texture2D(cell, cell, TextureFormat.RGBA32, false)
            {
                name = "RoadDiamond",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[cell * cell];
            float cx = cell * 0.5f, cy = cell * 0.5f;
            float hw = cell * 0.5f - 2f, hh = cell * 0.25f - 1f;
            var rng = new DeterministicRng32(0x5EED);

            for (int y = 0; y < cell; y++)
                for (int x = 0; x < cell; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - cx) / hw;
                    float dy = Mathf.Abs(y + 0.5f - cy) / hh;
                    float d = dx + dy;
                    if (d > 0.86f) continue;                 // 比地形略小，四周留出草色缝隙
                    float grain = 1f + rng.Range(-0.08f, 0.08f);
                    byte v = (byte)Mathf.Clamp(255 * grain, 0, 255);
                    px[y * cell + x] = new Color32(v, v, v, 255);
                }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            _diamond = Sprite.Create(tex, new Rect(0, 0, cell, cell), new Vector2(0.5f, 0.5f),
                TerrainAtlas.PixelsPerUnit, 0, SpriteMeshType.FullRect);
            _diamond.name = "road_diamond";
            return _diamond;
        }
    }
}
