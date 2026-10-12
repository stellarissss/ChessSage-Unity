using System.Collections.Generic;
using ChessSage.Core.Board;
using ChessSage.Core.Model;
using ChessSage.Presentation.Art;
using UnityEngine;

namespace ChessSage.Presentation.Board
{
    /// <summary>
    /// 通用棋盘视图：把 <see cref="BoardState"/> 渲染成「底格 + 棋子」两层 2D 表现。
    ///
    /// 【M5-A2 泛化】坐标映射不再硬编码矩形假设，而是委托给 Core 的 <see cref="IBoardLayout"/>：
    ///   · 矩形棋种（象棋/动物棋/围棋/五子棋/黑白棋）→ <see cref="RectLayout"/>，局部坐标即格坐标（1 格 = 1 单位）；
    ///   · 跳棋（六角星）→ <see cref="HexStarLayout"/>，格坐标是 (行号, 双倍列号)，横距 0.5、纵距 1。
    ///   于是「新增棋类无需改动表现层」从口号变成结构事实：形状差异被封装进布局对象，
    ///   视图只问布局「这个格在哪」（TryCellToLocal）与「这个点属于哪个格」（TryLocalToCell）。
    ///
    /// 【为什么底格也要逐格问 IsValid】跳棋棋盘是六角星而非实心矩形：若按 17×25 把底格全铺，
    /// 星形空洞处会凭空多出棋盘格，玩家会把「不可落子的洞」误当成可落子区域。
    /// 因此底格层对每格调用 <see cref="IBoardLayout.IsValid"/>，只铺真实存在的格位。
    ///
    /// 设计要点：
    ///   · 完全<b>数据驱动</b>——六种棋类共用同一份实现，不含任何棋类特判；
    ///   · <b>对象池</b>——底格与棋子的 GameObject 均复用，避免每回合 Instantiate/Destroy 造成 GC 抖动；
    ///   · <b>脏标记刷新</b>——只有 <see cref="MarkDirty"/> 被调用才重建，Update 阶段零分配。
    /// </summary>
    public sealed class BoardView : MonoBehaviour
    {
        [Header("布局")]
        [Tooltip("单格边长（世界单位）。")]
        public float CellSize = 1f;

        [Tooltip("棋盘锚点：true 表示布局局部坐标原点即原点；false 表示棋盘以合法格位的包络中心居中。")]
        public bool AnchorAtOrigin = false;

        [Tooltip("是否渲染棋盘底格（跳棋空洞处不铺）。")]
        public bool ShowCells = true;

        [Header("引用")]
        [Tooltip("棋子父节点；留空则自动挂到本节点下。")]
        public Transform PieceRoot;

        [Tooltip("底格父节点；留空则自动挂到本节点下。")]
        public Transform CellRoot;

        readonly List<PieceView> _pool = new List<PieceView>();
        readonly List<PieceView> _active = new List<PieceView>();
        readonly List<SpriteRenderer> _cellPool = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _activeCells = new List<SpriteRenderer>();

        bool _dirty = true;
        float _centerX;
        float _centerY;

        /// <summary>当前棋盘布局（格坐标 ⇄ 局部坐标）。由 <see cref="Bind(BoardState, IBoardLayout)"/> 注入。</summary>
        public IBoardLayout Layout { get; private set; }

        /// <summary>待渲染的棋盘状态。由 <c>GameScreen</c> 在每回合结束时注入。</summary>
        public BoardState State { get; private set; }

        /// <summary>布局 x 方向跨度（矩形=列数；六角星=行数），供输入层做边界判断。</summary>
        public int Width { get; private set; } = 9;

        /// <summary>布局 y 方向跨度（矩形=行数；六角星=双倍列取值数）。</summary>
        public int Height { get; private set; } = 10;

        // ── 绑定 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 矩形兼容入口：内部等价于 <see cref="RectLayout"/>(width, height)。
        /// 保留此重载是为了不破坏既有调用点（如旧脚本按 Width/Height 绑定），行为与 M5 之前完全一致。
        /// </summary>
        public void Bind(BoardState state, int width, int height)
            => Bind(state, new RectLayout(width, height));

        /// <summary>
        /// 任意布局入口：M5-A2 起 <c>GameScreen</c> 走这条，按棋种经 <see cref="BoardLayoutFactory"/>
        /// 选择 <see cref="RectLayout"/> / <see cref="HexStarLayout"/>。传入 null 时回退为矩形缺省尺寸，避免空引用。
        /// </summary>
        public void Bind(BoardState state, IBoardLayout layout)
        {
            State = state;
            Layout = layout ?? new RectLayout(Width, Height);
            Width = Layout.CellsX;
            Height = Layout.CellsY;
            RecomputeCenter();
            MarkDirty();
        }

        /// <summary>请求下一帧重建底格与棋子层。由外部在数据变更后调用。</summary>
        public void MarkDirty() => _dirty = true;

        void Update()
        {
            if (_dirty) Rebuild();
        }

        // ── 重建 ──────────────────────────────────────────────────────────────

        void Rebuild()
        {
            _dirty = false;
            EnsurePieceRoot();

            // 先全部回收，再按需取出——顺序即绘制层级，不需要额外排序。
            for (int i = 0; i < _active.Count; i++) _active[i].gameObject.SetActive(false);
            _active.Clear();

            RebuildCells();

            if (State == null) return;

            var pieces = State.Pieces;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (p == null || !p.IsAlive) continue;
                var view = Rent();
                view.Bind(p, CellToLocal(p.X, p.Y));
                _active.Add(view);
            }
        }

        /// <summary>铺底格：只铺 <see cref="IBoardLayout.IsValid"/> 为真的格位（跳棋空洞不铺）。</summary>
        void RebuildCells()
        {
            for (int i = 0; i < _activeCells.Count; i++) _activeCells[i].gameObject.SetActive(false);
            _activeCells.Clear();

            if (!ShowCells || Layout == null) return;
            EnsureCellRoot();

            for (int x = 0; x < Layout.CellsX; x++)
            {
                for (int y = 0; y < Layout.CellsY; y++)
                {
                    if (!Layout.IsValid(x, y)) continue;
                    var cell = RentCell();
                    cell.transform.localPosition = CellToLocal(x, y);
                    cell.transform.localScale = Vector3.one * CellSize;
                    cell.sprite = ((x + y) & 1) == 0 ? PlaceholderArt.Cell : PlaceholderArt.CellAlt;
                    cell.gameObject.SetActive(true);
                    _activeCells.Add(cell);
                }
            }
        }

        PieceView Rent()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (!_pool[i].gameObject.activeSelf)
                {
                    _pool[i].gameObject.SetActive(true);
                    return _pool[i];
                }
            }

            var go = new GameObject("piece");
            go.transform.SetParent(PieceRoot != null ? PieceRoot : transform, false);
            var view = go.AddComponent<PieceView>();
            _pool.Add(view);
            return view;
        }

        SpriteRenderer RentCell()
        {
            for (int i = 0; i < _cellPool.Count; i++)
                if (!_cellPool[i].gameObject.activeSelf) return _cellPool[i];

            var go = new GameObject("cell");
            go.transform.SetParent(CellRoot != null ? CellRoot : transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = -10;   // 底格永远在棋子（10）与高亮（5）之下
            _cellPool.Add(sr);
            return sr;
        }

        void EnsurePieceRoot()
        {
            if (PieceRoot != null) return;
            var go = new GameObject("PieceRoot");
            go.transform.SetParent(transform, false);
            PieceRoot = go.transform;
        }

        void EnsureCellRoot()
        {
            if (CellRoot != null) return;
            var go = new GameObject("CellRoot");
            go.transform.SetParent(transform, false);
            CellRoot = go.transform;
        }

        // ── 坐标 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 格坐标 → 本地坐标（棋盘以包络中心居中时整体偏移中心）。
        /// 非法格位（越界 / 六角星空洞）返回 (0,0)——与 <see cref="IBoardLayout.TryCellToLocal"/> 契约一致，
        /// 调用方可先用 <see cref="CellToLocal(int,int,out Vector3)"/> 重载判断合法性。
        /// </summary>
        public Vector3 CellToLocal(int x, int y)
        {
            double lx = 0, ly = 0;
            if (Layout != null) Layout.TryCellToLocal(x, y, out lx, out ly);
            return LocalFromLayout(lx, ly);
        }

        /// <summary>格坐标 → 本地坐标，并返回该格是否合法（跳棋空洞/越界为 false 且输出 (0,0)）。</summary>
        public bool CellToLocal(int x, int y, out Vector3 local)
        {
            double lx = 0, ly = 0;
            bool ok = Layout != null && Layout.TryCellToLocal(x, y, out lx, out ly);
            local = LocalFromLayout(lx, ly);
            return ok;
        }

        /// <summary>本地坐标 → 格坐标；越界或落在六角星空洞一律返回 false（宁可漏点击也不误判到相邻格）。</summary>
        public bool LocalToCell(Vector3 local, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (Layout == null) return false;

            float ox = AnchorAtOrigin ? 0f : -_centerX * CellSize;
            float oy = AnchorAtOrigin ? 0f : -_centerY * CellSize;
            double lx = (local.x - ox) / CellSize;
            double ly = (local.y - oy) / CellSize;
            return Layout.TryLocalToCell(lx, ly, out x, out y);
        }

        Vector3 LocalFromLayout(double lx, double ly)
        {
            float ox = AnchorAtOrigin ? 0f : -_centerX * CellSize;
            float oy = AnchorAtOrigin ? 0f : -_centerY * CellSize;
            return new Vector3(ox + (float)lx * CellSize, oy + (float)ly * CellSize, 0f);
        }

        /// <summary>包络中心（格单位），用于「以中心居中」的锚点换算。</summary>
        void RecomputeCenter()
        {
            if (Layout == null)
            {
                _centerX = 0f;
                _centerY = 0f;
                return;
            }
            Layout.GetBounds(out double minX, out double minY, out double maxX, out double maxY);
            _centerX = (float)((minX + maxX) * 0.5);
            _centerY = (float)((minY + maxY) * 0.5);
        }

        /// <summary>运行时调整格宽（响应式布局 / 大地图缩放）。</summary>
        public void SetCellSize(float size)
        {
            if (Mathf.Approximately(size, CellSize)) return;
            CellSize = Mathf.Max(0.05f, size);
            MarkDirty();
        }
    }
}
