using System.Collections.Generic;
using ChessSage.Presentation.Cheat;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// 结局演出界面：按 <see cref="ChessSage.Core.Samsara.EndingSystem"/> 的结局类型，
    /// 呈现不同文本与视觉基调（善终暖金 / 恶终暗红 / 轮回冷青 / 真我明金 / 识破墨红）。
    ///
    /// 【数据真源】结局名、描述、对白、尾声全部来自 story.json 的 <c>endings</c> 段
    /// （经 <c>GetEndingData</c> 注入 prayer_count 并替换占位符）。本界面不自创任何结局文案，
    /// 因此与 Core/Samsara 的结局命名严格一致。
    ///
    /// 【为何可切换】顶部结局选择器让玩家/取证者在同屏浏览全部 5 种结局基调；
    /// 未解锁的结局标注「未解锁」，但内容照常可预览（演出层不设权限，仅做展示）。
    /// </summary>
    internal sealed class EndingPanel
    {
        static readonly string[] EndingIds = { "enlightenment", "corruption", "samsara", "true_me", "exposed" };

        readonly SamsaraRpg _rpg;
        GameObject _root;
        Image _bg;
        Transform _selector, _dialogueList;
        Text _name, _badge, _desc, _epilogue, _unlockText;

        public GameObject Root => _root;

        public EndingPanel(SamsaraRpg rpg) { _rpg = rpg; }

        public GameObject Build(Transform parent)
        {
            _root = RpgUi.Backdrop("Ending", parent);
            _bg = _root.GetComponent<Image>();

            var title = Txt("结局演出 · 六道终章", 40, TextAnchor.MiddleCenter, RpgUi.Gold);
            RpgUi.Place(title.rectTransform, 0f, 20f, 1920f, 52f);

            var sel = RpgUi.NewUi("Selector", _root.transform);
            RpgUi.Place(RpgUi.Rect(sel), 178f, 84f, 1564f, 56f);
            _selector = sel.transform;

            // 左：结局名 / 类型 / 描述
            _name = Txt("", 46, TextAnchor.UpperLeft, RpgUi.Ink);
            RpgUi.Place(_name.rectTransform, 120f, 166f, 760f, 120f);

            _badge = Txt("", 24, TextAnchor.MiddleLeft, RpgUi.Gold);
            RpgUi.Place(_badge.rectTransform, 120f, 280f, 760f, 40f);

            _desc = Txt("", 23, TextAnchor.UpperLeft, new Color(0.82f, 0.86f, 0.92f, 1f));
            RpgUi.Place(_desc.rectTransform, 120f, 330f, 760f, 260f);

            _unlockText = Txt("", 20, TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(_unlockText.rectTransform, 120f, 620f, 760f, 80f);

            // 右：对白
            var dlgHead = Txt("◇ 剧情对白", 22, TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(dlgHead.rectTransform, 920f, 166f, 880f, 30f);

            var list = RpgUi.NewUi("Dialogues", _root.transform);
            RpgUi.Place(RpgUi.Rect(list), 920f, 206f, 880f, 494f);
            _dialogueList = list.transform;

            var epiHead = Txt("◇ 尾声", 22, TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(epiHead.rectTransform, 120f, 712f, 400f, 28f);

            var epiBox = RpgUi.NewUi("Epilogue", _root.transform);
            RpgUi.Place(RpgUi.Rect(epiBox), 120f, 744f, 1680f, 150f);
            epiBox.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.28f);
            _epilogue = CheatUiKit.Text(epiBox.transform, "", 23, TextAnchor.UpperLeft,
                new Color(0.88f, 0.90f, 0.94f, 1f));
            RpgUi.Stretch(_epilogue.rectTransform, 22f, 14f, 22f, 14f);

            var back = Btn(_root.transform, "← 返回", 24, new Color(0.20f, 0.22f, 0.28f, 1f), _rpg.Back);
            RpgUi.Place(RpgUi.Rect(back.gameObject), 60f, 912f, 200f, 60f);

            // P2-6：「关闭 ✕」与「← 返回」同为退一层（Back）。此前 ✕ 绑 Close 会
            // 清空整个界面栈直接掉回 hub——结局界面也可能是从选关/剧情推进来的。
            var close = Btn(_root.transform, "关闭 ✕", 24, new Color(0.20f, 0.22f, 0.28f, 1f), _rpg.Back);
            RpgUi.Place(RpgUi.Rect(close.gameObject), 1700f, 912f, 160f, 60f);

            _root.SetActive(false);
            return _root;
        }

        /// <summary>展示指定结局（endingId ∈ {enlightenment, corruption, samsara, true_me, exposed}）。</summary>
        public void Show(string endingId)
        {
            if (_root == null || _rpg.Engine == null) return;
            var data = _rpg.Engine.Endings.GetEndingData(endingId);
            var type = RpgUi.GetStr(data, "type", "");
            var tone = ResolveTone(type);

            _bg.color = tone.Background;
            _name.text = RpgUi.GetStr(data, "name", endingId);
            _name.color = tone.Accent;
            _badge.text = $"结局类型：{type} · {TypeLabel(type)}";
            _badge.color = tone.Accent;
            _desc.text = Wrap(RpgUi.GetStr(data, "description", "（暂无描述）"), 20);

            bool unlocked = IsUnlocked(endingId);
            _unlockText.text = unlocked
                ? "<color=#6BD08A>✓ 此结局已解锁</color>"
                : "<color=#9AA0AC>○ 此结局尚未解锁（此处为演出预览）</color>";
            _unlockText.supportRichText = true;

            BuildSelector(endingId);
            BuildDialogues(data, tone);
            _epilogue.text = RpgUi.GetStr(data, "epilogue", "（此结局暂无尾声文本）");
        }

        void BuildSelector(string current)
        {
            RpgUi.ClearChildren(_selector);
            for (int i = 0; i < EndingIds.Length; i++)
            {
                var id = EndingIds[i];
                bool active = id == current;
                bool unlocked = IsUnlocked(id);
                var data = _rpg.Engine.Endings.GetEndingData(id);
                var nm = RpgUi.GetStr(data, "name", id);
                if (nm.Contains("·")) nm = nm.Substring(nm.IndexOf('·') + 1);
                string label = (unlocked ? "◆ " : "◇ ") + nm;

                var bg = active ? new Color(0.24f, 0.30f, 0.44f, 1f) : new Color(0.12f, 0.14f, 0.19f, 1f);
                var captured = id;
                var btn = RpgUi.ClickableCard(_selector, bg, () => Show(captured));
                RpgUi.Place(RpgUi.Rect(btn.gameObject), i * 316f, 0f, 300f, 56f);
                var t = CheatUiKit.Text(btn.transform, label, 21, TextAnchor.MiddleCenter,
                    active ? RpgUi.Gold : RpgUi.Muted);
                RpgUi.Stretch(t.rectTransform, 8f, 2f, 8f, 2f);
            }
        }

        void BuildDialogues(JObject data, Tone tone)
        {
            RpgUi.ClearChildren(_dialogueList);
            var dlg = data["dialogues"] as JArray ?? data["dialogues_before"] as JArray;
            if (dlg == null || dlg.Count == 0)
            {
                var t = CheatUiKit.Text(_dialogueList, "（此结局暂无对白）", 22, TextAnchor.UpperLeft, RpgUi.Muted);
                RpgUi.Place(t.rectTransform, 0f, 0f, 880f, 40f);
                return;
            }

            int count = Mathf.Min(dlg.Count, 5);
            for (int i = 0; i < count; i++)
            {
                var line = dlg[i] as JObject;
                var speaker = RpgUi.GetStr(line, "speaker", "旁白");
                if (speaker == "narrator") speaker = "旁白";
                var text = RpgUi.GetStr(line, "text", "");

                var card = RpgUi.NewUi("Line", _dialogueList);
                RpgUi.Place(RpgUi.Rect(card), 0f, i * 98f, 880f, 90f);
                card.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.22f);

                var sp = CheatUiKit.Text(card.transform, speaker, 20, TextAnchor.MiddleLeft, tone.Accent);
                RpgUi.Place(sp.rectTransform, 16f, 8f, 300f, 26f);

                var body = CheatUiKit.Text(card.transform, Wrap(text, 34), 20, TextAnchor.UpperLeft,
                    new Color(0.90f, 0.92f, 0.96f, 1f));
                RpgUi.Place(body.rectTransform, 16f, 36f, 848f, 50f);
            }

            if (dlg.Count > count)
            {
                var more = CheatUiKit.Text(_dialogueList, $"…（另有 {dlg.Count - count} 句对白已省略）", 19,
                    TextAnchor.UpperLeft, RpgUi.Muted);
                RpgUi.Place(more.rectTransform, 0f, count * 98f, 880f, 26f);
            }
        }

        bool IsUnlocked(string endingId)
        {
            var status = _rpg.Engine.Endings.GetAllEndingsStatus();
            return RpgUi.GetBool(status[endingId] as JObject, "unlocked", false);
        }

        // ── 视觉基调 ──────────────────────────────────────────────────
        struct Tone { public Color Background; public Color Accent; }

        static Tone ResolveTone(string type)
        {
            switch (type)
            {
                case "good": return new Tone { Background = new Color(0.055f, 0.085f, 0.070f, 1f), Accent = RpgUi.Green };
                case "bad": return new Tone { Background = new Color(0.100f, 0.040f, 0.050f, 1f), Accent = RpgUi.Red };
                case "true": return new Tone { Background = new Color(0.085f, 0.075f, 0.045f, 1f), Accent = RpgUi.Gold };
                case "worst": return new Tone { Background = new Color(0.090f, 0.020f, 0.030f, 1f), Accent = new Color(0.95f, 0.42f, 0.36f, 1f) };
                case "neutral":
                default: return new Tone { Background = new Color(0.050f, 0.062f, 0.090f, 1f), Accent = RpgUi.Cyan };
            }
        }

        static string TypeLabel(string type)
        {
            switch (type)
            {
                case "good": return "善终";
                case "bad": return "恶终";
                case "neutral": return "轮回";
                case "true": return "真我";
                case "worst": return "识破";
                default: return type;
            }
        }

        /// <summary>简易折行：每 limit 个字符插入换行，避免长段落在固定框里溢出。</summary>
        static string Wrap(string text, int limit)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= limit) return text ?? "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                sb.Append(text[i]);
                if ((i + 1) % limit == 0 && text[i] != '\n') sb.Append('\n');
            }
            return sb.ToString();
        }

        Text Txt(string text, int size, TextAnchor anchor, Color color)
            => CheatUiKit.Text(_root.transform, text, size, anchor, color);

        Button Btn(Transform parent, string label, int size, Color bg, System.Action onClick)
            => CheatUiKit.Button(parent, label, size, bg, onClick);
    }
}
