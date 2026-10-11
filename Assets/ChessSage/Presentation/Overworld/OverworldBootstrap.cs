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
        [Range(1, 4)] public int ViewRadiusChunks = 2;
        [Range(1, 8)] public int FogRevealRadius = 4;
        public bool EnableDayNight = true;
        public float DayLengthSeconds = 300f;

        public OverworldMap Map { get; private set; }
        public OverworldChunkRenderer Chunks { get; private set; }
        public OverworldPlayer Player { get; private set; }
        public OverworldCamera CameraRig { get; private set; }
        public OverworldFog Fog { get; private set; }
        public PoiMarkerLayer PoiLayer { get; private set; }
        public OverworldAtmosphere Atmosphere { get; private set; }

        /// <summary>玩家请求进入某 POI（六道入口等）。上层据此切到对局场景。</summary>
        public event System.Action<Poi> RealmEntered;

        /// <summary>提示信息（HUD 显示）。</summary>
        public string LastHint { get; private set; } = "";

        void Awake()
        {
            if (!TryLoadMap()) return;

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
            Chunks.ViewRadiusChunks = ViewRadiusChunks;
            Chunks.Initialize(Map);

            // 迷雾
            Fog = FindObjectOfType<OverworldFog>();
            if (Fog == null) Fog = gameObject.AddComponent<OverworldFog>();
            Fog.RevealRadius = FogRevealRadius;
            Fog.Initialize(Map);

            // POI
            PoiLayer = FindObjectOfType<PoiMarkerLayer>();
            if (PoiLayer == null) PoiLayer = gameObject.AddComponent<PoiMarkerLayer>();
            PoiLayer.Map = Map;
            PoiLayer.Initialize(Map);

            // 玩家
            Player = FindObjectOfType<OverworldPlayer>();
            if (Player == null) Player = gameObject.AddComponent<OverworldPlayer>();
            Player.Map = Map;
            Player.Cam = cam;
            Player.Chunks = Chunks;
            Player.Fog = Fog;
            Player.Initialize(Map);
            Player.ArrivedAt += OnPlayerArrived;

            // 相机
            CameraRig = FindObjectOfType<OverworldCamera>();
            if (CameraRig == null) CameraRig = gameObject.AddComponent<OverworldCamera>();
            CameraRig.Target = Player;
            CameraRig.Map = Map;
            CameraRig.Cam2D = cam;

            // 昼夜
            if (EnableDayNight)
            {
                Atmosphere = FindObjectOfType<OverworldAtmosphere>();
                if (Atmosphere == null) Atmosphere = gameObject.AddComponent<OverworldAtmosphere>();
                Atmosphere.DayLengthSeconds = DayLengthSeconds;
            }

            LastHint = $"六道大陆已加载：{Map.Width}×{Map.Height}，{Map.Regions.Count} 区域，{Map.Pois.Count} 处地标";
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
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16, 16, 300, 260), GUI.skin.box);
            GUILayout.Label("六道大陆 · 大地图");
            if (Map != null && Player != null)
            {
                int cx = Player.CellX, cy = Player.CellY;
                var region = Map.GetRegion(cx, cy);
                GUILayout.Label($"坐标：({cx}, {cy})　地形：{Map.GetTerrain(cx, cy)}");
                GUILayout.Label("区域：" + (region?.Name ?? "—"));
                if (Chunks != null)
                    GUILayout.Label($"视野块：{Chunks.LoadedChunkCount}　Renderer：{Chunks.ActiveRendererCount}");
                if (Fog != null && Fog.State != null)
                    GUILayout.Label($"探索度：{Fog.State.ExploredRatio() * 100f:F1}%");
                if (Atmosphere != null)
                    GUILayout.Label($"时刻：{Atmosphere.TimeOfDay * 24f:F1} 时");
            }
            GUILayout.Label(LastHint);
            GUILayout.Label("WASD/方向键 移动　E 交互　滚轮 缩放");
            GUILayout.EndArea();
        }
    }
}