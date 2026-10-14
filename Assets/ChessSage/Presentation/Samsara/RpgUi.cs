using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using ChessSage.Presentation.Cheat;   // 复用既有程序化 UGUI 工具（同程序集 internal 可见）

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// RPG 表现层共用的小工具与配色。
    ///
    /// 【为什么复用 CheatUiKit 而不另起一套】项目无美术资源、无预制体，全部 UI 由代码搭建；
    /// <c>CheatUiKit</c> 已是本程序集内共享的「程序化 UGUI + 中文字体加载」工具
    /// （<c>VariantHub / CheatHud / CheatCommandPanel</c> 均基于它）。与其复制一份，不如复用，
    /// 既保证字体（<c>Resources/NotoSansSC-Regular</c>，中文不出方框）与观感一致，也少一处维护点。
    ///
    /// 【配色即语义】各界面按「六道 / 结局类型」用一组固定基调色，避免逐界面硬编码十六进制，
    /// 也让「结局视觉基调」可单点调整。
    /// </summary>
    internal static class RpgUi
    {
        // ── 基调 ──
        // 遮罩全不透明：RPG 界面是模态覆盖层，须完全盖住其下的 hub / 对局，避免文字透影。
        public static readonly Color Scrim = new Color(0.040f, 0.048f, 0.072f, 1f);
        public static readonly Color Bar = new Color(0.075f, 0.088f, 0.125f, 1f);
        public static readonly Color Panel = new Color(0.095f, 0.112f, 0.155f, 1f);
        public static readonly Color Card = new Color(0.125f, 0.145f, 0.195f, 1f);
        public static readonly Color CardLocked = new Color(0.098f, 0.106f, 0.132f, 1f);

        public static readonly Color Ink = new Color(0.930f, 0.950f, 0.980f, 1f);
        public static readonly Color Muted = new Color(0.620f, 0.670f, 0.750f, 1f);
        public static readonly Color Gold = new Color(1.000f, 0.845f, 0.380f, 1f);
        public static readonly Color Green = new Color(0.420f, 0.820f, 0.520f, 1f);
        public static readonly Color Amber = new Color(0.950f, 0.700f, 0.280f, 1f);
        public static readonly Color Red = new Color(0.870f, 0.340f, 0.320f, 1f);
        public static readonly Color Violet = new Color(0.660f, 0.550f, 0.920f, 1f);
        public static readonly Color Cyan = new Color(0.440f, 0.740f, 0.860f, 1f);
        public static readonly Color Locked = new Color(0.420f, 0.450f, 0.500f, 1f);

        public static Font Font => CheatUiKit.UiFont;

        public static GameObject NewUi(string name, Transform parent) => CheatUiKit.NewUi(name, parent);
        public static RectTransform Rect(GameObject go) => CheatUiKit.Rect(go);

        public static void Stretch(RectTransform rt, float l = 0f, float t = 0f, float r = 0f, float b = 0f)
            => CheatUiKit.Stretch(rt, l, t, r, b);

        /// <summary>贴父级左上角的定尺放置（本项目界面的基本排版单位）。</summary>
        public static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        /// <summary>满屏遮罩根：承接点击、挡住下层（棋盘走子由 BoardInputSuppressed 另外屏蔽）。</summary>
        public static GameObject Backdrop(string name, Transform parent, Color? color = null)
        {
            var go = NewUi(name, parent);
            Stretch(Rect(go));
            var img = go.AddComponent<Image>();
            img.color = color ?? Scrim;
            img.raycastTarget = true;
            return go;
        }

        /// <summary>可点击卡片：自带底图 + Button + 悬停高亮。</summary>
        public static Button ClickableCard(Transform parent, Color bg, System.Action onClick)
        {
            var go = NewUi("Card", parent);
            var img = go.AddComponent<Image>();
            img.color = bg;
            img.raycastTarget = true;
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = Lighten(bg, 0.10f);
            colors.pressedColor = Lighten(bg, 0.18f);
            colors.disabledColor = bg;
            btn.colors = colors;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        public static Color Lighten(Color c, float amount)
            => new Color(Mathf.Min(1f, c.r + amount), Mathf.Min(1f, c.g + amount), Mathf.Min(1f, c.b + amount), c.a);

        public static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Object.Destroy(t.GetChild(i).gameObject);
        }

        /// <summary>难度星标（1..5）：难度来自关卡数据的 difficulty 字段。</summary>
        public static string Stars(int difficulty)
        {
            var d = Mathf.Clamp(difficulty, 0, 5);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < d; i++) sb.Append('★');
            for (int i = d; i < 5; i++) sb.Append('☆');
            return sb.ToString();
        }

        // ── JSON 读取小工具（Core.Samsara.SamsaraJson 为 internal，表现层不可用）─────
        public static int GetInt(JObject o, string k, int def = 0)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<int>();
            if (v.Type == JTokenType.Float) return (int)v.Value<double>();
            return def;
        }

        public static double GetDouble(JObject o, string k, double def = 0.0)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<long>();
            if (v.Type == JTokenType.Float) return v.Value<double>();
            return def;
        }

        public static bool GetBool(JObject o, string k, bool def = false)
        {
            var v = o?[k];
            return v != null && v.Type == JTokenType.Boolean ? v.Value<bool>() : def;
        }

        public static string GetStr(JObject o, string k, string def = "")
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }

        // ── 目标类型 → 人读文案（关卡 objective.type）─────────────────────
        public static string ObjectiveLabel(JObject objective)
        {
            if (objective == null) return "赢得此局";
            var desc = (string)objective["description"];
            if (!string.IsNullOrEmpty(desc)) return desc;
            var display = (string)objective["display_name"];
            if (!string.IsNullOrEmpty(display)) return display;
            var type = (string)objective["type"] ?? "checkmate";
            var target = objective["target"] == null ? 0 : (int)objective["target"];
            var maxTurns = objective["max_turns"] == null ? 0 : (int)objective["max_turns"];
            switch (type)
            {
                case "checkmate": return "将死 / 击败对手";
                case "capture": return "达成吃子目标";
                case "capture_count": return target > 0 ? $"吃掉对方 {target} 枚棋子" : "达成吃子目标";
                case "survive": return maxTurns > 0 ? $"存活 {maxTurns} 回合" : "存活到回合结束";
                case "survival": return maxTurns > 0 ? $"{maxTurns} 回合内做活" : "存活到回合结束";
                case "evacuation": return "将棋子送入目标区域";
                case "assassination": return "擒获对方核心子";
                case "turn_limit": return maxTurns > 0 ? $"{maxTurns} 回合内完成目标" : "限回合完成目标";
                case "color_coverage": return "点亮足够多的格子";
                case "place": return "完成落子目标";
                default: return "赢得此局";
            }
        }
    }
}
