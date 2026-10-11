using System.Collections.Generic;
using ChessSage.Core.Model;
using UnityEngine;

namespace ChessSage.Presentation.Board
{
    /// <summary>
    /// 通用棋盘视图：把 <see cref="BoardState"/> 渲染成 2D 棋子层。
    ///
    /// 设计要点（这是"新增棋类无需改动表现层"的关键）：
    ///   · 完全<b>数据驱动</b>——格数取自 <see cref="VariantRuleBase"/> 的 Width/Height，
    ///     不含任何棋类特判，因此六种棋类共用同一份实现。
    ///   · <b>对象池</b>——棋子 GameObject 复用，避免每回合 Instantiate/Destroy 造成 GC 抖动。
    ///   · <b>脏标记刷新</b>——只有 <see cref="MarkDirty"/> 被调用才重建，Update 阶段零分配。
    ///   · 坐标转换集中于 <see cref="CellToLocal"/> / <see cref="LocalToCell"/>，
    ///     等距/透视大地图只需替换棋盘内部的视图，不影响调用方。
    /// </summary>
    public sealed class BoardView : MonoBehaviour
    {
        [Header("布局")]
        [Tooltip("单格边长（世界单位）。")]
        public float CellSize = 1f;

        [Tooltip("棋盘锚点：true 表示 (0,0) 格在原点；false 表示棋盘以原点居中。")]
        public bool AnchorAtOrigin = false;

        [Header("引用")]
        [Tooltip("棋子父节点；留空则自动挂到本节点下。")]
        public Transform PieceRoot;

        PieceView _poolRootView;
        readonly List<PieceView> _pool = new List<PieceView>();
        readonly List<PieceView> _active = new List<PieceView>();

        bool _dirty = true;
        int _lastWidth = -1;
        int _lastHeight = -1;

        /// <summary>待渲染的棋盘状态。由 <c>GameScreen</c> 在每回合结束时注入。</summary>
        public BoardState State { get; private set; }

        /// <summary>当前棋盘格数，供输入层做边界判断。</summary>
        public int Width { get; private set; } = 9;
        public int Height { get; private set; } = 10;

        public void Bind(BoardState state, int width, int height)
        {
            State = state;
            Width = width;
            Height = height;
            _lastWidth = width;
            _lastHeight = height;
            MarkDirty();
        }

        /// <summary>请求下一帧重建棋子层。由外部在数据变更后调用。</summary>
        public void MarkDirty() => _dirty = true;

        void Update()
        {
            if (_dirty) Rebuild();
        }

        // ── 重建 ──────────────────────────────────────────────────────────────

        void Rebuild()
        {
            _dirty = false;
            EnsurePoolRoot();

            // 先全部回收，再按需取出——顺序即绘制层级，不需要额外排序。
            for (int i = 0; i < _active.Count; i++) _active[i].gameObject.SetActive(false);
            _active.Clear();

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
            go.transform.SetParent(PieceRoot != null ? PieceRoot : _poolRootView.transform, false);
            var view = go.AddComponent<PieceView>();
            _pool.Add(view);
            return view;
        }

        void EnsurePoolRoot()
        {
            if (PieceRoot == null)
            {
                var go = new GameObject("PieceRoot");
                go.transform.SetParent(transform, false);
                PieceRoot = go.transform;
            }
        }

        // ── 坐标 ──────────────────────────────────────────────────────────────

        /// <summary>格坐标 → 本地坐标。棋盘以原点居中（AnchorAtOrigin=false）时整体偏移半格。</summary>
        public Vector3 CellToLocal(int x, int y)
        {
            float ox = AnchorAtOrigin ? 0f : -(Width - 1) * CellSize * 0.5f;
            float oy = AnchorAtOrigin ? 0f : -(Height - 1) * CellSize * 0.5f;
            return new Vector3(ox + x * CellSize, oy + y * CellSize, 0f);
        }

        /// <summary>本地坐标 → 格坐标；越界返回 false。</summary>
        public bool LocalToCell(Vector3 local, out int x, out int y)
        {
            float ox = AnchorAtOrigin ? 0f : (Width - 1) * CellSize * 0.5f;
            float oy = AnchorAtOrigin ? 0f : (Height - 1) * CellSize * 0.5f;
            x = Mathf.RoundToInt((local.x - ox) / CellSize);
            y = Mathf.RoundToInt((local.y - oy) / CellSize);
            return x >= 0 && x < Width && y >= 0 && y < Height;
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