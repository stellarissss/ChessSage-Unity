using System.IO;
using ChessSage.Core.Cheat;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Orchestration;
using ChessSage.Core.Samsara;
using ChessSage.Presentation.Board;
using ChessSage.Presentation.Cheat;
using ChessSage.Presentation.Game;
using ChessSage.Presentation.Hub;
using ChessSage.Presentation.Samsara;
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
    ///   4. 负责棋种切换：首次启动走 <see cref="StartVariant"/>，运行期由
    ///      <see cref="VariantHub"/> 触发 <see cref="LaunchVariant"/>，
    ///      并可经 <see cref="EnterHub"/> 返回选择界面。
    ///
    /// 这样 <c>ChessSage.Core</c> 保持零引擎依赖（可在 dotnet 下回归测试），
    /// <c>ChessSage.Presentation</c> 不直接 new 任何 Core 会话（可替换视图），
    /// 依赖关系严格单向：App → Presentation → Core。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Bootstrap : MonoBehaviour
    {
        [Header("启动参数")]
        [Tooltip("启动棋种 id，见 ChessSage.Core.Config.VariantCatalog。" +
                 "PlaythroughCapture 依赖此字段指定试玩棋种，请勿移除。")]
        public string StartVariant = "xiangqi";

        [Tooltip("为 true 时启动即进入棋种选择 hub（不直接开局）。默认 false 以保持垂直切片与试玩录制的既有行为。")]
        public bool StartAtHub = false;

        [Tooltip("格边长（世界单位）。")]
        public float CellSize = 1f;

        [Tooltip("启动 / 切换棋种时按相机视口自适应格宽：仅在棋盘超出视野时缩小，绝不放大，" +
                 "以保证既有棋种（象棋/动物棋）视觉不变，又不让大棋盘（围棋/五子棋/跳棋）被裁掉。")]
        public bool AutoFitBoardToCamera = true;

        [Header("场景引用（留空则运行时自动搭建）")]
        public GameScreen Screen;

        GameSession _session;
        BoardView _board;
        HighlightLayer _highlights;
        BoardInputRouter _router;
        VariantHub _hub;
        string _activeVariant;

        // ── 作弊改规链路（T3b：接入组合根，让 USP 真正可触达）────────────
        // 长生命周期：SamsaraEngine 跨棋种保留业力/识破；面板与 HUD 只建一次。
        // 每局的 CheatController / OrchestratorEngine 在 LaunchVariant 里按新会话重建。
        SamsaraEngine _samsara;
        OrchestratorEngine _orchestrator;
        CheatController _cheat;
        CheatCommandPanel _cheatPanel;
        CheatHud _cheatHud;
        SamsaraRpg _rpg;

        /// <summary>当前对局棋种 id；在 hub 界面（无对局）时为 null。</summary>
        public string ActiveVariant => _activeVariant;

        /// <summary>当前局作弊协调器（hub 无对局时为 null）。试玩脚本经此读取业力/识破。</summary>
        public CheatController Cheat => _cheat;

        /// <summary>作弊改规面板（常驻「⚡改规」入口 + 抽屉），供试玩脚本用公开 API 驱动。</summary>
        public CheatCommandPanel CheatPanel => _cheatPanel;

        /// <summary>业力 / 识破常驻 HUD。</summary>
        public CheatHud CheatHudView => _cheatHud;

        /// <summary>六道 RPG 表现层控制器（选关 / 技能树 / 剧情 / 结局 / 记忆）。</summary>
        public SamsaraRpg Rpg => _rpg;

        /// <summary>六道轮回引擎（Core）。仅供组合根与试玩脚本读取，表现层由 <see cref="Rpg"/> 内持有。</summary>
        public SamsaraEngine Samsara => _samsara;

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

            _hub = GetComponent<VariantHub>();
            if (_hub == null) _hub = gameObject.AddComponent<VariantHub>();
            _hub.Bootstrap = this;
            _hub.Build();

            // 作弊链路的长生命周期部件先就位；每局的协调器在 LaunchVariant 内重建。
            EnsureCheatComponents();

            if (StartAtHub) EnterHub();
            else LaunchVariant(StartVariant);
        }

        /// <summary>
        /// 构造作弊链路的<b>长生命周期</b>部件：<see cref="SamsaraEngine"/>（跨棋种保留业力/识破）、
        /// 面板与 HUD。每局的 <see cref="CheatController"/> 与 <see cref="OrchestratorEngine"/> 在
        /// <see cref="LaunchVariant"/> 内按当前会话重建（见 <see cref="WireCheat"/>），
        /// 从而「切换棋种 → 重建 GameSession」时面板永远绑到新对局、不会绑死旧 session。
        /// </summary>
        void EnsureCheatComponents()
        {
            if (_samsara == null)
            {
                // savePath=null：本切片不落盘（仅内存态），避免污染玩家存档。
                _samsara = new SamsaraEngine(GlobalConfigDir(), null);
            }

            _cheatHud = GetComponent<CheatHud>();
            if (_cheatHud == null) _cheatHud = gameObject.AddComponent<CheatHud>();

            _cheatPanel = GetComponent<CheatCommandPanel>();
            if (_cheatPanel == null) _cheatPanel = gameObject.AddComponent<CheatCommandPanel>();

            // 六道 RPG 表现层：与作弊链路同为长生命周期，只建一次；界面数据源是 _samsara（注入，不 new）。
            _rpg = GetComponent<SamsaraRpg>();
            if (_rpg == null) _rpg = gameObject.AddComponent<SamsaraRpg>();
            _rpg.Bind(this, _samsara);
        }

        /// <summary>global 配置目录（<see cref="SamsaraEngine"/> 的真源目录）。</summary>
        string GlobalConfigDir() => Path.Combine(ConfigsRoot(), "global");

        /// <summary>
        /// 把作弊面板 / HUD / 协调器接到<b>当前</b> <see cref="_session"/>。
        /// 每次 <see cref="LaunchVariant"/> 都必须调用——切棋种会 new 一个 GameSession，
        /// 绑死旧 session 会让改规落到已废弃的对局上（历史缺陷模式）。
        /// </summary>
        void WireCheat(string variantId)
        {
            if (_samsara == null || _cheatPanel == null || _cheatHud == null) return;

            // 无密钥 / 断网时 LlmClient.IsAvailable=false，CheatController 自动进入离线降级
            // （UX §10.1），预置指令链路（CheatPresetLibrary）照常可走，USP 仍可演示。
            _orchestrator = new OrchestratorEngine(new LlmClient(), ConfigsRoot()) { GameType = variantId };
            _cheat = new CheatController(_session, _samsara, _orchestrator);

            _cheatHud.Bind(_cheat);
            _cheatHud.SetVisible(true);

            _cheatPanel.Bind(_cheat, _router, Screen, _cheatHud);
            _cheatPanel.SetVisible(true);
            // 默认收起抽屉：主屏只留「⚡改规（I）」常驻入口，棋盘不被遮挡（UX §2.1）。
            _cheatPanel.Close();
        }

        /// <summary>
        /// 启动（或切换到）指定棋种。切换时会重建 <see cref="GameSession"/>，
        /// 并由 <see cref="GameScreen.Attach"/> 负责重绑 <see cref="BoardView"/> 的棋盘布局、
        /// 清空选择态与高亮。配置缺失时给出明确错误而不是静默黑屏。
        /// </summary>
        public void LaunchVariant(string variantId)
        {
            try
            {
                var loader = new ConfigLoader(ConfigsRoot());
                var config = loader.LoadVariant(variantId);
                _session = new GameSession(config, new MechanismEngine(config.Rules));
                _activeVariant = variantId;
                if (Screen != null) Screen.Attach(_session);
                FitBoardToCamera();
                if (_hub != null) _hub.Hide();
                WireCheat(variantId);   // 面板/HUD 重新绑定到新对局（切棋种必走）
                Debug.Log($"[ChessSage] 已启动棋种：{variantId}（{config.SideScheme}）");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ChessSage] 启动棋种 {variantId} 失败：{e.Message}");
                Debug.LogError("[ChessSage] 请确认 Assets/StreamingAssets/Configs 已完整同步（tools/sync-configs.sh）。");
            }
        }

        /// <summary>进入棋种选择 hub：分离当前对局、清空高亮、显示选择界面。</summary>
        public void EnterHub()
        {
            _activeVariant = null;
            if (Screen != null) Screen.Detach();
            if (_highlights != null) _highlights.Clear();
            if (_hub != null) _hub.Show();

            // 作弊链路随对局一起收殓：hub 无对局，隐藏面板与 HUD，避免点击穿透与陈旧状态。
            _cheat = null;
            _orchestrator = null;
            if (_cheatPanel != null) _cheatPanel.SetVisible(false);
            if (_cheatHud != null) _cheatHud.SetVisible(false);

            Debug.Log("[ChessSage] 已进入棋种选择 hub");
        }

        /// <summary>
        /// 让棋盘适应相机视口：<b>只在棋盘超出视野时缩小格宽，绝不放大</b>。
        ///
        /// 【为什么需要】Main 场景的相机是固定正交尺寸（orthographicSize=5.4，视高约 10.8 单位），
        /// 象棋（9×10）与动物棋（7×9）刚好放得下；而五子棋（15×15）、围棋（19×19）、
        /// 跳棋六角星（纵跨 17）都会溢出屏幕——玩家看不到边角，点击也够不着，实际"不可玩"。
        /// 这里按布局包络算出恰好容纳的格宽并取与原格宽的较小值，于是：
        ///   · 原格宽已能放下 → 维持不变（象棋/动物棋视觉零回归）；
        ///   · 放不下 → 等比缩小到带 2% 边距放下（大棋盘可玩）。
        /// 之所以不动相机而缩放格宽：视图与输入都经 <see cref="BoardView.CellToLocal"/> /
        /// <c>LocalToCell</c>，缩放格宽两端自动一致；改相机则需同时考虑大地图相机的复用。
        /// </summary>
        void FitBoardToCamera()
        {
            if (!AutoFitBoardToCamera || _board == null || _board.Layout == null) return;

            var cam = Camera.main;
            if (cam == null || !cam.orthographic) return;

            float viewH = cam.orthographicSize * 2f;
            float viewW = viewH * cam.aspect;
            if (viewH <= 0f || viewW <= 0f) return;

            _board.Layout.GetBounds(out double minX, out double minY, out double maxX, out double maxY);
            float cols = (float)(maxX - minX) + 1f;   // 含末格外沿
            float rows = (float)(maxY - minY) + 1f;
            if (cols <= 0f || rows <= 0f) return;

            const float margin = 0.98f;
            float cellFit = Mathf.Min(viewH * margin / rows, viewW * margin / cols);
            _board.SetCellSize(Mathf.Min(CellSize, cellFit));
            Debug.Log($"[ChessSage] 棋盘自适应：格宽 {_board.CellSize:F3}（视高 {viewH:F1}，格位 {cols:F0}×{rows:F0}）");
        }

        /// <summary>配置根目录（实例便捷访问，供 hub 等使用）。</summary>
        public string ConfigsRootPath => ConfigsRoot();

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
