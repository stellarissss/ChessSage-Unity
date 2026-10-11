using ChessSage.Core.Model;
using ChessSage.Presentation.Art;
using UnityEngine;

namespace ChessSage.Presentation.Board
{
    /// <summary>
    /// 单个棋子的表现层组件。只负责"把一个 <see cref="Piece"/> 画出来"，
    /// 不含任何规则判断——走法合法性一律由 <c>ChessSage.Core</c> 决定。
    /// </summary>
    public sealed class PieceView : MonoBehaviour
    {
        SpriteRenderer _renderer;
        Transform _tf;
        Piece _piece;

        public Piece Piece => _piece;

        void Awake()
        {
            _tf = transform;
            _renderer = GetComponent<SpriteRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<SpriteRenderer>();
            // 棋子排序：越大越晚绘制；实际层级由生成顺序保证，此处仅留可调基准。
            _renderer.sortingOrder = 10;
        }

        public void Bind(Piece piece, Vector3 localPosition)
        {
            _piece = piece;
            _tf.localPosition = localPosition;
            var sprite = PlaceholderArt.Piece(piece.Side, piece.Type);
            if (_renderer.sprite != sprite) _renderer.sprite = sprite;
            gameObject.name = string.IsNullOrEmpty(piece.Id) ? piece.Type : piece.Id;
        }

        public void SetHighlighted(bool on)
        {
            // 选中态用色彩提亮表达，避免额外对象。
            var c = _renderer.color;
            c = on ? new Color(1.15f, 1.05f, 0.65f, 1f) : Color.white;
            _renderer.color = c;
        }

        /// <summary>强制设置精灵，供导入正式美术后替换占位图。</summary>
        public void SetSprite(Sprite sprite)
        {
            if (sprite != null && _renderer != null) _renderer.sprite = sprite;
        }
    }
}