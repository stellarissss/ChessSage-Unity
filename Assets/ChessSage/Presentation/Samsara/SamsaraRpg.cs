using ChessSage.Core.Samsara;
using ChessSage.Presentation.Cheat;
using ChessSage.Presentation.Game;
using UnityEngine;
using UnityEngine.UI;

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// RPG 表现层控制器（六道轮回的「关卡 / 技能 / 剧情 / 结局 / 记忆」五块界面）。
    ///
    /// 【组合根唯一】本类<b>不 new 任何 Core 会话对象</b>：<see cref="SamsaraEngine"/> 由组合根
    /// <see cref="Bootstrap"/> 注入（<see cref="Bind"/>），棋种启动也一律回经
    /// <c>Bootstrap.LaunchVariant</c>。因此依赖严格单向：Presentation → Core，
    /// 且「谁知道所有层」仍只有 Bootstrap 一处。
    ///
    /// 【界面路由】用轻量 <see cref="UiScreenStack"/> 管理多屏，避免各界面互相覆盖留下僵尸面板。
    /// 【对局 HUD】业力 / 识破 HUD 直接复用既有 <c>CheatHud</c>，本层绝不重造第二个 HUD。
    /// 【进入 / 退出】左下角常驻「☸ 六道轮回」入口（L 键等效）；每屏都有「返回 / 关闭」，
    /// Esc 退栈，栈空即关闭——与棋种 hub、作弊面板互不抢屏（本层 sortingOrder 最高，仅在全屏模态时覆盖）。
    ///
    /// 【输入】棋盘输入走原始鼠标事件、不经 EventSystem，故模态打开时必须置
    /// <c>GameScreen.BoardInputSuppressed</c>，否则点界面会穿透到棋盘误落子。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SamsaraRpg : MonoBehaviour
    {
        [Tooltip("组合根引用，由 Bootstrap 注入。")]
        public Bootstrap Bootstrap;

        /// <summary>注入的六道引擎（Core，零引擎依赖）。界面只读它、不构造它。</summary>
        public SamsaraEngine Engine { get; private set; }

        Canvas _canvas;
        GameObject _screenRoot;
        GameObject _entryButton;
        readonly UiScreenStack _stack = new UiScreenStack();

        LevelSelectPanel _levels;
        SkillTreePanel _skills;
        StoryPanel _story;
        EndingPanel _ending;
        MemoryAlbumPanel _memory;

        bool _built;

        /// <summary>是否有 RPG 界面正在显示（模态）。</summary>
        public bool IsOpen => !_stack.IsEmpty;

        /// <summary>剧情面板：推进对白（供界面按钮与试玩取证共用的公开入口）。</summary>
        public void StoryNext() => _story?.Next();

        /// <summary>剧情面板：一路推进对白直到出现抉择。</summary>
        public void StoryAdvanceToChoice() => _story?.AdvanceToChoice();

        /// <summary>剧情面板：选择第 optionIndex 个选项（结算在 Core.ChoiceSystem）。</summary>
        public void StoryChoose(int optionIndex) => _story?.Choose(optionIndex);

        // ══════════════════════════════════════════════════════════════
        // 注入与构建
        // ══════════════════════════════════════════════════════════════

        public void Bind(Bootstrap bootstrap, SamsaraEngine engine)
        {
            Bootstrap = bootstrap;
            Engine = engine;
            if (_built) return;
            Build();
            _built = true;
        }

        void Build()
        {
            CheatUiKit.EnsureEventSystem(transform);

            var go = new GameObject("SamsaraRpgCanvas");
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 120;   // 高于 hub(100)/作弊面板(90)/HUD(80)：全屏模态时置顶
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();

            _screenRoot = RpgUi.NewUi("Screens", _canvas.transform);
            RpgUi.Stretch(RpgUi.Rect(_screenRoot));

            _levels = new LevelSelectPanel(this);
            _levels.Build(_screenRoot.transform);
            _skills = new SkillTreePanel(this);
            _skills.Build(_screenRoot.transform);
            _story = new StoryPanel(this);
            _story.Build(_screenRoot.transform);
            _ending = new EndingPanel(this);
            _ending.Build(_screenRoot.transform);
            _memory = new MemoryAlbumPanel(this);
            _memory.Build(_screenRoot.transform);

            BuildEntryButton();
            SyncVisibility();
        }

        void BuildEntryButton()
        {
            var btn = CheatUiKit.Button(_canvas.transform, "☸ 六道轮回", 24,
                new Color(0.16f, 0.19f, 0.26f, 0.96f), Toggle);
            _entryButton = btn.gameObject;
            var rt = RpgUi.Rect(_entryButton);
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(28f, 28f);   // 左下角（右下角已被「⚡改规」占用）
            rt.sizeDelta = new Vector2(240f, 68f);
        }

        // ══════════════════════════════════════════════════════════════
        // 公开导航 API（界面按钮与试玩取证共用）
        // ══════════════════════════════════════════════════════════════

        public void Toggle()
        {
            if (IsOpen) Close();
            else OpenLevelSelect();
        }

        public void Open() => OpenLevelSelect();

        /// <summary>打开选关界面（RPG 主入口）。</summary>
        public void OpenLevelSelect()
        {
            if (!_built) return;
            _levels.Refresh();
            _stack.Push(_levels.Root);
            SyncVisibility();
        }

        public void ShowSkills()
        {
            if (!_built) return;
            _skills.Refresh();
            _stack.Push(_skills.Root);
            SyncVisibility();
        }

        /// <summary>打开某道某关的剧情（levelIndex 为 0 基）。</summary>
        public void ShowStory(string realm, int levelIndex)
        {
            if (!_built) return;
            _story.Load(realm, levelIndex);
            _stack.Push(_story.Root);
            SyncVisibility();
        }

        /// <summary>展示指定结局。</summary>
        public void ShowEnding(string endingId)
        {
            if (!_built) return;
            _ending.Show(endingId);
            _stack.Push(_ending.Root);
            SyncVisibility();
        }

        public void ShowMemory()
        {
            if (!_built) return;
            _memory.Refresh();
            _stack.Push(_memory.Root);
            SyncVisibility();
        }

        /// <summary>退栈（返回上一屏；栈空则关闭）。</summary>
        public void Back()
        {
            _stack.Pop();
            SyncVisibility();
        }

        /// <summary>关闭全部 RPG 界面。</summary>
        public void Close()
        {
            _stack.Clear();
            SyncVisibility();
        }

        /// <summary>
        /// 进入某道某关：先把选择写入 Samsara 状态（current_realm / current_level），
        /// 再回经组合根启动对应棋种的对局，最后收起 RPG 覆盖层。
        /// </summary>
        public void EnterLevel(string realm, int levelIndex)
        {
            if (Engine != null)
            {
                var progress = Engine.Levels.GetRealmProgress(realm);
                var gameType = RpgUi.GetStr(progress, "game_type", "xiangqi");
                Engine.State.SetRealm(realm);
                Engine.State.Set("current_level", levelIndex);
                Debug.Log($"[六道] 进入 {realm} 第 {levelIndex + 1} 关（棋种 {gameType}）");
                Close();
                if (Bootstrap != null) Bootstrap.LaunchVariant(gameType);
                return;
            }
            Close();
        }

        // ══════════════════════════════════════════════════════════════
        // 可见性 / 输入屏蔽
        // ══════════════════════════════════════════════════════════════

        void SyncVisibility()
        {
            if (_entryButton != null) _entryButton.SetActive(!IsOpen);

            if (Bootstrap == null || Bootstrap.Screen == null) return;

            // 模态打开时屏蔽棋盘走子（棋盘走原始鼠标事件，UGUI 挡不住），并隐藏 legacy OnGUI HUD，
            // 避免它盖在演出层之上。关闭时一并恢复。
            var suppress = IsOpen;
            Bootstrap.Screen.BoardInputSuppressed = suppress;
            if (Bootstrap.Screen.Hud != null) Bootstrap.Screen.Hud.SetVisible(!suppress);
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.L)) Toggle();
            else if (Input.GetKeyDown(KeyCode.Escape) && IsOpen) Back();
        }
    }
}
