using System.Collections.Generic;
using UnityEngine;

namespace ChessSage.Presentation.Board
{
    /// <summary>
    /// 落点标记层：显示当前选中棋子的可走点 / 可吃子点。
    /// 与 <see cref="BoardView"/> 一样是对象池化 + 脏标记驱动，不产生每帧分配。
    /// </summary>
    public sealed class HighlightLayer : MonoBehaviour
    {
        public float MarkerScale = 0.42f;

        SpriteRenderer _markerPrefab;
        readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _active = new List<SpriteRenderer>();
        Transform _root;
        bool _dirty = true;

        public void MarkDirty() => _dirty = true;

        void Update()
        {
            if (_dirty) Rebuild();
        }

        /// <summary>设置可走点。capture 为 true 时用红色标记（可吃子）。</summary>
        public void SetMoves(IReadOnlyList<Vector3> moves, IReadOnlyList<bool> capture)
        {
            EnsureRoot();
            for (int i = 0; i < _active.Count; i++) _active[i].gameObject.SetActive(false);
            _active.Clear();

            if (moves == null) { MarkDirty(); return; }

            for (int i = 0; i < moves.Count; i++)
            {
                var mr = Rent();
                bool isCapture = capture != null && i < capture.Count && capture[i];
                mr.sprite = isCapture ? Art.PlaceholderArt.DotCapture : Art.PlaceholderArt.DotMove;
                // 传入的是世界坐标，标记层自身保持原点不动。
                mr.transform.position = moves[i];
                mr.transform.localScale = Vector3.one * MarkerScale;
                mr.color = Color.white;
                mr.gameObject.SetActive(true);
                _active.Add(mr);
            }
            MarkDirty();
        }

        public void Clear()
        {
            for (int i = 0; i < _active.Count; i++) _active[i].gameObject.SetActive(false);
            _active.Clear();
        }

        void Rebuild()
        {
            _dirty = false;
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