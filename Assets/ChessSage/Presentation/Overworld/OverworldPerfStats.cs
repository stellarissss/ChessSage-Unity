using UnityEngine;
using Unity.Profiling;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 大地图性能面板：把"帧耗时 / 帧率 / draw call / 各层对象数"实测出来并打点。
    ///
    /// 任务把性能列为<b>硬指标</b>（沙箱是 CPU 软渲染），因此性能不能靠"感觉"，必须实测。
    /// 这里每 <see cref="LogIntervalSeconds"/> 秒往日志写一行 <c>[OW-PERF]</c>，
    /// 便于在无交互的 batchmode 试玩里直接抓取数字——而不是靠事后推断。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OverworldPerfStats : MonoBehaviour
    {
        [Header("引用")]
        public OverworldChunkRenderer Chunks;
        public OverworldDecorRenderer Decor;
        public OverworldRoadRenderer Roads;
        public OverworldLandmarkRenderer Landmarks;

        [Header("打点")]
        public float LogIntervalSeconds = 2f;

        [Tooltip("是否在屏幕上叠加性能面板（试玩截图会带上它）。")]
        public bool ShowOverlay = true;

        /// <summary>最近一帧耗时（毫秒，平滑后）。</summary>
        public float FrameMs { get; private set; }

        /// <summary>最近统计窗口内的平均帧率。</summary>
        public float Fps { get; private set; }

        /// <summary>最近一次采样的 draw call 数（采不到则为 -1）。</summary>
        public int DrawCalls { get; private set; } = -1;

        public int ChunkRenderers { get; private set; }
        public int DecorSprites { get; private set; }
        public int RoadSprites { get; private set; }
        public int LoadedChunks { get; private set; }

        ProfilerRecorder _drawCallsRec;
        float _acc;
        int _frames;
        float _windowSeconds;

        void OnEnable()
        {
            // 采样渲染线程的 draw call 计数；平台/版本不支持时返回 invalid（不会抛）。
            try
            {
                _drawCallsRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 8);
            }
            catch { _drawCallsRec = default; }
        }

        void OnDisable()
        {
            if (_drawCallsRec.Valid) _drawCallsRec.Dispose();
        }

        void Update()
        {
            _frames++;
            _windowSeconds += Time.unscaledDeltaTime;
            _acc += Time.unscaledDeltaTime;

            if (_drawCallsRec.Valid && _drawCallsRec.Count > 0)
                DrawCalls = (int)_drawCallsRec.LastValue;

            if (_acc >= LogIntervalSeconds)
            {
                Fps = _frames / Mathf.Max(0.001f, _windowSeconds);
                FrameMs = 1000f / Mathf.Max(0.001f, Fps);

                ChunkRenderers = Chunks != null ? Chunks.ActiveRendererCount : 0;
                LoadedChunks = Chunks != null ? Chunks.LoadedChunkCount : 0;
                DecorSprites = Decor != null ? Decor.ActiveDecorCount : 0;
                RoadSprites = Roads != null ? Roads.ActiveRoadCount : 0;

                Debug.Log($"[OW-PERF] fps={Fps:F1} frame={FrameMs:F1}ms drawCalls={DrawCalls} " +
                          $"chunks={LoadedChunks} tileSprites={ChunkRenderers} decor={DecorSprites} roads={RoadSprites} " +
                          $"landmarks={(Landmarks != null ? Landmarks.LandmarkCount : 0)}");

                _acc = 0f;
                _frames = 0;
                _windowSeconds = 0f;
            }
        }

        void OnGUI()
        {
            if (!ShowOverlay) return;
            GUILayout.BeginArea(new Rect(Screen.width - 300, 16, 288, 150), GUI.skin.box);
            GUILayout.Label("大地图性能（软渲染实测）");
            GUILayout.Label($"帧耗时：{FrameMs:F1} ms　帧率：{Fps:F1}");
            GUILayout.Label($"DrawCall：{(DrawCalls < 0 ? "n/a" : DrawCalls.ToString())}");
            GUILayout.Label($"已加载块：{LoadedChunks}　地形精灵：{ChunkRenderers}");
            GUILayout.Label($"装饰：{DecorSprites}　道路：{RoadSprites}");
            GUILayout.EndArea();
        }
    }
}
