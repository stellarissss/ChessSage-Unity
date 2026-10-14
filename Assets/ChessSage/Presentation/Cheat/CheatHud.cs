using ChessSage.Core.Cheat;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ChessSage.Presentation.Cheat
{
    /// <summary>
    /// 业力 / 识破 HUD（UX §9）：对局屏顶栏常驻，含「数值 + 进度条 + 图标」四条目
    /// （业力 / 识破 / 回合 / 目标·道），并在业力条 100% 处画金色安全线刻度。
    ///
    /// 变化反馈（UX §9.2）：施展涨业力 → 条发光；溢出 → 条尾变红脉动 + 文字提示；
    /// 被识破 → 由面板弹全屏卡。这里用「文字 + 颜色脉动」做多通道反馈。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CheatHud : MonoBehaviour
    {
        CheatController _controller;

        Canvas _canvas;
        Text _karmaText, _detectionText, _turnText, _objectiveText, _messageText;
        Image _karmaFill, _detectionFill, _safeLine;
        float _pulse;
        Color _pulseColor;

        public void Bind(CheatController controller)
        {
            _controller = controller;
            Build();
            Refresh();
        }

        public void SetVisible(bool visible)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(visible);
        }

        void Build()
        {
            if (_canvas != null) return;
            CheatUiKit.EnsureEventSystem(transform);

            var go = new GameObject("CheatHudCanvas");
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var bar = CheatUiKit.Panel("HudBar", _canvas.transform, CheatUiKit.PanelBg);
            CheatUiKit.AnchorTopStretch(bar.gameObject, 92f, 0f, 0f);

            // ── 业力 ──
            _karmaText = CheatUiKit.Text(bar.transform, "☯ 业力 0/120", 26, TextAnchor.MiddleLeft);
            Place(_karmaText, 24f, 10f, 340f, 34f);
            var karmaTrack = Track(bar.transform, 24f, 50f, 340f, 22f, out _karmaFill);
            _safeLine = CheatUiKit.Panel("SafeLine", karmaTrack.transform, CheatUiKit.Gold, false).GetComponent<Image>();
            var sl = (RectTransform)_safeLine.transform;
            sl.anchorMin = new Vector2(1f, 0f);
            sl.anchorMax = new Vector2(1f, 1f);
            sl.pivot = new Vector2(1f, 0.5f);
            sl.anchoredPosition = new Vector2(-2f, 0f);
            sl.sizeDelta = new Vector2(4f, 6f);

            // ── 识破 ──
            _detectionText = CheatUiKit.Text(bar.transform, "👁 识破 0%", 26, TextAnchor.MiddleLeft);
            Place(_detectionText, 400f, 10f, 340f, 34f);
            Track(bar.transform, 400f, 50f, 340f, 22f, out _detectionFill);

            // ── 回合 ──
            _turnText = CheatUiKit.Text(bar.transform, "⏱ 回合 1/20", 24, TextAnchor.MiddleLeft);
            Place(_turnText, 780f, 12f, 320f, 32f);

            // ── 目标 & 道 ──
            _objectiveText = CheatUiKit.Text(bar.transform, "🎯 目标：—", 24, TextAnchor.MiddleLeft);
            Place(_objectiveText, 1120f, 12f, 700f, 32f);

            _messageText = CheatUiKit.Text(bar.transform, "", 22, TextAnchor.MiddleLeft, CheatUiKit.Amber);
            Place(_messageText, 780f, 50f, 1040f, 32f);
        }

        static void Place(Text t, float x, float y, float w, float h)
        {
            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        static RectTransform Track(Transform parent, float x, float y, float w, float h, out Image fill)
        {
            var trackImg = CheatUiKit.Panel("Track", parent, new Color(0.16f, 0.18f, 0.23f, 1f), false);
            var rt = CheatUiKit.Rect(trackImg.gameObject);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);

            fill = CheatUiKit.Panel("Fill", trackImg.transform, CheatUiKit.Green, false).GetComponent<Image>();
            var fr = CheatUiKit.Rect(fill.gameObject);
            fr.anchorMin = new Vector2(0f, 0f);
            fr.anchorMax = new Vector2(0f, 1f);
            fr.pivot = new Vector2(0f, 0.5f);
            fr.offsetMin = new Vector2(0f, 0f);
            fr.offsetMax = new Vector2(0f, 0f);
            fr.sizeDelta = new Vector2(0f, 0f);
            return rt;
        }

        /// <summary>刷新 HUD（每次对局刷新 / 确认施展后调用）。</summary>
        public void Refresh()
        {
            if (_canvas == null) return;
            if (_controller == null)
            {
                _karmaText.text = "☯ 业力 —";
                _detectionText.text = "👁 识破 —";
                _turnText.text = "⏱ 回合 —";
                _objectiveText.text = "🎯 目标：—";
                return;
            }

            var st = _controller.GetHudState();
            int karma = GetInt(st, "karma");
            int karmaMax = Mathf.Max(1, GetInt(st, "karma_max", 120));
            double detection = GetDouble(st, "detection");
            var locked = GetBool(st, "detection_state", "detection_locked");
            int turn = GetInt(st, "current_turn");
            int turnLimit = GetInt(st, "level_turn_limit", 20);
            var realm = GetStr(st, "current_realm_name", "地狱道");

            _karmaText.text = $"☯ 业力 {karma}/{karmaMax}";
            var karmaRatio = Mathf.Clamp01((float)karma / karmaMax);
            _karmaFill.rectTransform.anchorMax = new Vector2(karmaRatio, 1f);
            _karmaFill.color = karma >= karmaMax ? CheatUiKit.Red
                : karma > karmaMax * 0.7f ? CheatUiKit.Amber : CheatUiKit.Green;

            _detectionText.text = locked ? "👁 识破 🔒 已锁死" : $"👁 识破 {detection:0}%";
            var detRatio = Mathf.Clamp01((float)(detection / 100.0));
            _detectionFill.rectTransform.anchorMax = new Vector2(detRatio, 1f);
            _detectionFill.color = detection >= 70 ? CheatUiKit.Red
                : detection >= 40 ? CheatUiKit.Amber : new Color(0.55f, 0.65f, 0.35f, 1f);

            _turnText.text = $"⏱ 回合 {Mathf.Max(1, turn + 1)}/{turnLimit}";
            _objectiveText.text = $"🎯 目标：{ObjectiveLabel()} · {realm}";
        }

        string ObjectiveLabel()
        {
            var obj = _controller.GetObjective();
            if (obj == null) return "—";
            var desc = GetStr(obj, "description", null);
            if (!string.IsNullOrEmpty(desc)) return desc;
            var display = GetStr(obj, "display_name", null);
            if (!string.IsNullOrEmpty(display)) return display;
            switch (GetStr(obj, "type", "checkmate"))
            {
                case "checkmate": return "将死对方";
                case "capture": return "达成吃子目标";
                case "capture_count": return "达成吃子目标";
                case "survive": return "存活到回合结束";
                case "place": return "完成落子目标";
                default: return "赢得此局";
            }
        }

        /// <summary>变化反馈：闪烁提示文字 + 对应条脉动一次（UX §9.2）。</summary>
        public void Flash(string message, bool detectionPulse = false)
        {
            if (_messageText == null) return;
            _messageText.text = message ?? "";
            _messageText.color = CheatUiKit.Amber;
            _pulse = 1f;
            _pulseColor = detectionPulse ? CheatUiKit.Red : CheatUiKit.Gold;
            Refresh();
        }

        void Update()
        {
            if (_pulse <= 0f || _karmaFill == null) return;
            _pulse -= Time.unscaledDeltaTime;
            var k = Mathf.Clamp01(_pulse);
            var blink = Mathf.Abs(Mathf.Sin(Time.unscaledTime * 12f));
            _karmaFill.color = Color.Lerp(_karmaFill.color, _pulseColor, blink * k);
            if (_pulse <= 0f && _controller != null) Refresh();
        }

        static int GetInt(JObject o, string k, int def = 0)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<int>();
            if (v.Type == JTokenType.Float) return (int)v.Value<double>();
            return def;
        }

        static double GetDouble(JObject o, string k, double def = 0.0)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<long>();
            if (v.Type == JTokenType.Float) return v.Value<double>();
            return def;
        }

        static bool GetBool(JObject o, string sub, string k)
        {
            var s = o?[sub] as JObject;
            var v = s?[k];
            return v != null && v.Type == JTokenType.Boolean && v.Value<bool>();
        }

        static string GetStr(JObject o, string k, string def = "")
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }
    }
}
