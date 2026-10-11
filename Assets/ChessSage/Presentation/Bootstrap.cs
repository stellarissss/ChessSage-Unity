using System.IO;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using ChessSage.Presentation.Board;
using ChessSage.Presentation.Game;
using UnityEngine;

namespace ChessSage.Presentation
{
    /// <summary>
    /// 组合根（Composition Root）：程序唯一允许"知道所有层"的地方。
    ///
    /// 它负责：
    ///   1. 定位 StreamingAssets/Configs 并加载棋类配置；
    ///   2. 构造 <see cref="GameSession"/>（纯逻辑，无引擎依赖）；
    ///   3. 在场景中搭出棋盘视图 + 输入层 + HUD，并接线；
    ///   4. 负责棋种切换。
    ///
    /// 这样 <c>ChessSage.Core</c> 保持零引擎依赖（可在 dotnet 下回归测试），
    /// <c>ChessSage.Presentation</c> 不直接 new 任何 Core 会话（可替换视图），
    /// 依赖关系严格单向：App → Presentation → Core。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Bootstrap : MonoBehaviour
    {
        [Header("启动参数")]
        [Tooltip("启动棋种 id，见 ChessSage.Core.Config.VariantCatalog。")]
        public string StartVariant = "xiangqi";

        [Tooltip("格边长（世界单位）。")]
        public float CellSize = 1f;

        [Header("场景引用（留空则运行时自动搭建）")]
        public GameScreen Screen;

        GameSession _session;
        BoardView _board;
        HighlightLayer _highlights;
        BoardInputRouter _router;

        void Awake()
        {
            _board = FindObjectOfType<BoardView>();
            if (_board == null) _board = new GameObject("BoardView").AddComponent<BoardView>();
            _board.CellSize = CellSize;

            _highlights = FindObjectOfType<HighlightLayer>();
            if (_highlights == null) _highlights = new GameObject("Highlights").AddComponent<HighlightLayer>();

            _router = FindObjectOfType<BoardInputRouter>();
            if (_router == null) _router = _board.gameObject.AddComponent<BoardInputRouter>();
            _router.View = _board;

            Screen = Screen != null ? Screen : FindObjectOfType<GameScreen>();
            if (Screen == null) Screen = gameObject.AddComponent<GameScreen>();
            Screen.Board = _board;
            Screen.Highlights = _highlights;
            Screen.Input = _router;
            Screen.Hud = FindObjectOfType<GameFlowHud>();
            if (Screen.Hud == null) Screen.Hud = gameObject.AddComponent<GameFlowHud>();

            LaunchVariant(StartVariant);
        }

        /// <summary>启动（或切换）到指定棋种。配置缺失时给出明确错误而不是静默黑屏。</summary>
        public void LaunchVariant(string variantId)
        {
            try
            {
                var loader = new ConfigLoader(ConfigsRoot());
                var config = loader.LoadVariant(variantId);
                _session = new GameSession(config, new MechanismEngine(config.Rules));
                Screen.Attach(_session);
                Debug.Log($"[ChessSage] 已启动棋种：{variantId}（{config.SideScheme}）");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ChessSage] 启动棋种 {variantId} 失败：{e.Message}");
                Debug.LogError("[ChessSage] 请确认 Assets/StreamingAssets/Configs 已完整同步（tools/sync-configs.sh）。");
            }
        }

        /// <summary>定位配置根目录。优先 StreamingAssets（运行时），回退 Assets（编辑器模式）。</summary>
        public static string ConfigsRoot()
        {
            var streaming = Path.Combine(Application.streamingAssetsPath, "Configs");
            if (Directory.Exists(streaming)) return streaming;
            var inAssets = Path.Combine(Application.dataPath, "StreamingAssets", "Configs");
            if (Directory.Exists(inAssets)) return inAssets;
            Debug.LogWarning("[ChessSage] 未找到配置目录，请运行 tools/sync-configs.sh");
            return streaming;
        }
    }
}