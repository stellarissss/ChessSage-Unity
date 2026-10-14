using System.Collections;
using System.Collections.Generic;
using System.Threading;
using ChessSage.Core.Cheat;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using ChessSage.Presentation.Board;

namespace ChessSage.Presentation.Cheat
{
    /// <summary>
    /// 作弊改规指令面板 + 业力/识破 HUD 的展现层驱动（UGUI，程序化构建，与 VariantHub 同风格）。
    ///
    /// 【职责边界】本类只做「画 UI + 收输入 + 调 <see cref="CheatController"/>」；
    /// 所有判断（diff 人读化、代价分档、失败文案、预置补丁、扣费顺序）都在 Core 的 CheatController，
    /// 因此既可回归测试，又天然保证「引擎只预估、表现层在确认后才扣费」（UX §11.1）。
    ///
    /// 【常驻入口】棋盘右下角「⚡改规」按钮 + I 键召唤（USP 必须被看见，不藏菜单里）。
    ///
    /// 【双撤销栈】「撤回AI修改」只回退规则、不返还业力、不动棋谱；棋谱由 GameFlowHud 的悔棋负责。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CheatCommandPanel : MonoBehaviour
    {
        enum State { Idle, Input, Processing, Preview, Danger, Offline }

        [Header("节奏")]
        [Tooltip("产品级硬超时（秒）。LlmClient 默认 120s，UI 侧必须更早主动取消，避免玩家干等。")]
        public float HardTimeoutSeconds = 20f;
        [Tooltip("软提示阈值（秒）：超过后在处理卡片追加「仍在推演…」。")]
        public float SoftHintSeconds = 8f;

        public KeyCode ToggleKey = KeyCode.I;

        // ── 依赖（由组合根 Bootstrap 注入）──────────────────────────────
        CheatController _controller;
        BoardInputRouter _router;
        Game.GameScreen _screen;
        CheatHud _hud;

        // ── UI 引用 ────────────────────────────────────────────────────
        Canvas _canvas;
        GameObject _entryButton, _drawer;
        Text _titleText, _offlineBanner;
        GameObject _inputGroup, _previewGroup, _failureGroup;
        InputField _input;
        Text _auxHint;
        GameObject _examplesGrid, _historyRow, _historyGrid, _presetGrid;
        GameObject _procOverlay; Text _procStage, _procTimer, _procSub;
        GameObject _dangerModal; Text _dangerBody;
        GameObject _detectedOverlay; Text _detectedBody;
        GameObject _previewCards; Text _previewBanner, _costBar;
        Button _confirmButton; Text _confirmLabel;
        GameObject _failureCard; Text _failureTitle, _failureReason; GameObject _failureActions;
        Button _undoRuleButton; Text _undoRuleLabel;
        Button _submitButton;

        State _state = State.Idle;
        CheatPreview _preview;
        string _lastCommand = "";
        bool _pickCoord;
        bool _failureVisible;
        Coroutine _submitRoutine;

        /// <summary>处理中 / 预览 / 高危确认时，棋盘输入应被屏蔽（UX §4.1）。</summary>
        public bool BlocksBoardInput => _state == State.Processing || _state == State.Preview || _state == State.Danger;

        public bool IsOpen => _drawer != null && _drawer.activeSelf;

        static readonly string[] ExampleCommands =
        {
            "让我的马可以横着走",
            "把我的兵变成炮",
            "让我的车可以飞过楚河汉界",
            "让我落子时一次下两颗子",
            "让我的棋子被提掉后还能留在盘上",
            "让我的老鼠可以吃掉大象",
            "让我的棋子在六角星上可以连跳三跳",
            "让我下的棋子这一回合不能被翻转",
            "让AI这一回合不能动",
            "悔一步棋",
        };

        // ══════════════════════════════════════════════════════════════
        // 注入与生命周期
        // ══════════════════════════════════════════════════════════════

        public void Bind(CheatController controller, BoardInputRouter router, Game.GameScreen screen, CheatHud hud)
        {
            _controller = controller;
            _router = router;
            _screen = screen;
            _hud = hud;
            Build();
            SetVisible(true);
            RebuildHistory();
            RebuildPresets();
            ApplyState();
        }

        /// <summary>无对局（hub）时整体隐藏。</summary>
        public void SetVisible(bool visible)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(visible);
        }

        public void Open()
        {
            if (_drawer == null) return;
            _drawer.SetActive(true);
            RebuildHistory();
            ApplyState();
        }

        public void Close()
        {
            if (_drawer == null) return;
            if (_state == State.Processing) return;   // 处理中不可收起
            _drawer.SetActive(false);
            if (_state != State.Input) SetState(State.Input);
        }

        public void Toggle()
        {
            if (IsOpen) Close(); else Open();
        }

        /// <summary>试玩取证 / 无网演示：强制进入离线预置模式。</summary>
        public void SetOffline(bool offline)
        {
            _controller?.SetOffline(offline);
            RebuildPresets();
            ApplyState();
        }

        public void SetInputText(string text)
        {
            if (_input != null) _input.text = text ?? "";
        }

        // ══════════════════════════════════════════════════════════════
        // 构建 UI
        // ══════════════════════════════════════════════════════════════

        void Build()
        {
            if (_canvas != null) return;
            CheatUiKit.EnsureEventSystem(transform);

            var go = new GameObject("CheatCommandCanvas");
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 90;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();

            BuildEntryButton();
            BuildDrawer();
            BuildProcessingOverlay();
            BuildDangerModal();
            BuildDetectedOverlay();
        }

        void BuildEntryButton()
        {
            var btn = CheatUiKit.Button(_canvas.transform, "⚡ 改 规（I）", 28,
                new Color(0.16f, 0.19f, 0.26f, 0.96f), Toggle);
            _entryButton = btn.gameObject;
            var rt = CheatUiKit.Rect(_entryButton);
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-28f, 28f);
            rt.sizeDelta = new Vector2(300f, 76f);
        }

        void BuildDrawer()
        {
            var drawer = CheatUiKit.Panel("Drawer", _canvas.transform, CheatUiKit.PanelBg);
            CheatUiKit.AnchorBottomStretch(drawer.gameObject, 560f, 0f, 0f);
            _drawer = drawer.gameObject;

            var title = CheatUiKit.Text(drawer.transform, "⚡ 改写天规", 34, TextAnchor.MiddleLeft, CheatUiKit.Gold);
            PlaceTopLeft(title, 28f, 14f, 600f, 44f);
            _titleText = title;

            var closeBtn = CheatUiKit.Button(drawer.transform, "收起 ✕", 22, new Color(0.20f, 0.22f, 0.28f, 1f), Close);
            var closeRt = CheatUiKit.Rect(closeBtn.gameObject);
            PlaceTopLeft(closeRt, 1700f, 14f, 140f, 44f);

            _offlineBanner = CheatUiKit.Text(drawer.transform,
                "🔌 离线模式：无法连接天道，你只能使用预置的改规。", 24, TextAnchor.MiddleLeft, CheatUiKit.Amber);
            PlaceTopLeft(_offlineBanner, 28f, 62f, 1500f, 32f);

            BuildInputGroup(drawer.transform);
            BuildPreviewGroup(drawer.transform);
            BuildFailureGroup(drawer.transform);
        }

        void BuildInputGroup(Transform parent)
        {
            var root = CheatUiKit.NewUi("InputGroup", parent);
            PlaceTopLeft(CheatUiKit.Rect(root), 28f, 100f, 1864f, 440f);
            _inputGroup = root;

            // 输入框
            var iHolder = CheatUiKit.Panel("InputHolder", root.transform, CheatUiKit.InputBg);
            var ihr = CheatUiKit.Rect(iHolder.gameObject);
            ihr.anchorMin = new Vector2(0f, 1f); ihr.anchorMax = new Vector2(0f, 1f);
            ihr.pivot = new Vector2(0f, 1f); ihr.anchoredPosition = new Vector2(0f, 0f);
            ihr.sizeDelta = new Vector2(1560f, 56f);
            var input = iHolder.gameObject.AddComponent<InputField>();
            input.targetGraphic = iHolder;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 200;
            var txt = CheatUiKit.Text(iHolder.transform, "", 24, TextAnchor.MiddleLeft);
            txt.supportRichText = false;
            CheatUiKit.Stretch(txt.rectTransform, 14f, 8f, 14f, 8f);
            var ph = CheatUiKit.Text(iHolder.transform,
                "输入一句话，改写这条棋规……例如：让我的马可以横着走", 24, TextAnchor.MiddleLeft, CheatUiKit.Muted);
            ph.supportRichText = false;
            CheatUiKit.Stretch(ph.rectTransform, 14f, 8f, 14f, 8f);
            input.textComponent = txt;
            input.placeholder = ph;
            input.onSubmit.AddListener(_ => SubmitCurrent());
            _input = input;

            // 施展按钮
            var submit = CheatUiKit.Button(root.transform, "施 展", 26, new Color(0.20f, 0.42f, 0.62f, 1f), SubmitCurrent);
            var sr = CheatUiKit.Rect(submit.gameObject);
            sr.anchorMin = new Vector2(0f, 1f); sr.anchorMax = new Vector2(0f, 1f);
            sr.pivot = new Vector2(0f, 1f); sr.anchoredPosition = new Vector2(1572f, 0f);
            sr.sizeDelta = new Vector2(292f, 56f);
            _submitButton = submit;

            // 提示行
            var hint = CheatUiKit.Text(root.transform, "试试（点击直接填入）：", 22, TextAnchor.MiddleLeft, CheatUiKit.Muted);
            PlaceTopLeft(hint, 0f, 66f, 600f, 30f);

            // 示例 chips（5 列 × 2 行）
            var grid = CheatUiKit.NewUi("Examples", root.transform);
            PlaceTopLeft(CheatUiKit.Rect(grid), 0f, 100f, 1864f, 90f);
            var glg = grid.AddComponent<GridLayoutGroup>();
            glg.cellSize = new Vector2(364f, 40f);
            glg.spacing = new Vector2(6f, 6f);
            glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = 5;
            _examplesGrid = grid;
            foreach (var ex in ExampleCommands)
            {
                var text = ex;   // 闭包捕获
                CheatUiKit.Button(grid.transform, text, 17, CheatUiKit.CardBg, () =>
                {
                    SetInputText(text);
                    SetFailureVisible(false);
                    SetState(State.Input);
                });
            }

            // 历史行
            var histLabel = CheatUiKit.Text(root.transform, "常用 · 历史", 22, TextAnchor.MiddleLeft, CheatUiKit.Muted);
            PlaceTopLeft(histLabel, 0f, 198f, 180f, 30f);
            _historyGrid = CheatUiKit.NewUi("History", root.transform);
            PlaceTopLeft(CheatUiKit.Rect(_historyGrid), 190f, 194f, 1674f, 44f);
            var hlg = _historyGrid.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f; hlg.childForceExpandWidth = false; hlg.childControlWidth = false;
            hlg.childForceExpandHeight = true; hlg.childControlHeight = true;

            // 离线预置区
            var presetLabel = CheatUiKit.Text(root.transform, "预置改规：", 22, TextAnchor.MiddleLeft, CheatUiKit.Muted);
            PlaceTopLeft(presetLabel, 0f, 246f, 140f, 30f);
            _presetGrid = CheatUiKit.NewUi("Presets", root.transform);
            PlaceTopLeft(CheatUiKit.Rect(_presetGrid), 150f, 242f, 1714f, 44f);
            var plg = _presetGrid.AddComponent<HorizontalLayoutGroup>();
            plg.spacing = 8f; plg.childForceExpandWidth = false; plg.childControlWidth = false;
            plg.childForceExpandHeight = true; plg.childControlHeight = true;

            // 辅助行：选坐标 / 撤回AI修改
            var pick = CheatUiKit.Button(root.transform, "📍 选坐标", 22, new Color(0.18f, 0.24f, 0.30f, 1f), TogglePickCoord);
            PlaceTopLeft(CheatUiKit.Rect(pick.gameObject), 0f, 296f, 200f, 44f);

            var undoBtn = CheatUiKit.Button(root.transform, "撤回AI修改", 22, new Color(0.24f, 0.20f, 0.18f, 1f), UndoRule);
            PlaceTopLeft(CheatUiKit.Rect(undoBtn.gameObject), 216f, 296f, 260f, 44f);
            _undoRuleButton = undoBtn;
            _undoRuleLabel = undoBtn.GetComponentInChildren<Text>();

            var undoNote = CheatUiKit.Text(root.transform, "（撤回不返还业力；棋谱悔棋请用 U 键）",
                20, TextAnchor.MiddleLeft, CheatUiKit.Muted);
            PlaceTopLeft(undoNote, 486f, 296f, 1000f, 44f);

            var aux = CheatUiKit.Text(root.transform, "", 20, TextAnchor.MiddleLeft, CheatUiKit.Amber);
            PlaceTopLeft(aux, 0f, 348f, 1600f, 30f);
            _auxHint = aux;
        }

        void BuildPreviewGroup(Transform parent)
        {
            var root = CheatUiKit.NewUi("PreviewGroup", parent);
            PlaceTopLeft(CheatUiKit.Rect(root), 28f, 100f, 1864f, 440f);
            _previewGroup = root;

            _previewBanner = CheatUiKit.Text(root.transform, "预览 · 尚未生效（确认后才写入对局）", 24, TextAnchor.MiddleLeft, CheatUiKit.Amber);
            PlaceTopLeft(_previewBanner, 0f, 0f, 1200f, 30f);

            var card = CheatUiKit.Panel("PreviewCards", root.transform, new Color(0.10f, 0.09f, 0.06f, 1f));
            var cr = CheatUiKit.Rect(card.gameObject);
            cr.anchorMin = new Vector2(0f, 1f); cr.anchorMax = new Vector2(0f, 1f);
            cr.pivot = new Vector2(0f, 1f); cr.anchoredPosition = new Vector2(0f, -36f);
            cr.sizeDelta = new Vector2(1180f, 240f);
            var vlg = card.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f; vlg.childForceExpandHeight = false; vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true; vlg.childControlWidth = true;
            _previewCards = card.gameObject;

            _costBar = CheatUiKit.Text(root.transform, "", 22, TextAnchor.UpperLeft);
            PlaceTopLeft(_costBar, 1200f, 36f, 664f, 200f);

            var cancel = CheatUiKit.Button(root.transform, "✕ 取消", 24, new Color(0.30f, 0.20f, 0.20f, 1f), CancelPreview);
            PlaceTopLeft(CheatUiKit.Rect(cancel.gameObject), 1200f, 240f, 300f, 52f);

            var confirm = CheatUiKit.Button(root.transform, "✓ 确认施展", 24, new Color(0.20f, 0.44f, 0.28f, 1f), RequestConfirm);
            PlaceTopLeft(CheatUiKit.Rect(confirm.gameObject), 1516f, 240f, 348f, 52f);
            _confirmButton = confirm;
            _confirmLabel = confirm.GetComponentInChildren<Text>();
        }

        void BuildFailureGroup(Transform parent)
        {
            var root = CheatUiKit.NewUi("FailureGroup", parent);
            PlaceTopLeft(CheatUiKit.Rect(root), 28f, 100f, 1864f, 440f);
            _failureGroup = root;

            var card = CheatUiKit.Panel("FailureCard", root.transform, new Color(0.20f, 0.08f, 0.08f, 1f));
            CheatUiKit.Stretch(CheatUiKit.Rect(card.gameObject), 0f, 0f, 0f, 90f);
            _failureCard = card.gameObject;

            _failureTitle = CheatUiKit.Text(card.transform, "", 28, TextAnchor.MiddleLeft, CheatUiKit.Amber);
            PlaceTopLeft(_failureTitle, 24f, 16f, 1780f, 40f);

            _failureReason = CheatUiKit.Text(card.transform, "", 23, TextAnchor.UpperLeft);
            PlaceTopLeft(_failureReason, 24f, 60f, 1780f, 260f);

            _failureActions = CheatUiKit.NewUi("FailureActions", root.transform);
            PlaceTopLeft(CheatUiKit.Rect(_failureActions), 0f, 360f, 1864f, 56f);
            var hlg = _failureActions.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 12f; hlg.childForceExpandWidth = false; hlg.childControlWidth = false;
            hlg.childForceExpandHeight = true; hlg.childControlHeight = true;
        }

        void BuildProcessingOverlay()
        {
            var overlay = CheatUiKit.Panel("ProcessingOverlay", _canvas.transform, new Color(0f, 0f, 0f, 0.55f));
            CheatUiKit.Stretch(CheatUiKit.Rect(overlay.gameObject));
            _procOverlay = overlay.gameObject;

            var card = CheatUiKit.Panel("ProcCard", overlay.transform, new Color(0.10f, 0.12f, 0.17f, 0.98f));
            var cr = CheatUiKit.Rect(card.gameObject);
            cr.anchorMin = new Vector2(0.5f, 0.5f); cr.anchorMax = new Vector2(0.5f, 0.5f);
            cr.pivot = new Vector2(0.5f, 0.5f); cr.sizeDelta = new Vector2(760f, 260f);

            _procStage = CheatUiKit.Text(card.transform, "◐ 天道正在推演你的妄念…", 30, TextAnchor.MiddleCenter);
            PlaceTopLeft(_procStage, 20f, 30f, 720f, 60f);
            var sub = CheatUiKit.Text(card.transform, "阶段：意图解析", 24, TextAnchor.MiddleCenter, CheatUiKit.Muted);
            PlaceTopLeft(sub, 20f, 100f, 720f, 40f);
            _procSub = sub;
            _procTimer = CheatUiKit.Text(card.transform, "已用时 0.0s（超时 20s）", 22, TextAnchor.MiddleCenter, CheatUiKit.Muted);
            PlaceTopLeft(_procTimer, 20f, 150f, 720f, 40f);
        }

        void BuildDangerModal()
        {
            var overlay = CheatUiKit.Panel("DangerModal", _canvas.transform, new Color(0f, 0f, 0f, 0.6f));
            CheatUiKit.Stretch(CheatUiKit.Rect(overlay.gameObject));
            _dangerModal = overlay.gameObject;

            var card = CheatUiKit.Panel("DangerCard", overlay.transform, new Color(0.14f, 0.08f, 0.08f, 0.99f));
            var cr = CheatUiKit.Rect(card.gameObject);
            cr.anchorMin = new Vector2(0.5f, 0.5f); cr.anchorMax = new Vector2(0.5f, 0.5f);
            cr.pivot = new Vector2(0.5f, 0.5f); cr.sizeDelta = new Vector2(900f, 360f);

            CheatUiKit.Text(card.transform, "⚠️ 此举将超出业力安全线", 30, TextAnchor.MiddleCenter, CheatUiKit.Red)
                .rectTransform.anchoredPosition = Vector2.zero;

            _dangerBody = CheatUiKit.Text(card.transform, "", 24, TextAnchor.UpperLeft);
            PlaceTopLeft(_dangerBody, 40f, 70f, 820f, 200f);

            var think = CheatUiKit.Button(card.transform, "再想想", 26, new Color(0.24f, 0.26f, 0.32f, 1f), () => HideDanger());
            PlaceTopLeft(CheatUiKit.Rect(think.gameObject), 60f, 286f, 360f, 56f);

            var go = CheatUiKit.Button(card.transform, "我意已决，施展", 26, CheatUiKit.Red, ApplyConfirm);
            PlaceTopLeft(CheatUiKit.Rect(go.gameObject), 480f, 286f, 360f, 56f);

            _dangerModal.SetActive(false);
        }

        void BuildDetectedOverlay()
        {
            var overlay = CheatUiKit.Panel("DetectedOverlay", _canvas.transform, new Color(0f, 0f, 0f, 0.88f));
            CheatUiKit.Stretch(CheatUiKit.Rect(overlay.gameObject));
            _detectedOverlay = overlay.gameObject;

            var card = CheatUiKit.Panel("DetectedCard", overlay.transform, new Color(0.10f, 0.05f, 0.05f, 1f));
            var cr = CheatUiKit.Rect(card.gameObject);
            cr.anchorMin = new Vector2(0.5f, 0.5f); cr.anchorMax = new Vector2(0.5f, 0.5f);
            cr.pivot = new Vector2(0.5f, 0.5f); cr.sizeDelta = new Vector2(880f, 460f);

            CheatUiKit.Text(card.transform, "👁 天 道 识 破", 44, TextAnchor.MiddleCenter, CheatUiKit.Red)
                .rectTransform.anchoredPosition = new Vector2(0f, 160f);

            _detectedBody = CheatUiKit.Text(card.transform, "", 26, TextAnchor.MiddleCenter);
            PlaceTopLeft(_detectedBody, 40f, 160f, 800f, 220f);

            var rb = CheatUiKit.Button(card.transform, "重新开始", 28,
                new Color(0.30f, 0.22f, 0.22f, 1f), () => _detectedOverlay.SetActive(false));
            PlaceTopLeft(CheatUiKit.Rect(rb.gameObject), 290f, 380f, 300f, 56f);

            _detectedOverlay.SetActive(false);
        }

        // ══════════════════════════════════════════════════════════════
        // 输入与提交
        // ══════════════════════════════════════════════════════════════

        void SubmitCurrent()
        {
            if (_state == State.Processing || _controller == null) return;
            var cmd = (_input != null ? _input.text : "").Trim();
            if (cmd.Length == 0)
            {
                ShowFailure(new CheatFailure { Title = "天道静候", Reason = "先说一句你想改什么。" }, cmd, false);
                return;
            }
            if (cmd.Length > 200) cmd = cmd.Substring(0, 200);
            _lastCommand = cmd;
            _submitRoutine = StartCoroutine(SubmitRoutine(cmd));
        }

        IEnumerator SubmitRoutine(string command)
        {
            _procStage.text = "◐ 天道正在推演你的妄念…";
            _procSub.text = "阶段：意图解析";
            SetState(State.Processing);

            var cts = new CancellationTokenSource();
            var task = _controller.SubmitAsync(command, cts.Token);
            float t = 0f; var soft = false; var timedOut = false;
            while (!task.IsCompleted)
            {
                t += Time.unscaledDeltaTime;
                _procTimer.text = $"已用时 {t:0.0}s（超时 {HardTimeoutSeconds:0}s）";
                if (!soft && t >= SoftHintSeconds)
                {
                    soft = true;
                    _procSub.text = "仍在推演…天道有时也会迟疑";
                }
                if (t >= HardTimeoutSeconds && !cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    timedOut = true;
                    break;
                }
                yield return null;
            }

            CheatPreview preview;
            if (timedOut)
            {
                preview = ErrorPreview($"请求超时或被取消（已等待 {HardTimeoutSeconds:0} 秒）");
            }
            else
            {
                try { preview = task.Result; }
                catch (System.Exception e) { preview = ErrorPreview("意图解析失败: " + e.Message); }
            }
            cts.Dispose();
            _submitRoutine = null;
            OnPreviewReady(preview, command);
        }

        void OnPreviewReady(CheatPreview preview, string command)
        {
            if (preview == null)
            {
                ShowFailure(new CheatFailure { Title = "⚠️ 天道没有回应", Reason = "结果为空。" }, command, false);
                return;
            }

            if (preview.Ok)
            {
                _preview = preview;
                SetFailureVisible(false);
                RenderPreview(preview);
                SetState(State.Preview);
                return;
            }

            if (preview.Failure != null && preview.Failure.Offline)
            {
                EnterOffline();
                ShowFailure(preview.Failure, command, false);
                return;
            }

            ShowFailure(preview.Failure ?? new CheatFailure { Title = "⚠️ 天道没有回应", Reason = "结果为空。" },
                command, preview.Failure != null && preview.Failure.IsFun);
        }

        // ══════════════════════════════════════════════════════════════
        // 预览 / 确认 / 撤回
        // ══════════════════════════════════════════════════════════════

        void RenderPreview(CheatPreview preview)
        {
            ClearChildren(_previewCards.transform);
            foreach (var c in preview.Cards) AddDiffCard(c);

            var cost = preview.CostInfo;
            _costBar.text = BuildCostText(cost);

            if (cost != null && cost.Tier == CheatTier.Danger)
            {
                _confirmLabel.text = $"⚠️ 强行施展（将溢出 {cost.Overshoot} 点）";
                _confirmButton.GetComponent<Image>().color = CheatUiKit.Red;
            }
            else
            {
                _confirmLabel.text = "✓ 确认施展";
                _confirmButton.GetComponent<Image>().color = new Color(0.20f, 0.44f, 0.28f, 1f);
            }

            _previewBanner.text = preview.Partial
                ? preview.PartialNote + "（预览 · 尚未生效）"
                : "预览 · 尚未生效（确认后才写入对局）";
        }

        void AddDiffCard(DiffCard card)
        {
            float h = 34f + card.Lines.Count * 34f + (string.IsNullOrEmpty(card.Impact) ? 0f : 30f);
            var go = CheatUiKit.Panel("Card", _previewCards.transform,
                card.Failed ? new Color(0.22f, 0.08f, 0.08f, 1f) : new Color(0.16f, 0.14f, 0.08f, 1f));
            var le = go.gameObject.AddComponent<LayoutElement>();
            le.minHeight = h; le.preferredHeight = h;

            var t = CheatUiKit.Text(go.transform, (card.Failed ? "❌ " : "✦ ") + card.Title, 24,
                TextAnchor.MiddleLeft, card.Failed ? CheatUiKit.Red : CheatUiKit.Amber);
            PlaceTopLeft(t, 14f, 6f, 1120f, 30f);

            float y = 36f;
            foreach (var line in card.Lines)
            {
                var lineText = $"{line.Label}：{line.Before}  →  {line.After}";
                var lt = CheatUiKit.Text(go.transform, lineText, 22, TextAnchor.MiddleLeft);
                PlaceTopLeft(lt, 24f, y, 1120f, 30f);
                y += 34f;
            }
            if (!string.IsNullOrEmpty(card.Impact))
            {
                var it = CheatUiKit.Text(go.transform, "↗ 影响：" + card.Impact, 21, TextAnchor.MiddleLeft, CheatUiKit.Muted);
                PlaceTopLeft(it, 24f, y, 1120f, 28f);
            }
        }

        static string BuildCostText(CostPreview cost)
        {
            if (cost == null) return "代价预估：—";
            var sb = new System.Text.StringBuilder();
            if (cost.FreeCheat)
                sb.AppendLine($"☯ 本手免费（『白嫖』剩余 1 次）");
            else
                sb.AppendLine($"☯ 业力代价 {cost.Cost}（{cost.Karma} → {cost.Karma + cost.Cost}，安全线 {cost.KarmaMax}）");
            if (cost.Overshoot > 0)
                sb.AppendLine($"　 将溢出 {cost.Overshoot} 点");
            sb.AppendLine(cost.Overshoot <= 0
                ? "👁 识破风险 +0%（未溢出，不会触发结算）"
                : $"👁 识破风险 +{cost.DetectionDelta:0.0}%（施展后立即结算一次）");
            // switch 表达式先算进局部变量再插值：Unity 的 C# 9 不允许非逐字插值字符串内含换行（CS8967）。
            var tierLabel = cost.Tier switch
            {
                CheatTier.Safe => "划算·安全施展",
                CheatTier.Critical => "临界·紧张施展",
                _ => "危险·反噬风险",
            };
            sb.AppendLine($"⚖️ 局面影响：见左侧改动（{tierLabel}）");
            if (!string.IsNullOrEmpty(cost.SkillNote)) sb.AppendLine("🎁 " + cost.SkillNote);
            return sb.ToString();
        }

        void RequestConfirm()
        {
            if (_preview == null) return;
            var cost = _preview.CostInfo;
            if (cost != null && cost.Tier == CheatTier.Danger)
            {
                _dangerBody.text = BuildDangerText(cost);
                _dangerModal.SetActive(true);
                SetState(State.Danger);
                return;
            }
            ApplyConfirm();
        }

        static string BuildDangerText(CostPreview cost)
        {
            return $"施展这一手将消耗业力 {cost.Cost}，使你从 {cost.Karma} 溢出到 {cost.Karma + cost.Cost}" +
                   $"（超出安全阈值 {cost.KarmaMax} 共 {cost.Overshoot} 点）。\n\n" +
                   $"天道的怀疑将增长约 +{cost.DetectionDelta:0.0}%。一旦被识破，本道余下棋局将无法再作弊。";
        }

        void HideDanger()
        {
            if (_dangerModal != null) _dangerModal.SetActive(false);
            SetState(_preview != null ? State.Preview : State.Input);
        }

        void ApplyConfirm()
        {
            if (_preview == null) return;
            if (_dangerModal != null) _dangerModal.SetActive(false);

            var res = _controller.Confirm(_preview);
            var detectionPulse = res.Overshoot > 0;
            _preview = null;

            if (_hud != null)
            {
                _hud.Refresh();
                if (res.Detected)
                    _hud.Flash("⚠️ 被识破：天道已锁定你的妄念", true);
                else if (res.Overshoot > 0)
                    _hud.Flash($"⚠️ 业力溢出 {res.Overshoot} 点，天道已生疑", true);
                else
                    _hud.Flash($"☯ 改规生效（业力 {res.KarmaBefore} → {res.KarmaAfter}）");
            }

            if (_screen != null) _screen.RefreshBoardOnly();

            if (res.Detected) ShowDetected(res.Judgment);

            UpdateUndoRuleButton();
            SetFailureVisible(false);
            SetState(State.Input);
        }

        void CancelPreview()
        {
            _preview = null;
            SetFailureVisible(false);
            SetState(State.Input);
        }

        void TogglePickCoord()
        {
            _pickCoord = !_pickCoord;
            if (_screen != null) _screen.BoardInputSuppressed = _pickCoord;
            if (_auxHint != null)
                _auxHint.text = _pickCoord ? "请在棋盘上点一个格，坐标将插入输入框…" : "";
            EnsureRouterSubscription();
        }

        bool _routerSubscribed;

        void EnsureRouterSubscription()
        {
            if (_routerSubscribed || _router == null) return;
            _router.CellClicked += OnBoardCellClicked;
            _routerSubscribed = true;
        }

        void OnBoardCellClicked(int x, int y)
        {
            if (!_pickCoord) return;
            _pickCoord = false;
            if (_screen != null) _screen.BoardInputSuppressed = false;
            if (_auxHint != null) _auxHint.text = "";
            SetInputText((_input != null ? _input.text : "") + $"（坐标 {x},{y}）");
        }

        void UndoRule()
        {
            if (_controller == null || !_controller.HasRuleUndo)
            {
                if (_hud != null) _hud.Flash("没有可撤回的改规");
                return;
            }
            if (_controller.NeedsMismatchWarning)
            {
                if (_hud != null) _hud.Flash("⚠️ 你在这条改规之后已经走过棋，撤回规则不回退棋局，当前局面可能不一致");
            }
            if (_controller.UndoRule(out _))
            {
                if (_screen != null) _screen.RefreshBoardOnly();
                if (_hud != null) _hud.Refresh();
                UpdateUndoRuleButton();
                SetState(State.Input);
            }
        }

        void UpdateUndoRuleButton()
        {
            if (_undoRuleButton == null) return;
            var can = _controller != null && _controller.HasRuleUndo;
            _undoRuleButton.interactable = can;
            if (_undoRuleLabel != null)
                _undoRuleLabel.text = can ? $"撤回AI修改（{_controller.RuleUndoDepth}）" : "撤回AI修改";
        }

        // ══════════════════════════════════════════════════════════════
        // 离线 / 预置
        // ══════════════════════════════════════════════════════════════

        void EnterOffline()
        {
            _controller?.SetOffline(true);
            RebuildPresets();
            SetState(State.Offline);
        }

        void RebuildPresets()
        {
            if (_presetGrid == null || _controller == null) return;
            ClearChildren(_presetGrid.transform);
            var variant = _controller.Session?.Config?.Variant ?? "xiangqi";
            var presets = CheatPresetLibrary.ForVariant(variant);
            for (int i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                CheatUiKit.Button(_presetGrid.transform, preset.Text, 20,
                    new Color(0.14f, 0.18f, 0.24f, 1f), () => SelectPreset(preset));
            }
        }

        /// <summary>选中一条预置指令 → 生成预览（离线演示主路径，也是试玩取证的驱动入口）。</summary>
        public void SelectPreset(PresetCommand preset)
        {
            if (_controller == null || preset == null) return;
            if (_state == State.Processing) return;
            SetInputText(preset.Text);
            var preview = _controller.SubmitPreset(preset);
            OnPreviewReady(preview, preset.Text);
        }

        /// <summary>按索引选中预置（试玩取证用）。</summary>
        public void SimulateSelectPreset(int index)
        {
            var variant = _controller?.Session?.Config?.Variant ?? "xiangqi";
            var presets = CheatPresetLibrary.ForVariant(variant);
            if (index >= 0 && index < presets.Count) SelectPreset(presets[index]);
        }

        public void SimulateConfirm() => RequestConfirm();

        public void SimulateOpenAndSubmit(string command)
        {
            Open();
            SetInputText(command);
            SubmitCurrent();
        }

        // ══════════════════════════════════════════════════════════════
        // 失败反馈
        // ══════════════════════════════════════════════════════════════

        void ShowFailure(CheatFailure failure, string command, bool fun)
        {
            ClearChildren(_failureActions.transform);
            _failureTitle.text = failure.Title;
            _failureTitle.color = fun ? CheatUiKit.Green : CheatUiKit.Amber;
            _failureCard.GetComponent<Image>().color = fun
                ? new Color(0.10f, 0.18f, 0.12f, 1f)
                : new Color(0.20f, 0.08f, 0.08f, 1f);
            _failureReason.text = failure.Reason;

            if (failure.Actions != null)
            {
                foreach (var action in failure.Actions)
                {
                    var a = action;
                    CheatUiKit.Button(_failureActions.transform, a.Label, 22,
                        new Color(0.22f, 0.26f, 0.34f, 1f), () => HandleAction(a, command));
                }
            }

            SetFailureVisible(true);
            SetState(State.Input);
        }

        void SetFailureVisible(bool visible)
        {
            _failureVisible = visible;
            if (_failureGroup != null) _failureGroup.SetActive(visible);
        }

        void HandleAction(CheatAction action, string command)
        {
            switch (action.Kind)
            {
                case CheatActionKind.ApplySuggestion:
                case CheatActionKind.SplitSteps:
                    SetInputText(!string.IsNullOrEmpty(action.FillText) ? action.FillText : command);
                    break;
                case CheatActionKind.ManualEdit:
                case CheatActionKind.ChangeWording:
                    SetInputText(command);
                    break;
                case CheatActionKind.Retry:
                    SetInputText(command);
                    SubmitCurrent();
                    break;
                case CheatActionKind.UsePreset:
                case CheatActionKind.OpenPresets:
                    EnterOffline();
                    break;
                case CheatActionKind.Reconnect:
                    _controller?.Reconnect();
                    RebuildPresets();
                    break;
                case CheatActionKind.GoSkillTree:
                    if (_hud != null) _hud.Flash("技能树尚未在此版本开放（敬请期待）");
                    break;
            }
        }

        void ShowDetected(string judgment)
        {
            var text = string.IsNullOrEmpty(judgment)
                ? "你不是渴求胜利的战士，是作弊成性的怪物。"
                : judgment;
            _detectedBody.text = text + "\n\n存档已重置，但技能与成就得以保留。\n" +
                                 "你的结局之路，已转向「天道审判」。";
            _detectedOverlay.SetActive(true);
        }

        // ══════════════════════════════════════════════════════════════
        // 状态与刷新
        // ══════════════════════════════════════════════════════════════

        void SetState(State state)
        {
            _state = state;
            ApplyState();
        }

        void ApplyState()
        {
            if (_drawer == null) return;
            bool offline = _controller != null && _controller.Offline;

            _inputGroup.SetActive(_state != State.Preview && _state != State.Processing);
            _previewGroup.SetActive(_state == State.Preview);
            _failureGroup.SetActive(_state == State.Input && _failureVisible);
            _procOverlay.SetActive(_state == State.Processing);

            if (_offlineBanner != null)
                _offlineBanner.gameObject.SetActive(offline);

            if (_input != null)
                _input.interactable = !offline && _state != State.Processing;

            if (_submitButton != null)
            {
                bool blockedByAi = _controller != null && _controller.AiTurnBlocked;
                _submitButton.interactable = !offline && !blockedByAi && _state != State.Processing;
                var label = _submitButton.GetComponentInChildren<Text>();
                if (label != null && blockedByAi) label.text = "轮到对方";
                else if (label != null) label.text = "施 展";
            }

            UpdateUndoRuleButton();
        }

        void RebuildHistory()
        {
            if (_historyGrid == null || _controller == null) return;
            ClearChildren(_historyGrid.transform);
            foreach (var item in _controller.History.Items)
            {
                var text = item;
                var btn = CheatUiKit.Button(_historyGrid.transform, text, 19,
                    new Color(0.14f, 0.16f, 0.20f, 1f), () => SetInputText(text));
                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = Mathf.Clamp(text.Length * 20f + 30f, 140f, 360f);
                le.preferredHeight = 40f;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 工具
        // ══════════════════════════════════════════════════════════════

        void Update()
        {
            if (Input.GetKeyDown(ToggleKey) && Input.GetKey(KeyCode.LeftShift) == false)
                Toggle();
            if (Input.GetKeyDown(KeyCode.Escape) && IsOpen && _state == State.Input)
                Close();
        }

        void OnDisable()
        {
            if (_submitRoutine != null) { StopCoroutine(_submitRoutine); _submitRoutine = null; }
            if (_routerSubscribed && _router != null) { _router.CellClicked -= OnBoardCellClicked; _routerSubscribed = false; }
        }

        void OnDestroy() => OnDisable();

        static void PlaceTopLeft(Text t, float x, float y, float w, float h)
            => PlaceTopLeft(t.rectTransform, x, y, w, h);

        static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Destroy(t.GetChild(i).gameObject);
        }

        CheatPreview ErrorPreview(string message)
        {
            var result = new JObject { ["type"] = "error", ["message"] = message };
            return new CheatPreview { Type = "error", Failure = CheatErrorTranslator.Present(result) };
        }
    }
}
