using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 大地图玩家角色控制器（等距四向移动）。
    ///
    /// 移动采用<b>格子插值</b>而非自由模拟：角色始终处于格心，跨格耗时固定
    /// <paramref cref="MoveSecondsPerCell"/>。这保证：
    ///   · 与 <c>baked</c> 离散地图严格对齐，POI 交互不需要容差；
    ///   · 天然可撤销/可打断（作弊机制"跳过回合""强制移动"可复用同一接口）；
    ///   · 掉帧时用 <c>Time.deltaTime</b> 补步，不会出现"卡在两格之间"。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldPlayer : MonoBehaviour
    {
        [Header("状态")]
        public OverworldMap Map;

        [Tooltip("格/秒。取自 world.speed，经等距投影折算。")]
        public float CellsPerSecond = 8f;

        [Tooltip("跨一格所需秒数；由 CellsPerSecond 推导，勿直接改。")]
        public float MoveSecondsPerCell => 1f / Mathf.Max(0.1f, CellsPerSecond);

        [Header("引用")]
        public Camera Cam;
        public OverworldChunkRenderer Chunks;
        public OverworldFog Fog;

        // ── 位置状态 ──────────────────────────────────────────────────────────

        int _cellX, _cellY;
        int _fromX, _fromY;
        int _toX, _toY;
        float _t;                 // 0~1，本格移动插值
        bool _moving;

        public int CellX => _moving ? _fromX : _toX;
        public int CellY => _moving ? _fromY : _toY;

        public bool IsMoving => _moving;

        /// <summary>到达一格时触发：(x, y)。用于交互判定、迷雾揭示。</summary>
        public System.Action<int, int> ArrivedAt;

        public void Initialize(OverworldMap map)
        {
            Map = map;
            _cellX = _fromX = _toX = map.PlayerSpawn[0];
            _cellY = _fromY = _toY = map.PlayerSpawn[1];
            _moving = false;
            _t = 0f;
            transform.position = IsoGrid.CellToWorld(_cellX, _cellY,
                map.GetHeight(_cellX, _cellY), TerrainPalette.IsRaised(map.GetTerrain(_cellX, _cellY)));
            if (Cam != null) SnapCamera();
        }

        void Update()
        {
            if (Map == null) return;

            if (!_moving) ReadInput();
            if (_moving) Advance(Time.deltaTime);

            if (Chunks != null) Chunks.SyncViewport(CellX, CellY);
            if (Fog != null) Fog.Reveal(CellX, CellY);
        }

        void ReadInput()
        {
            // 屏幕空间方向 → 网格方向。等距下屏幕上/下不再是地图 y+1，
            // 必须先逆投影，否则手感与地图错位（这是等距改造最容易踩的坑）。
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            if (Mathf.Abs(h) < 0.1f && Mathf.Abs(v) < 0.1f) return;

            var dir = ScreenToGridDir(h, v);
            TryStep(dir.x, dir.y);
        }

        /// <summary>屏幕输入向量 → 网格方向。取输入向量在两个网格轴上的投影，取较大者。</summary>
        public static Vector2Int ScreenToGridDir(float h, float v)
        {
            // 网格 +x 在屏幕上是右下（(1, 0.5)），+y 在屏幕上是左下（(-1, 0.5)）
            float dotX = h * 1f + v * 0.5f;
            float dotY = -h * 1f + v * 0.5f;
            if (Mathf.Abs(dotX) >= Mathf.Abs(dotY))
                return new Vector2Int(dotX >= 0 ? 1 : -1, 0);
            return new Vector2Int(0, dotY >= 0 ? 1 : -1);
        }

        /// <summary>尝试向 (dx,dy) 走一格。越界或外圈群山会被拒绝。</summary>
        public bool TryStep(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return false;
            int nx = CellX + dx, ny = CellY + dy;
            if (!Map.IsWalkable(nx, ny)) return false;

            _fromX = CellX; _fromY = CellY;
            _toX = nx; _toY = ny;
            _t = 0f;
            _moving = true;
            return true;
        }

        /// <summary>直接传送到指定格（传送门、剧情跳转、调试）。会取消当前移动。</summary>
        public void Teleport(int x, int y)
        {
            if (!Map.InBounds(x, y)) return;
            _moving = false;
            _cellX = _fromX = _toX = x;
            _cellY = _fromY = _toY = y;
            transform.position = CellWorld(_toX, _toY);
        }

        void Advance(float dt)
        {
            _t += dt / MoveSecondsPerCell;
            if (_t >= 1f)
            {
                _t = 1f;
                _cellX = _toX; _cellY = _toY;
                _moving = false;
                transform.position = CellWorld(_cellX, _cellY);
                ArrivedAt?.Invoke(_cellX, _cellY);
            }
            else
            {
                var a = CellWorld(_fromX, _fromY);
                var b = CellWorld(_toX, _toY);
                transform.position = Vector3.Lerp(a, b, SmoothStep(_t));
            }
        }

        /// <summary>缓动：起步加速、到位减速，避免"瞬移感"，是手感提升的关键细节。</summary>
        static float SmoothStep(float t) => t * t * (3f - 2f * t);

        Vector3 CellWorld(int x, int y)
        {
            var kind = Map.GetTerrain(x, y);
            return IsoGrid.CellToWorld(x, y, Map.GetHeight(x, y), TerrainPalette.IsRaised(kind));
        }

        void SnapCamera()
        {
            if (Cam == null) Cam = Camera.main;
            if (Cam == null) return;
            var p = transform.position;
            Cam.transform.position = new Vector3(p.x, p.y, -30f);
        }

        /// <summary>供外部（相机跟随脚本）使用的当前世界坐标。</summary>
        public Vector3 WorldPosition => transform.position;
    }
}