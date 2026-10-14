using System.Collections.Generic;
using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>天气状态（按昼夜时间确定性地轮换，无需网络/存档）。</summary>
    public enum WeatherKind : byte { Clear = 0, Overcast = 1, Rain = 2, Snow = 3, Haze = 4 }

    /// <summary>
    /// 昼夜、天气与 URP 2D 光照。
    ///
    /// 【本轮升级：从"顶点色手工调"到"真正的 2D 光照"】
    /// 历史实现只把昼夜色调写进一个静态注册表，且<b>没有任何渲染器真正读取它</b>
    /// （地形渲染器根本没设 color）——所以"昼夜"其实是"没生效"的。
    /// 现在改为使用 URP 的 <b>Light2D（Global）</b>：它由 2D 渲染器在 GPU 侧统一作用到
    /// 所有用了 Sprite-Lit-Default 的精灵上，昼夜/天气只需改这一盏灯的
    /// color/intensity，零额外 draw call、零逐对象遍历。
    ///
    /// <b>为什么用反射创建 Light2D</b>：Presentation 程序集刻意不引用 URP 程序集
    /// （见 <c>tools/verify/compile-check.sh</c> 的 L2 编译门禁只提供 UnityEngine 参考）。
    /// 反射创建既拿到了 URP 2D 光照，又不破坏"无 URP 也能编译"的架构约束；
    /// URP 缺失或类型不存在时静默降级为"无动态光照"。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldAtmosphere : MonoBehaviour
    {
        [Header("时间")]
        [Tooltip("一个完整昼夜循环的秒数。0 = 关闭昼夜。")]
        public float DayLengthSeconds = 300f;

        [Tooltip("起始时刻（0~1，0.25=日出，0.5=正午）。")]
        [Range(0f, 1f)] public float StartTime = 0.32f;

        [Header("色板")]
        public Color MidnightTint = new Color(0.42f, 0.48f, 0.78f, 1f);
        public Color DawnTint = new Color(1.00f, 0.78f, 0.62f, 1f);
        public Color NoonTint = new Color(1f, 1f, 1f, 1f);
        public Color DuskTint = new Color(1.00f, 0.70f, 0.52f, 1f);
        public Color NightTint = new Color(0.48f, 0.54f, 0.86f, 1f);

        [Header("天气")]
        [Tooltip("天气轮换周期（秒）。0 = 始终晴朗。")]
        public float WeatherPeriodSeconds = 90f;

        [Tooltip("是否尝试启用 URP 2D 全局光照（缺 URP 时自动降级）。")]
        public bool EnableLight2D = true;

        /// <summary>夜间光照强度下限（正午为 1.0）。避免夜里全黑无法游玩。</summary>
        public float NightIntensity = 0.55f;

        public Color CurrentTint { get; private set; } = Color.white;
        public float CurrentIntensity { get; private set; } = 1f;
        public WeatherKind Weather { get; private set; } = WeatherKind.Clear;

        /// <summary>0=午夜, 0.25=日出, 0.5=正午, 0.75=日落。</summary>
        public float TimeOfDay { get; private set; }

        /// <summary>URA 2D 光照是否真的挂上了（供 HUD/日志核实，避免"以为开了其实没开"）。</summary>
        public bool Light2DActive { get; private set; }

        object _light;            // UnityEngine.Rendering.Universal.Light2D
        System.Reflection.PropertyInfo _lightColor;
        System.Reflection.PropertyInfo _lightIntensity;

        void OnEnable()
        {
            TimeOfDay = StartTime;
            TryCreateLight2D();
            Apply(TimeOfDay);
        }

        void Update()
        {
            if (DayLengthSeconds > 0f)
                TimeOfDay = (TimeOfDay + Time.deltaTime / DayLengthSeconds) % 1f;
            Apply(TimeOfDay);
        }

        /// <summary>反射创建 URP 2D 全局光。失败/缺席即降级，不影响其它系统。</summary>
        void TryCreateLight2D()
        {
            if (!EnableLight2D || _light != null) return;
            var type = FindType("UnityEngine.Rendering.Universal.Light2D");
            if (type == null) return;   // 无 URP：降级
            try
            {
                var go = new GameObject("GlobalLight2D");
                go.transform.SetParent(transform, false);
                var comp = go.AddComponent(type);

                var lightTypeEnum = type.GetNestedType("LightType");
                var lightTypeProp = type.GetProperty("lightType");
                if (lightTypeEnum != null && lightTypeProp != null)
                    lightTypeProp.SetValue(comp, System.Enum.Parse(lightTypeEnum, "Global"));

                _lightColor = type.GetProperty("color");
                _lightIntensity = type.GetProperty("intensity");
                _light = comp;
                Light2DActive = true;
                Debug.Log("[ChessSage] 已启用 URP 2D 全局光照（昼夜/天气作用于全场景）。");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ChessSage] 创建 Light2D 失败，降级为无动态光照：{e.Message}");
                _light = null;
                Light2DActive = false;
            }
        }

        void Apply(float t)
        {
            CurrentTint = SampleDayCycle(t);
            CurrentIntensity = SampleIntensity(t);
            Weather = SampleWeather(t);

            // 天气修正：阴雨压暗、飘雪偏冷、雾霾降对比。
            switch (Weather)
            {
                case WeatherKind.Overcast:
                    CurrentIntensity *= 0.88f;
                    CurrentTint = Color.Lerp(CurrentTint, new Color(0.86f, 0.88f, 0.92f), 0.35f);
                    break;
                case WeatherKind.Rain:
                    CurrentIntensity *= 0.74f;
                    CurrentTint = Color.Lerp(CurrentTint, new Color(0.66f, 0.72f, 0.84f), 0.5f);
                    break;
                case WeatherKind.Snow:
                    CurrentIntensity *= 0.86f;
                    CurrentTint = Color.Lerp(CurrentTint, new Color(0.92f, 0.96f, 1f), 0.42f);
                    break;
                case WeatherKind.Haze:
                    CurrentIntensity *= 0.9f;
                    CurrentTint = Color.Lerp(CurrentTint, new Color(0.94f, 0.90f, 0.78f), 0.4f);
                    break;
            }

            // 保留注册表以兼容可能读取它的旧逻辑（当前地形/装饰已改由光照驱动）。
            OverworldTintRegistry.Tint = CurrentTint;

            if (_light != null)
            {
                try
                {
                    _lightColor?.SetValue(_light, new Color(CurrentTint.r, CurrentTint.g, CurrentTint.b, 1f));
                    _lightIntensity?.SetValue(_light, CurrentIntensity);
                }
                catch { /* 反射失败不致命，保持上一帧即可 */ }
            }
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

        /// <summary>光照强度：正午 1.0，午夜 <see cref="NightIntensity"/>。保证夜里仍看得清。</summary>
        public float SampleIntensity(float t)
        {
            float day = 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI * 2f);   // 0=午夜,1=正午
            return Mathf.Lerp(NightIntensity, 1f, day);
        }

        /// <summary>天气：按时间确定性轮换，种子固定 → 每次运行天气一致（便于回归）。</summary>
        public WeatherKind SampleWeather(float t)
        {
            if (WeatherPeriodSeconds <= 0f) return WeatherKind.Clear;
            float cycles = Time.time / WeatherPeriodSeconds + t;
            int idx = Mathf.Abs(Mathf.FloorToInt(cycles * 7f)) % 5;
            switch (idx)
            {
                case 0: return WeatherKind.Clear;
                case 1: return WeatherKind.Clear;
                case 2: return WeatherKind.Overcast;
                case 3: return WeatherKind.Haze;
                default: return (t < 0.25f || t > 0.85f) ? WeatherKind.Snow : WeatherKind.Rain;
            }
        }

        /// <summary>时间快进（剧情/调试用）。</summary>
        public void SkipHours(float hours)
        {
            TimeOfDay = Mathf.Repeat(TimeOfDay + hours / 24f, 1f);
            Apply(TimeOfDay);
        }

        public string WeatherName => Weather switch
        {
            WeatherKind.Clear => "晴",
            WeatherKind.Overcast => "阴",
            WeatherKind.Rain => "雨",
            WeatherKind.Snow => "雪",
            WeatherKind.Haze => "霾",
            _ => "晴",
        };

        static System.Type FindType(string fullName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }
    }

    /// <summary>全局色调注册表。保留以兼容旧调用方；实际昼夜由 Light2D 驱动。</summary>
    public static class OverworldTintRegistry
    {
        static Color _tint = Color.white;

        public static Color Tint
        {
            get => _tint;
            set => _tint = value;
        }

        /// <summary>大地颜色。地形基色 × 该色 = 最终受光色。</summary>
        public static Color Shade(Color baseColor) => baseColor * _tint;
    }

    /// <summary>
    /// POI 标记层：为各兴趣点生成悬浮图标（六道入口、技能 NPC、告示牌等）。
    ///
    /// <b>缺陷修复</b>：旧实现每帧读取 <c>sr.color</c> 再乘一次色调，
    /// 结果是<b>逐帧累积相乘</b>——跑几秒后所有标记都会衰减成暗红/全黑
    /// （实测截图里那个"罩在玩家头上的黑方块"之一就是这个）。
    /// 现在改为保存<b>基准色</b>，每帧从基准色重新计算最终色，绝不累积。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PoiMarkerLayer : MonoBehaviour
    {
        public OverworldMap Map;
        public float IconScale = 1.2f;

        [Tooltip("图标相对格心的抬升（世界单位），避免被地标建筑完全遮住。")]
        public float LiftY = 1.9f;

        [Tooltip("与分块渲染器一致的每格世界缩放（不乘会导致图标随距离漂离地标）。")]
        public float TileScale = 0.96f;

        readonly List<SpriteRenderer> _markers = new List<SpriteRenderer>();
        readonly List<Color> _baseColors = new List<Color>();
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
                    TerrainPalette.IsRaised(kind)) * TileScale;
                // localPosition：与地形瓦片同坐标系（见 OverworldChunkRenderer 的说明）。
                go.transform.localPosition = w + new Vector3(0f, LiftY, 0f);
                go.transform.localScale = Vector3.one * IconScale;

                // 六道入口用暖金、其余用中性白——一眼可辨。
                var baseColor = poi.Type == "realm" ? OverworldIcons.RealmColor : Color.white;
                _baseColors.Add(baseColor);
                sr.color = baseColor;
                _markers.Add(sr);
            }
        }

        void Update()
        {
            if (Map == null || _markers.Count == 0) return;
            var cam = Camera.main;

            for (int i = 0; i < _markers.Count && i < _baseColors.Count; i++)
            {
                var sr = _markers[i];
                if (sr == null) continue;

                // 从基准色重新推导（绝不基于上一帧的 color 累乘）。
                var c = _baseColors[i];
                // 远处淡出，避免地图边缘图标堆叠抢焦点。
                if (cam != null)
                {
                    float d = Vector3.Distance(cam.transform.position, sr.transform.position);
                    c.a = Mathf.Clamp01(1.7f - d / 55f);
                }
                else c.a = 1f;
                sr.color = c;

                // 轻微上下浮动，让图标"活着"。
                var p = sr.transform.localPosition;
                sr.transform.localPosition = new Vector3(p.x,
                    Mathf.Sin(Time.time * 2.2f + i) * 0.05f + p.y, p.z);
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