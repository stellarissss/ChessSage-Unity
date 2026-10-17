using System.Collections.Generic;
using ChessSage.Presentation.Art;
using UnityEngine;

namespace ChessSage.Presentation.Board
{
    /// <summary>
    /// 落点标记层：显示当前选中棋子的可走点 / 可吃子点。
    /// 与 <see cref="BoardView"/> 一样是对象池化 + 脏标记驱动，不产生每帧分配。
    ///
    /// 【P1-4 为什么存格坐标而不是世界坐标】旧实现直接把世界坐标存进 marker：
    /// 一旦格宽在设置高亮<b>之后</b>被改写（从 hub 进入围棋/五子棋/黑白棋时
    /// <c>FitBoardToCamera</c> 会缩小 cellSize），高亮仍按旧格宽算出的位置摆放——
    /// 实测围棋绿点整体偏移 1.79×、黑白棋绿点全落占据格。现在只存「格坐标」，
    /// 世界坐标在 <see cref="Rebuild"/> 里经 <see cref="Board"/> 的
    /// <c>CellToLocal + TransformPoint</c> 现算；格宽变化时 <see cref="MarkDirty"/>
    /// 触发重算（本类亦自行监听 cellSize，双保险），从此「棋盘缩放了、绿点还在原地」
    /// 这类问题在结构上不可能发生。
    /// </summary>
    public sealed class HighlightLayer : MonoBehaviour
    {
        public float MarkerScale = 0.42f;

        [Tooltip("棋盘视图：格坐标 → 世界坐标的换算真源。由组合根 Bootstrap 注入。")]
        public BoardView Board;

        // 只存格坐标 + 可吃子标记；世界坐标一律现算（见类注释）。
        readonly List<Vector2Int> _cells = new List<Vector2Int>();
        readonly List<bool> _captureFlags = new List<bool>();
        readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _active = new List<SpriteRenderer>();
        Transform _root;
        bool _dirty = true;
        float _lastCellSize = float.NaN;

        public void MarkDirty() => _dirty = true;

        void Update()
        {
            // 自行监听格宽变化（双保险之一：Bootstrap 改格宽后也会显式 MarkDirty）。
            // 覆盖「任何外部改写 cellSize」的路径，不依赖调用方记得通知本层。
            if (Board != null && !Mathf.Approximately(_lastCellSize, Board.CellSize))
            {
                _lastCellSize = Board.CellSize;
                MarkDirty();
            }
            if (_dirty) Rebuild();
        }

        /// <summary>设置可走点（格坐标）。capture 为 true 时用红色标记（可吃子）。</summary>
        public void SetCells(IReadOnlyList<Vector2Int> cells, IReadOnlyList<bool> capture)
        {
            _cells.Clear();
            _captureFlags.Clear();
            if (cells != null)
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    _cells.Add(cells[i]);
                    _captureFlags.Add(capture != null && i < capture.Count && capture[i]);
                }
            }
            if (Board != null) _lastCellSize = Board.CellSize;
            Rebuild();   // 同步重建，保持旧实现的即时性（不等下一帧）
        }

        public void Clear()
        {
            _cells.Clear();
            _captureFlags.Clear();
            for (int i = 0; i < _active.Count; i++) _active[i].gameObject.SetActive(false);
            _active.Clear();
        }

        /// <summary>
        /// 按当前格宽重算全部标记的世界坐标。数据变更（<see cref="SetCells"/>）或
        /// 格宽变化（<see cref="MarkDirty"/>）时调用；对象池复用，无逐帧分配。
        /// </summary>
        void Rebuild()
        {
            _dirty = false;
            if (_root != null)
            {
                for (int i = 0; i < _active.Count; i++) _active[i].gameObject.SetActive(false);
                _active.Clear();
            }
            if (_cells.Count == 0) return;

            EnsureRoot();
            for (int i = 0; i < _cells.Count; i++)
            {
                var mr = Rent();
                mr.sprite = _captureFlags[i] ? PlaceholderArt.DotCapture : PlaceholderArt.DotMove;
                // 世界坐标现算：棋盘移动/缩放（cellSize 改写）后标记自动跟着格心走。
                mr.transform.position = CellToWorld(_cells[i]);
                mr.transform.localScale = Vector3.one * MarkerScale;
                mr.color = Color.white;
                mr.gameObject.SetActive(true);
                _active.Add(mr);
            }
        }

        Vector3 CellToWorld(Vector2Int cell)
        {
            if (Board == null) return Vector3.zero;
            return Board.transform.TransformPoint(Board.CellToLocal(cell.x, cell.y));
        }

        SpriteRenderer Rent()
        {
            for (int i = 0; i < _pool.Count; i++)
                if (!_pool[i].gameObject.activeSelf) return _pool[i];

            var go = new GameObject("hl");
            go.transform.SetParent(_root, false);
            var mr = go.AddComponent<SpriteRenderer>();
            mr.sortingOrder = 5;
            _pool.Add(mr);
            return mr;
        }

        void EnsureRoot()
        {
            if (_root != null) return;
            var go = new GameObject("HighlightRoot");
            go.transform.SetParent(transform, false);
            _root = go.transform;
        }
    }
}
