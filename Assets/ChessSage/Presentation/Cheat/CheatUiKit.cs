using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChessSage.Presentation.Cheat
{
    /// <summary>
    /// 作弊面板与 HUD 共用的 UGUI 程序化搭建小工具。
    ///
    /// 【为什么程序化】本项目无美术资源、无预制体（.prefab/.uxml 依赖编辑器序列化，脚本无法可靠手写），
    /// 与既有 <c>VariantHub</c> 一致，全部用代码搭 UGUI，做到自包含、可 CI、无外部依赖。
    /// 中文字体经 <c>Resources/NotoSansSC-Regular</c> 加载，保证不是方框。
    /// </summary>
    internal static class CheatUiKit
    {
        public static readonly Color PanelBg = new Color(0.06f, 0.07f, 0.10f, 0.98f);
        public static readonly Color CardBg = new Color(0.12f, 0.14f, 0.19f, 1f);
        public static readonly Color InputBg = new Color(0.10f, 0.12f, 0.16f, 1f);
        public static readonly Color Amber = new Color(0.95f, 0.70f, 0.20f, 1f);
        public static readonly Color Green = new Color(0.35f, 0.80f, 0.45f, 1f);
        public static readonly Color Red = new Color(0.86f, 0.30f, 0.28f, 1f);
        public static readonly Color Muted = new Color(0.62f, 0.67f, 0.75f, 1f);
        public static readonly Color Gold = new Color(1f, 0.84f, 0f, 1f);

        static Font _font;

        public static Font UiFont
        {
            get
            {
                if (_font != null) return _font;
                _font = Resources.Load<Font>("NotoSansSC-Regular");
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return _font;
            }
        }

        public static void EnsureEventSystem(Transform parent)
        {
            if (Object.FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.transform.SetParent(parent, false);
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }

        public static GameObject NewUi(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        public static Image Panel(string name, Transform parent, Color color, bool raycast = true)
        {
            var go = NewUi(name, parent);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        public static Text Text(Transform parent, string text, int size, TextAnchor anchor, Color? color = null)
        {
            var go = NewUi("Text", parent);
            var t = go.AddComponent<Text>();
            t.text = text;
            t.font = UiFont;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = color ?? Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }

        public static Button Button(Transform parent, string label, int fontSize, Color bg, System.Action onClick)
        {
            var go = NewUi("Button", parent);
            var img = go.AddComponent<Image>();
            img.color = bg;
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(
                Mathf.Min(1f, bg.r + 0.12f), Mathf.Min(1f, bg.g + 0.12f), Mathf.Min(1f, bg.b + 0.12f), bg.a);
            colors.pressedColor = new Color(bg.r * 0.8f, bg.g * 0.8f, bg.b * 0.8f, bg.a);
            btn.colors = colors;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var t = Text(go.transform, label, fontSize, TextAnchor.MiddleCenter);
            Stretch(t.rectTransform, 6f, 2f, 6f, 2f);
            return btn;
        }

        public static void Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        public static RectTransform Rect(GameObject go) => (RectTransform)go.transform;

        /// <summary>设置锚点为「贴父级底部、横向拉伸」的抽屉容器。</summary>
        public static void AnchorBottomStretch(GameObject go, float height, float xMargin = 0f, float bottom = 0f)
        {
            var rt = Rect(go);
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(xMargin, bottom);
            rt.offsetMax = new Vector2(-xMargin, bottom + height);
        }

        /// <summary>设置锚点为「贴父级顶部、横向拉伸」的顶栏容器。</summary>
        public static void AnchorTopStretch(GameObject go, float height, float xMargin = 0f, float top = 0f)
        {
            var rt = Rect(go);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(xMargin, -top - height);
            rt.offsetMax = new Vector2(-xMargin, -top);
        }
    }
}
