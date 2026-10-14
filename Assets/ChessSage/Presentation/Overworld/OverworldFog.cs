using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 探索迷雾渲染：把 <see cref="FogOfWar"/> 的探索位图烘成一张"等距投影"的纹理，
    /// 以 1 个 Sprite 绘制完成（不逐格建节点）。
    ///
    /// 【本轮修复的两个真实缺陷 —— 历史版本这里基本是坏的】
    ///   ① <b>挂在错误的物体上</b>：<c>GetComponent&lt;SpriteRenderer&gt;()</c> 落在了
    ///      OverworldBootstrap 的宿主物体上，而该物体<b>同时</b>承载 OverworldPlayer 的位移，
    ///      导致迷雾矩形<b>跟着玩家跑</b>，看起来是一个罩在角色头上的黑方块；
    ///   ② <b>缩放与朝向错误</b>：旧实现把一张 112×84 的纹理按"地图宽高"拉伸，
    ///      而等距地图的外接框是 (W+H)×(W+H)/2 的平行四边形——拉伸结果与格子完全对不上。
    ///
    /// 现在：迷雾独占一个子物体，纹理按<b>等距反投影</b>逐texel采样（每个 texel 落回一个格子），
    /// 因此在任何位置都与地形严格对齐。并加入"揭开时淡入"的过渡动画（探索反馈）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldFog : MonoBehaviour
    {
        [Header("调参")]
        [Tooltip("视野半径（格）。")]
        [Range(1, 12)] public int RevealRadius = 6;

        [Tooltip("未探索区域的不透明度。0=完全不遮，1=全黑。")]
        [Range(0f, 1f)] public float UnexploredAlpha = 0.30f;

        [Tooltip("已探索但当前不在视野内的淡出程度。")]
        [Range(0f, 1f)] public float DimmedAlpha = 0.0f;

        [Tooltip("揭开过渡时长（秒）。")]
        public float RevealFadeSeconds = 0.35f;

        [Tooltip("与分块渲染器一致的每格世界缩放。")]
        public float TileScale = 0.96f;

        [Tooltip("迷雾着色（纯黑=压暗；略带蓝紫更有夜色感）。")]
        public Color FogColor = new Color(0.04f, 0.05f, 0.10f, 1f);

        FogOfWar _fog;
        Texture2D _tex;
        SpriteRenderer _sr;
        GameObject _go;

        int _tw, _th;
        byte[] _prev;      // 上一次揭开的 alpha
        byte[] _target;    // 目标 alpha
        Color32[] _pixels;
        float _progress = 1f;

        int _lastCx = int.MinValue, _lastCy = int.MinValue;

        public FogOfWar State => _fog;

        public void Initialize(OverworldMap map)
        {
            _fog = new FogOfWar(map);
            _tw = map.Width + map.Height - 1;
            _th = _tw;

            _tex = new Texture2D(_tw, _th, TextureFormat.RGBA32, mipChain: false)
            {
                name = "FogMaskIso",
                filterMode = FilterMode.Bilinear,   // 双线性 → 揭开边缘柔和，不出现锯齿
                wrapMode = TextureWrapMode.Clamp,
            };
            _pixels = new Color32[_tw * _th];
            _prev = new byte[_tw * _th];
            _target = new byte[_tw * _th];

            // 独占子物体：绝不能再和玩家共用 transform（那正是历史缺陷）。
            _go = new GameObject("FogMask");
            _go.transform.SetParent(transform, false);
            _sr = _go.AddComponent<SpriteRenderer>();
            _sr.sprite = Sprite.Create(_tex, new Rect(0, 0, _tw, _th),
                new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _sr.sortingOrder = 2000;
            _sr.color = Color.white;

            // 等距反投影：纹理坐标轴与等距网格成 45°，故 y 方向额外压 1/2（2:1 等距）。
            _go.transform.localScale = new Vector3(TileScale, TileScale * IsoGrid.TileH / IsoGrid.TileW, 1f);
            // 用 localPosition：雾层挂在组合根之下，与地形瓦片同一坐标系；
            // 若用世界坐标 position，会因根节点随玩家移动而与地形错位。
            _go.transform.localPosition = new Vector3(
                (map.Width - map.Height) * 0.5f * TileScale,
                (_th * 0.5f - 0.5f) * IsoGrid.TileH * TileScale,
                5f);

            BuildTarget();
            System.Array.Copy(_target, _prev, _target.Length);
            _progress = 1f;
            UploadAll();
        }

        /// <summary>逐 texel 反投影回格子，采样探索状态。</summary>
        void BuildTarget()
        {
            var map = _fog.MapRef;
            int h1 = map.Height - 1;
            for (int row = 0; row < _th; row++)
            {
                for (int col = 0; col < _tw; col++)
                {
                    int sx = col - h1;                 // (x - y)
                    int cx = (sx + row) / 2;           // 逆等距
                    int cy = (row - sx) / 2;
                    float a = UnexploredAlpha;
                    if (map.InBounds(cx, cy))
                        a = _fog.IsExplored(cx, cy) ? DimmedAlpha : UnexploredAlpha;
                    _target[row * _tw + col] = (byte)(a * 255f);
                }
            }
        }

        /// <summary>揭示玩家周围视野。仅在玩家跨格时实际改动纹理。</summary>
        public void Reveal(int cx, int cy)
        {
            if (_fog == null) return;
            if (cx == _lastCx && cy == _lastCy) return;
            _lastCx = cx;
            _lastCy = cy;

            _fog.RevealAround(cx, cy, RevealRadius);

            // 把"当前显示值"作为动画起点，再设新目标——避免重复揭示时跳变。
            if (_progress < 1f) Blend(1f);
            System.Array.Copy(_target, _prev, _target.Length);
            BuildTarget();
            _progress = 0f;
        }

        void Update()
        {
            if (_fog == null || _progress >= 1f) return;
            _progress = Mathf.Min(1f, _progress + Time.deltaTime / Mathf.Max(0.01f, RevealFadeSeconds));
            Blend(_progress);
        }

        void Blend(float t)
        {
            var fog = (Color32)FogColor;
            for (int i = 0; i < _pixels.Length; i++)
            {
                float a = Mathf.Lerp(_prev[i], _target[i], t);
                _pixels[i] = new Color32(fog.r, fog.g, fog.b, (byte)a);
            }
            _tex.SetPixels32(_pixels);
            _tex.Apply(false, false);
        }

        void UploadAll()
        {
            var fog = (Color32)FogColor;
            for (int i = 0; i < _pixels.Length; i++)
                _pixels[i] = new Color32(fog.r, fog.g, fog.b, _target[i]);
            _tex.SetPixels32(_pixels);
            _tex.Apply(false, false);
        }

        void OnDestroy()
        {
            if (_tex != null) Destroy(_tex);
        }
    }
}
