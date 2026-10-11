using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 探索迷雾渲染：把 <see cref="FogOfWar"/> 的探索位图烘成一张 112×84 的 Texture2D，
    /// 每格 1像素，未探索处压暗至半透明黑。以 1 个 Sprite 绘制完成，
    /// 相比逐格 GameObject 的做法节省 9400 个节点。
    ///
    /// 采用「原地更新单个像素」而非整张重传：每步只改约 40 像素，
    /// 移动时CPU 与 GPU 开销都可忽略。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldFog : MonoBehaviour
    {
        [Header("调参")]
        [Tooltip("视野半径（格）。")]
        [Range(1, 8)] public int RevealRadius = 4;

        [Tooltip("未探索区域的不透明度。")]
        [Range(0f, 1f)] public float UnexploredAlpha = 0.72f;

        [Tooltip("已探索但当前不在视野内的淡出程度。")]
        [Range(0f, 1f)] public float DimmedAlpha = 0.28f;

        FogOfWar _fog;
        Texture2D _tex;
        SpriteRenderer _sr;
        Color32[] _pixels;
        int _lastCx = int.MinValue, _lastCy = int.MinValue;

        /// <summary>底层的探索状态（与逻辑层共享）。</summary>
        public FogOfWar State => _fog;

        public void Initialize(OverworldMap map)
        {
            _fog = new FogOfWar(map);
            _tex = new Texture2D(map.Width, map.Height, TextureFormat.RGBA32, mipChain: false)
            {
                name = "FogMask",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _pixels = new Color32[map.Width * map.Height];

            if (_sr == null) _sr = GetComponent<SpriteRenderer>();
            if (_sr == null) _sr = gameObject.AddComponent<SpriteRenderer>();

            _sr.sprite = Sprite.Create(_tex, new Rect(0, 0, map.Width, map.Height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _sr.sortingOrder = 2000;

            // 迷雾平面覆盖整张等距地图：其范围即所有格子世界坐标的包围盒。
            float w = (map.Width + map.Height) * IsoGrid.TileW;
            float h = (map.Width + map.Height) * IsoGrid.TileH;
            transform.localScale = new Vector3(w / map.Width, h / map.Height, 1f);
            transform.position = new Vector3(0f, 0f, 5f);

            RefreshAll();
        }

        /// <summary>揭示玩家周围视野。仅在玩家跨格时实际改动纹理。</summary>
        public void Reveal(int cx, int cy)
        {
            if (_fog == null) return;
            if (cx == _lastCx && cy == _lastCy) return;

            _fog.RevealAround(cx, cy, RevealRadius);
            PaintRegion(cx, cy, RevealRadius + 1);
            _lastCx = cx;
            _lastCy = cy;
        }

        /// <summary>只重绘变化区域内的像素（差分更新）。</summary>
        void PaintRegion(int cx, int cy, int r)
        {
            var map = _fog.MapRef;
            int x0 = Mathf.Max(0, cx - r), x1 = Mathf.Min(map.Width - 1, cx + r);
            int y0 = Mathf.Max(0, cy - r), y1 = Mathf.Min(map.Height - 1, cy + r);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float a = _fog.IsExplored(x, y) ? DimmedAlpha : UnexploredAlpha;
                    _pixels[y * map.Width + x] = new Color32(0, 0, 0, (byte)(a * 255));
                }
            }
            _tex.SetPixels32(_pixels);
            _tex.Apply(false, false);
        }

        public void RefreshAll()
        {
            if (_fog == null) return;
            var map = _fog.MapRef;
            for (int i = 0; i < _pixels.Length; i++)
                _pixels[i] = new Color32(0, 0, 0, (byte)(UnexploredAlpha * 255));
            _tex.SetPixels32(_pixels);
            _tex.Apply(false, false);
        }

        void OnDestroy()
        {
            if (_tex != null) Destroy(_tex);
        }
    }
}