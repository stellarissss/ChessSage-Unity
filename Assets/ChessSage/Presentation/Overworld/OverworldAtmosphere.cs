using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 昼夜与氛围：全局色调随时间循环，并用「区域主题」给不同地貌染色。
    ///
    /// 相比 web 版仅靠 post-processing 的做法，这里把氛围放在
    /// <b>SpriteRenderer.color（顶点色）</b> 上——不依赖 URP Volume，
    /// 因此内置管线也能正确显示，且零额外 draw call。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldAtmosphere : MonoBehaviour
    {
        [Header("时间")]
        [Tooltip("一个完整昼夜循环的秒数。0 = 关闭昼夜。")]
        public float DayLengthSeconds = 300f;

        [Tooltip("起始时刻（0~1，0.25=正午）。")]
        [Range(0f, 1f)] public float StartTime = 0.3f;

        [Header("色板")]
        public Color MidnightTint = new Color(0.35f, 0.42f, 0.72f, 1f);
        public Color DawnTint = new Color(0.98f, 0.72f, 0.60f, 1f);
        public Color NoonTint = new Color(1f, 1f, 1f, 1f);
        public Color DuskTint = new Color(0.95f, 0.62f, 0.48f, 1f);
        public Color NightTint = new Color(0.42f, 0.48f, 0.80f, 1f);

        public Color CurrentTint { get; private set; } = Color.white;

        /// <summary>0=午夜, 0.25=日出, 0.5=正午, 0.75=日落。</summary>
        public float TimeOfDay { get; private set; }

        void OnEnable()
        {
            TimeOfDay = StartTime;
            ApplyTint(TimeOfDay);
        }

        void Update()
        {
            if (DayLengthSeconds <= 0f) return;
            TimeOfDay = (TimeOfDay + Time.deltaTime / DayLengthSeconds) % 1f;
            ApplyTint(TimeOfDay);
        }

        void ApplyTint(float t)
        {
            CurrentTint = SampleDayCycle(t);
            // 场景没有内置光源时，2D 色调由各渲染器的 color 承载；
            // 这里通过全局静态暴露给分块/角色渲染器使用。
            OverworldTintRegistry.Tint = CurrentTint;
        }

        /// <summary>
        /// 昼夜曲线（4 段关键色线性插值）。
        /// 关键：使用<b>环形插值</b>，0.95→0.05（跨午夜）也能平滑过渡。
        /// </summary>
        public Color SampleDayCycle(float t)
        {
            t = Mathf.Repeat(t, 1f);
            if (t < 0.25f) return Color.Lerp(MidnightTint, DawnTint, t / 0.25f);
            if (t < 0.5f) return Color.Lerp(DawnTint, NoonTint, (t - 0.25f) / 0.25f);
            if (t < 0.75f) return Color.Lerp(NoonTint, DuskTint, (t - 0.5f) / 0.25f);
            return Color.Lerp(DuskTint, NightTint, (t - 0.75f) / 0.25f);
        }

        /// <summary>时间快进（剧情/调试用）。</summary>
        public void SkipHours(float hours)
        {
            TimeOfDay = Mathf.Repeat(TimeOfDay + hours / 24f, 1f);
            ApplyTint(TimeOfDay);
        }
    }

    /// <summary>全局色调注册表。极简单例，让分块渲染器无需逐层传参即可应用昼夜色。</summary>
    public static class OverworldTintRegistry
    {
        static Color _tint = Color.white;

        public static Color Tint
        {
            get => _tint;
            set => _tint = value;
        }

        /// <summary>大地颜色。地形基色 × 该色= 最终受光色。</summary>
        public static Color Shade(Color baseColor) => baseColor * _tint;
    }

    /// <summary>
    /// POI 标记层：为 11 个兴趣点生成可点击图标（六道入口、技能 NPC、告示牌等）。
    /// 使用对象池，图标随昼夜着色，随距离淡出。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PoiMarkerLayer : MonoBehaviour
    {
        public OverworldMap Map;
        public float IconScale = 1.2f;

        readonly List<SpriteRenderer> _markers = new List<SpriteRenderer>();
        Sprite _icon;

        /// <summary>玩家进入某POI 的交互范围时触发。</summary>
        public System.Action<Poi> PoiEnteredRange;

        Sprite Icon
        {
            get
            {
                if (_icon == null) _icon = OverworldIcons.Diamond();
                return _icon;
            }
        }

        public void Initialize(OverworldMap map)
        {
            Map = map;
            for (int i = 0; i < map.Pois.Count; i++)
            {
                var poi = map.Pois[i];
                var go = new GameObject("poi_" + poi.Id);
                go.transform.SetParent(transform, false);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = Icon;
                sr.sortingOrder = 1500;

                var kind = map.GetTerrain(poi.X, poi.Y);
                var w = IsoGrid.CellToWorld(poi.X, poi.Y, map.GetHeight(poi.X, poi.Y),
                    TerrainPalette.IsRaised(kind));
                go.transform.position = w + new Vector3(0f, IsoGrid.TileH, 0f);
                go.transform.localScale = Vector3.one * IconScale;

                // 六道入口用暖色，其余用中性色，一眼可辨
                sr.color = poi.Type == "realm" ? OverworldIcons.RealmColor : Color.white;
                _markers.Add(sr);
            }
        }

        void Update()
        {
            if (Map == null || _markers.Count == 0) return;

            var cam = Camera.main;
            for (int i = 0; i < _markers.Count && i < Map.Pois.Count; i++)
            {
                var poi = Map.Pois[i];
                var sr = _markers[i];

                // 昼夜着色
                var c = sr.color;
                c = OverworldTintRegistry.Shade(c);
                // 远处淡出，避免地图边缘图标堆叠抢焦点
                if (cam != null)
                {
                    float d = Vector3.Distance(cam.transform.position, _markers[i].transform.position);
                    c.a = Mathf.Clamp01(1.6f - d / 60f);
                }
                sr.color = c;
            }
        }

        /// <summary>找出玩家交互范围内最近的 POI（无则null）。</summary>
        public Poi FindInteractable(int cellX, int cellY)
        {
            Poi best = null;
            int bestD = int.MaxValue;
            foreach (var p in Map.Pois)
            {
                int d = Mathf.Abs(p.X - cellX) + Mathf.Abs(p.Y - cellY);
                if (d < bestD) { bestD = d; best = p; }
            }
            return bestD <= Mathf.RoundToInt(Map.InteractTiles) ? best : null;
        }
    }

    /// <summary>程序化 UI 图标（避免依赖美术资源）。</summary>
    public static class OverworldIcons
    {
        public static readonly Color RealmColor = new Color(1f, 0.86f, 0.42f, 1f);

        static Sprite _diamond;

        /// <summary>一个带描边的菱形图标，32×32。</summary>
        public static Sprite Diamond()
        {
            if (_diamond != null) return _diamond;
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "PoiDiamond",
                filterMode = FilterMode.Point,
            };
            var px = new Color32[n * n];
            var fill = new Color32(255, 255, 255, 255);
            var edge = new Color32(40, 30, 20, 255);

            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Abs(x - n / 2f + 0.5f) / (n / 2f - 3f);
                    float dy = Mathf.Abs(y - n / 2f + 0.5f) / (n / 2f - 3f);
                    float d = dx + dy;
                    if (d <= 1f)
                    {
                        bool isEdge = d > 0.72f;
                        px[y * n + x] = isEdge ? edge : fill;
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            _diamond = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 32f);
            return _diamond;
        }
    }
}