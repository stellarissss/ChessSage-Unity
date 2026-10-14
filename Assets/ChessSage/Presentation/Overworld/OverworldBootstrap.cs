using System.IO;
using ChessSage.Core.Config;
using ChessSage.Core.Overworld;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 大地图组合根：加载 <c>Configs/global/overworld.json</c> 并搭起整套表现层。
    ///
    /// 与棋盘的 <c>Bootstrap</c> 平级，但两者互不依赖——大地图是对局之外的独立场景。
    /// 需要进入对局时，由 <see cref="OverworldExitRequest"/> 事件通知上层切场景，
    /// 避免表现层直接引用 <c>SceneManager.LoadScene</c>（保持可测试性）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldBootstrap : MonoBehaviour
    {
        [Header("配置")]
        [Tooltip("相对 Configs 的路径。")]
        public string ConfigRelativePath = "global/overworld";

        [Header("视图调参")]
        [Range(8, 40)] public int LoadMarginCells = 18;
        [Range(1, 12)] public int FogRevealRadius = 6;
        public bool EnableDayNight = true;
        public bool EnableWeather = true;
        public float DayLengthSeconds = 300f;

        [Tooltip("装饰布点种子（0 = 用地图自带种子，保证同图同貌）。")]
        public int DecorSeedOverride = 0;

        public OverworldMap Map { get; private set; }
        public OverworldChunkRenderer Chunks { get; private set; }
        public OverworldDecorRenderer Decor { get; private set; }
        public OverworldRoadRenderer Roads { get; private set; }
        public OverworldLandmarkRenderer Landmarks { get; private set; }
        public OverworldPlayer Player { get; private set; }
        public OverworldCamera CameraRig { get; private set; }
        public OverworldFog Fog { get; private set; }
        public PoiMarkerLayer PoiLayer { get; private set; }
        public OverworldAtmosphere Atmosphere { get; private set; }
        public OverworldPerfStats Perf { get; private set; }

        /// <summary>装饰布点数量（供 HUD 与取证核对）。</summary>
        public int DecorTotal { get; private set; }

        /// <summary>玩家请求进入某 POI（六道入口等）。上层据此切到对局场景。</summary>
        public event System.Action<Poi> RealmEntered;

        /// <summary>提示信息（HUD 显示）。</summary>
        public string LastHint { get; private set; } = "";

        /// <summary>探索里程碑横幅（短暂显示，如"探索度 25%"）。</summary>
        string _milestone = "";
        float _milestoneUntil;
        bool _m25, _m50, _m75, _m100;

        void Awake()
        {
            if (!TryLoadMap()) return;

            // 排序口径必须在任何渲染层建对象之前定好（远小近大，见 OverworldSort）。
            OverworldSort.MaxDiag = Map.Width + Map.Height;

            // 相机（正交）
            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
            }
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.10f, 0.16f, 1f);

            // 分块渲染
            Chunks = FindObjectOfType<OverworldChunkRenderer>();
            if (Chunks == null) Chunks = gameObject.AddComponent<OverworldChunkRenderer>();
            Chunks.Map = Map;
            Chunks.LoadMarginCells = LoadMarginCells;
            Chunks.Initialize(Map);

            // 装饰（Core 侧 Poisson 布点 → 表现层分块实例化）
            int decorSeed = DecorSeedOverride != 0 ? DecorSeedOverride : Map.Seed;
            var stats = new DecorPlanStats();
            var plan = DecorPlanner.Plan(Map, decorSeed, stats);
            DecorTotal = plan.Count;

            Decor = FindObjectOfType<OverworldDecorRenderer>();
            if (Decor == null) Decor = gameObject.AddComponent<OverworldDecorRenderer>();
            Decor.Map = Map;
            Decor.LoadMarginCells = LoadMarginCells;
            Decor.Initialize(Map, plan);

            // 道路（官道，作为探索引导线）
            Roads = FindObjectOfType<OverworldRoadRenderer>();
            if (Roads == null) Roads = gameObject.AddComponent<OverworldRoadRenderer>();
            Roads.Map = Map;
            Roads.LoadMarginCells = LoadMarginCells;
            Roads.Initialize(Map);

            // 地标建筑（六道入口的视觉锚点）
            Landmarks = FindObjectOfType<OverworldLandmarkRenderer>();
            if (Landmarks == null) Landmarks = gameObject.AddComponent<OverworldLandmarkRenderer>();
            Landmarks.Map = Map;
            Landmarks.Initialize(Map);

            // 迷雾
            Fog = FindObjectOfType<OverworldFog>();
            if (Fog == null) Fog = gameObject.AddComponent<OverworldFog>();
            Fog.RevealRadius = FogRevealRadius;
            Fog.Initialize(Map);

            // POI
            PoiLayer = FindObjectOfType<PoiMarkerLayer>();
            if (PoiLayer == null) PoiLayer = gameObject.AddComponent<PoiMarkerLayer>();
            PoiLayer.Map = Map;
            PoiLayer.TileScale = Chunks.TileScale;
            PoiLayer.Initialize(Map);

            // 玩家：<b>必须独占一个 GameObject</b>。
            // 历史实现把 OverworldPlayer 也挂在组合根上，于是"玩家位移"和"世界根位移"
            // 是同一次写入——相机、地形、角色的坐标系全部纠缠在一起（相机因此永远无法真正跟随，
            // 只能靠"世界不动、相机不动"勉强出图）。现在世界根保持原点静止，玩家独立移动，相机追玩家。
            Player = FindObjectOfType<OverworldPlayer>();
            if (Player == null)
            {
                var playerGo = new GameObject("Player");
                playerGo.transform.SetParent(transform, false);
                Player = playerGo.AddComponent<OverworldPlayer>();
            }
            Player.Map = Map;
            Player.Cam = cam;
            Player.Chunks = Chunks;
            Player.Fog = Fog;
            Player.TileScale = Chunks.TileScale;
            Player.Initialize(Map);
            Player.ArrivedAt += OnPlayerArrived;

            // 相机
            CameraRig = FindObjectOfType<OverworldCamera>();
            if (CameraRig == null) CameraRig = gameObject.AddComponent<OverworldCamera>();
            CameraRig.Target = Player;
            CameraRig.Map = Map;
            CameraRig.Cam2D = cam;
            CameraRig.TileScale = Chunks.TileScale;

            // 昼夜 / 天气 / 2D 光照
            if (EnableDayNight)
            {
                Atmosphere = FindObjectOfType<OverworldAtmosphere>();
                if (Atmosphere == null) Atmosphere = gameObject.AddComponent<OverworldAtmosphere>();
                Atmosphere.DayLengthSeconds = DayLengthSeconds;
                Atmosphere.WeatherPeriodSeconds = EnableWeather ? 90f : 0f;
            }

            // 性能面板（硬指标：必须实测）
            Perf = FindObjectOfType<OverworldPerfStats>();
            if (Perf == null) Perf = gameObject.AddComponent<OverworldPerfStats>();
            Perf.Chunks = Chunks;
            Perf.Decor = Decor;
            Perf.Roads = Roads;
            Perf.Landmarks = Landmarks;

            LastHint = $"六道大陆已加载：{Map.Width}×{Map.Height}，{Map.Regions.Count} 区域，" +
                       $"{Map.Pois.Count} 处地标，装饰 {DecorTotal}，河流 {Map.Rivers.Count}，湖泊 {Map.Lakes.Count}";
            Debug.Log("[ChessSage] " + LastHint);
        }

        bool TryLoadMap()
        {
            try
            {
                var path = Path.Combine(Bootstrap.ConfigsRoot(), ConfigRelativePath + ".json");
                if (!File.Exists(path))
                {
                    Debug.LogError($"[ChessSage] 缺少大地图配置：{path}");
                    return false;
                }
                Map = OverworldMap.FromJson(JObject.Parse(File.ReadAllText(path)));
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ChessSage] 大地图加载失败：{e.Message}");
                return false;
            }
        }

        void OnPlayerArrived(int x, int y)
        {
            var poi = PoiLayer != null ? PoiLayer.FindInteractable(x, y) : null;
            if (poi == null) return;

            LastHint = $"{poi.Label}（{poi.Realm}）— 按 E 进入";

            if (poi.Type == "realm")
                RealmEntered?.Invoke(poi);
        }

        /// <summary>由输入层调用（E 键）：进入当前交互范围内的 POI。</summary>
        public void Interact()
        {
            if (Player == null || PoiLayer == null) return;
            var poi = PoiLayer.FindInteractable(Player.CellX, Player.CellY);
            if (poi == null) { LastHint = "附近没有可交互目标"; return; }

            switch (poi.Type)
            {
                case "realm":
                    RealmEntered?.Invoke(poi);
                    break;
                case "spawn":
                    LastHint = "轮回起点";
                    break;
                default:
                    LastHint = $"{poi.Label}（{poi.Type}）— 功能开发中";
                    break;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.E)) Interact();
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f && CameraRig != null) CameraRig.Zoom(scroll);

            // 装饰/道路与地形共用同一套窗口规则，这里用玩家当前格驱动（幂等，多调无害）。
            if (Player != null)
            {
                if (Decor != null) Decor.SyncViewport(Player.CellX, Player.CellY);
                if (Roads != null) Roads.SyncViewport(Player.CellX, Player.CellY);
            }
            CheckMilestones();
        }

        /// <summary>探索度跨越 25/50/75/100% 时给出瞬时反馈（"过关"的成就感）。</summary>
        void CheckMilestones()
        {
            if (Fog == null || Fog.State == null) return;
            float r = Fog.State.ExploredRatio();
            if (!_m25 && r >= 0.25f) { _m25 = true; ShowMilestone("探索度 25% · 初见六道"); }
            else if (!_m50 && r >= 0.50f) { _m50 = true; ShowMilestone("探索度 50% · 半壁江山"); }
            else if (!_m75 && r >= 0.75f) { _m75 = true; ShowMilestone("探索度 75% · 足迹遍野"); }
            else if (!_m100 && r >= 0.999f) { _m100 = true; ShowMilestone("探索度 100% · 六道尽览"); }
        }

        void ShowMilestone(string text)
        {
            _milestone = text;
            _milestoneUntil = Time.unscaledTime + 4f;
            Debug.Log("[ChessSage][里程碑] " + text);
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16, 16, 340, 280), GUI.skin.box);
            GUILayout.Label("六道大陆 · 大地图");
            if (Map != null && Player != null)
            {
                int cx = Player.CellX, cy = Player.CellY;
                var region = Map.GetRegion(cx, cy);
                GUILayout.Label($"坐标：({cx}, {cy})　地形：{Map.GetTerrain(cx, cy)}");
                GUILayout.Label("区域：" + (region?.Name ?? "—"));
                if (Chunks != null)
                    GUILayout.Label($"视野块：{Chunks.LoadedChunkCount}　地形精灵：{Chunks.ActiveRendererCount}");
                if (Decor != null)
                    GUILayout.Label($"装饰在屏：{Decor.ActiveDecorCount} / 全图 {DecorTotal}");
                if (Fog != null && Fog.State != null)
                    GUILayout.Label($"探索度：{Fog.State.ExploredRatio() * 100f:F1}%");
                if (Atmosphere != null)
                    GUILayout.Label($"时刻：{Atmosphere.TimeOfDay * 24f:F1} 时　天气：{Atmosphere.WeatherName}" +
                                    $"　2D光照：{(Atmosphere.Light2DActive ? "开" : "降级")}");
            }
            GUILayout.Label(LastHint);
            GUILayout.Label("WASD/方向键 移动　E 交互　滚轮 缩放");
            GUILayout.EndArea();

            if (!string.IsNullOrEmpty(_milestone) && Time.unscaledTime < _milestoneUntil)
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 30,
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                };
                style.normal.textColor = new Color(1f, 0.86f, 0.4f);
                GUI.Label(new Rect(0, Screen.height * 0.28f, Screen.width, 50f), _milestone, style);
            }
        }
    }
}