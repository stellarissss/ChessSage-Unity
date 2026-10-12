using System.Collections.Generic;
using System.IO;
using ChessSage.Core.Config;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChessSage.Presentation.Hub
{
    /// <summary>
    /// 棋种选择 hub：程序化生成的选择界面，把「六种棋类」列成可点击入口。
    ///
    /// 【为什么程序化生成而不做预制体】预制体 / UXML 依赖编辑器序列化，脚本无法可靠手写；
    /// 而 <c>ProjectBootstrapper</c> 需要在无人干预下生成可直接运行的场景。用
    /// <c>Canvas + UnityEngine.UI</c> 在运行时搭出界面，能让 hub 自包含、可测试、可 CI 验证，
    /// 也不依赖任何外部美术资源。待正式 UI 美术 / 本地化就位后可整体替换。
    ///
    /// 【数据驱动】棋种清单来自 <see cref="ConfigLoader.ListAvailableVariants"/>（其真源是
    /// <see cref="VariantCatalog.All"/>），说明文案优先取各棋种 board.json 的
    /// <c>_metadata.description</c>，取不到再回退代码内映射。新增棋类只要补配置即出现在这里，
    /// 不需要在 hub 里硬编码字符串。
    ///
    /// 【与对局的关系】hub 只负责「显示入口」与「转发选择」；真正的会话构造、布局重建、
    /// 状态清理都在 <see cref="Bootstrap"/> 与 <see cref="GameScreen"/>。返回 hub 通过右上角的
    /// 「返回棋种选择」按钮触发 <see cref="Bootstrap.EnterHub"/>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VariantHub : MonoBehaviour
    {
        [Tooltip("组合根引用，由 Bootstrap 在 Awake 注入。")]
        public Bootstrap Bootstrap;

        Canvas _canvas;
        GameObject _hubPanel;
        GameObject _gameBar;

        /// <summary>board.json 缺 description 时的代码内兜底说明（简体中文）。</summary>
        static readonly Dictionary<string, string> FallbackDescriptions = new Dictionary<string, string>
        {
            { "xiangqi", "中国象棋 · 六道轮回，吃掉对方将帅取胜" },
            { "dongwuqi", "斗兽棋，以小搏大、抢占兽穴" },
            { "tiaoqi", "中国跳棋，把全部棋子送进对角营区" },
            { "wuziqi", "五子棋，先连成五子者胜" },
            { "weiqi", "围棋，吃十子或全歼对手取胜" },
            { "heibaiqi", "黑白棋，夹吃翻面、终局子多者胜" },
        };

        /// <summary>构建界面（幂等）。由 <see cref="Bootstrap"/> 在 Awake 末尾调用一次。</summary>
        public void Build()
        {
            if (_canvas != null) return;
            EnsureEventSystem();
            BuildCanvas();
            BuildHubPanel();
            BuildGameBar();
            Show();
        }

        /// <summary>显示棋种选择面板（返回 hub 时）。</summary>
        public void Show()
        {
            if (_hubPanel != null) _hubPanel.SetActive(true);
            if (_gameBar != null) _gameBar.SetActive(false);
        }

        /// <summary>隐藏选择面板、显示「返回棋种选择」按钮（进入对局时）。</summary>
        public void Hide()
        {
            if (_hubPanel != null) _hubPanel.SetActive(false);
            if (_gameBar != null) _gameBar.SetActive(true);
        }

        // ── 搭建 ──────────────────────────────────────────────────────────────

        void BuildCanvas()
        {
            var go = new GameObject("VariantHubCanvas");
            go.transform.SetParent(transform, false);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;   // 盖在对局 HUD / 棋盘之上

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();
        }

        void BuildHubPanel()
        {
            _hubPanel = NewUi("HubPanel", _canvas.transform);
            Stretch(_hubPanel.GetComponent<RectTransform>());

            var bg = _hubPanel.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.06f, 0.09f, 0.98f);
            bg.raycastTarget = true;   // 挡住穿透到棋盘的原始鼠标事件

            var title = NewText(_hubPanel.transform, "六道棋局 · 选择棋种", 48, TextAnchor.MiddleCenter);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -60f);
            titleRt.sizeDelta = new Vector2(0f, 90f);

            var listGo = NewUi("VariantList", _hubPanel.transform);
            var list = listGo.AddComponent<VerticalLayoutGroup>();
            list.spacing = 14f;
            list.childAlignment = TextAnchor.MiddleCenter;
            list.childControlWidth = true;
            list.childControlHeight = true;
            list.childForceExpandWidth = true;
            list.childForceExpandHeight = false;

            var listRt = listGo.GetComponent<RectTransform>();
            listRt.anchorMin = new Vector2(0.5f, 0.5f);
            listRt.anchorMax = new Vector2(0.5f, 0.5f);
            listRt.pivot = new Vector2(0.5f, 0.5f);
            listRt.anchoredPosition = new Vector2(0f, -30f);
            listRt.sizeDelta = new Vector2(820f, 640f);

            foreach (var id in AvailableVariants())
                CreateVariantButton(listGo.transform, id);
        }

        void BuildGameBar()
        {
            _gameBar = NewUi("GameBar", _canvas.transform);
            var rt = _gameBar.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-24f, -24f);
            rt.sizeDelta = new Vector2(240f, 60f);

            var img = _gameBar.AddComponent<Image>();
            img.color = new Color(0.16f, 0.19f, 0.26f, 0.95f);

            var btn = _gameBar.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.28f, 0.36f, 0.50f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener(() => { if (Bootstrap != null) Bootstrap.EnterHub(); });

            var label = NewText(_gameBar.transform, "返回棋种选择", 24, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
        }

        void CreateVariantButton(Transform parent, string id)
        {
            var info = VariantCatalog.Get(id);
            string displayName = info?.DisplayName ?? id;
            string desc = Describe(id);

            var go = NewUi("Variant_" + id, parent);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.18f, 0.25f, 1f);

            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 86f;
            le.preferredHeight = 86f;

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.26f, 0.34f, 0.48f, 1f);
            colors.pressedColor = new Color(0.34f, 0.44f, 0.60f, 1f);
            btn.colors = colors;

            string captured = id;
            btn.onClick.AddListener(() => OnSelected(captured));

            var label = NewText(go.transform,
                $"{displayName}\n<size=22><color=#b9c2d0>{desc}</color></size>",
                32, TextAnchor.MiddleLeft);
            label.supportRichText = true;
            Stretch(label.rectTransform, 28f, 8f, 28f, 8f);
        }

        void OnSelected(string id)
        {
            Debug.Log($"[Hub] 选择棋种：{id}");
            if (Bootstrap != null) Bootstrap.LaunchVariant(id);
        }

        // ── 数据 ──────────────────────────────────────────────────────────────

        /// <summary>可用棋种清单：优先配置枚举（存在 board.json 的棋种），失败回退到 Catalog。</summary>
        IReadOnlyList<string> AvailableVariants()
        {
            try
            {
                var loader = new ConfigLoader(ConfigsRoot());
                var list = loader.ListAvailableVariants();
                if (list.Count > 0) return list;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Hub] 枚举棋种失败，回退代码内目录：{e.Message}");
            }

            var fallback = new List<string>();
            foreach (var info in VariantCatalog.All) fallback.Add(info.Id);
            return fallback;
        }

        /// <summary>取棋种一句玩法说明：优先 board.json 的 _metadata.description，缺失回退代码内映射。</summary>
        string Describe(string id)
        {
            try
            {
                var loader = new ConfigLoader(ConfigsRoot());
                var board = loader.LoadVariant(id).Board;
                var desc = board?["_metadata"]?["description"]?.Value<string>();
                if (!string.IsNullOrEmpty(desc)) return desc;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Hub] 读取 {id} 说明失败，回退代码内映射：{e.Message}");
            }
            return FallbackDescriptions.TryGetValue(id, out var f) ? f : "";
        }

        string ConfigsRoot()
            => Bootstrap != null
                ? Bootstrap.ConfigsRootPath
                : Path.Combine(Application.streamingAssetsPath, "Configs");

        // ── UGUI 小工具 ───────────────────────────────────────────────────────

        void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.transform.SetParent(transform, false);
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        static GameObject NewUi(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static void Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        Text NewText(Transform parent, string text, int size, TextAnchor anchor)
        {
            var go = NewUi("Text", parent);
            var t = go.AddComponent<Text>();
            t.text = text;
            t.font = UiFont;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }

        static Font _uiFont;

        /// <summary>
        /// 解析界面字体：优先工程内置的中文字体（Resources/NotoSansSC-Regular），
        /// 缺失时回退 Unity 内置字体。之所以优先工程字体：内置 LegacyRuntime 无中文字形，
        /// 会把全部中文标题渲染成方框。
        /// </summary>
        static Font UiFont
        {
            get
            {
                if (_uiFont != null) return _uiFont;
                _uiFont = Resources.Load<Font>("NotoSansSC-Regular");
                if (_uiFont == null) _uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_uiFont == null) _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return _uiFont;
            }
        }
    }
}
