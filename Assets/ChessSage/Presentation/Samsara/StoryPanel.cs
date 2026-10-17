using System.Collections.Generic;
using ChessSage.Core.Samsara;
using ChessSage.Presentation.Cheat;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// 剧情对话界面：逐句对白 + 分支选择（真源 story.json，经 Core <see cref="ChoiceSystem"/> 读取/结算）。
    ///
    /// 【职责边界】本界面只「显示对白、呈现选项、把选择交回 Core」。选择造成的 alignment / karma
    /// 效果、选择历史、结局走向判定全部在 <see cref="ChoiceSystem"/> 内完成，因此剧情分支可被
    /// 引擎外测试覆盖，界面不持有任何剧情规则。
    ///
    /// 关卡在 story.json 中按 "1".."6" 编号（1 基），而 Core 关卡索引用 0 基 —— 这里统一在
    /// <see cref="Load"/> 里做一次 +1 换算，避免调用方各自记忆偏移。
    /// </summary>
    internal sealed class StoryPanel
    {
        enum Phase { Dialogue, Choice, Response, Done }

        readonly SamsaraRpg _rpg;
        GameObject _root;
        Text _title, _speaker, _body, _progress, _hint;
        Image _portrait;
        Transform _controls;

        string _realm = "hell";
        string _key = "1";
        JObject _level;
        List<JObject> _lines = new List<JObject>();
        JArray _panels;
        int _line, _panel;
        Phase _phase = Phase.Done;
        string _response;

        public GameObject Root => _root;

        public StoryPanel(SamsaraRpg rpg) { _rpg = rpg; }

        public GameObject Build(Transform parent)
        {
            _root = RpgUi.Backdrop("Story", parent);

            _title = Txt(_root.transform, "", 30, TextAnchor.MiddleLeft, RpgUi.Gold);
            RpgUi.Place(_title.rectTransform, 60f, 30f, 1500f, 44f);

            // P2-6：「关闭 ✕」与「← 返回」同为退一层（Back）。此前 ✕ 绑 Close 会
            // 清空整个界面栈直接掉回 hub——剧情是从选关推进来的，关掉剧情理应回到选关。
            var close = Btn(_root.transform, "关闭 ✕", 22, new Color(0.20f, 0.22f, 0.28f, 1f), _rpg.Back);
            RpgUi.Place(RpgUi.Rect(close.gameObject), 1700f, 30f, 160f, 50f);

            var box = RpgUi.NewUi("Box", _root.transform);
            RpgUi.Place(RpgUi.Rect(box), 200f, 180f, 1520f, 520f);
            box.AddComponent<Image>().color = RpgUi.Panel;

            _portrait = RpgUi.NewUi("Portrait", box.transform).AddComponent<Image>();
            RpgUi.Place((RectTransform)_portrait.transform, 32f, 70f, 150f, 150f);

            _speaker = Txt(box.transform, "", 27, TextAnchor.MiddleLeft, RpgUi.Gold);
            RpgUi.Place(_speaker.rectTransform, 210f, 34f, 1270f, 40f);

            _body = Txt(box.transform, "", 27, TextAnchor.UpperLeft, RpgUi.Ink);
            RpgUi.Place(_body.rectTransform, 210f, 84f, 1270f, 400f);

            var controls = RpgUi.NewUi("Controls", _root.transform);
            RpgUi.Place(RpgUi.Rect(controls), 200f, 730f, 1520f, 230f);
            _controls = controls.transform;

            _progress = Txt(_root.transform, "", 19, TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(_progress.rectTransform, 60f, 978f, 900f, 28f);

            _hint = Txt(_root.transform, "", 19, TextAnchor.MiddleRight, RpgUi.Muted);
            RpgUi.Place(_hint.rectTransform, 960f, 978f, 900f, 28f);

            _root.SetActive(false);
            return _root;
        }

        /// <summary>载入某道某关的剧情（levelIndex 为 0 基，内部换算为 story 的 1 基键）。</summary>
        public void Load(string realm, int levelIndex)
        {
            _realm = realm;
            _key = (levelIndex + 1).ToString();
            _level = _rpg.Engine == null ? new JObject() : _rpg.Engine.Choices.GetLevel(realm, _key);

            _lines = new List<JObject>();
            var dlg = _level["dialogues"] as JArray ?? _level["dialogues_before"] as JArray;
            if (dlg != null)
                foreach (var t in dlg) { if (t is JObject o) _lines.Add(o); }

            _panels = _level["choices"] as JArray;
            _line = 0; _panel = 0; _response = null;

            var title = (string)_level["title"];
            var realmName = SamsaraConstants.RealmName(realm);
            _title.text = $"{realmName} · 第 {levelIndex + 1} 关" +
                          (string.IsNullOrEmpty(title) ? "" : $" · {title}");

            if (_lines.Count > 0) _phase = Phase.Dialogue;
            else if (_panels != null && _panels.Count > 0) _phase = Phase.Choice;
            else _phase = Phase.Done;

            Refresh();
        }

        /// <summary>推进对白；处于选择/结束时为空操作（必须先点选项）。</summary>
        public void Next()
        {
            if (_phase == Phase.Dialogue)
            {
                if (_line < _lines.Count - 1) { _line++; }
                else if (_panels != null && _panels.Count > 0) { _phase = Phase.Choice; }
                else _phase = Phase.Done;
            }
            else if (_phase == Phase.Response)
            {
                if (_panels != null && _panel + 1 < _panels.Count) { _panel++; _phase = Phase.Choice; }
                else _phase = Phase.Done;
            }
            Refresh();
        }

        /// <summary>玩家选择第 optionIndex 个选项（结算走 Core.ChoiceSystem.ApplyChoice）。</summary>
        public void Choose(int optionIndex)
        {
            if (_phase != Phase.Choice || _rpg.Engine == null) return;
            var result = _rpg.Engine.Choices.ApplyChoice(_realm, _key, _panel, optionIndex);
            if (result == null || !RpgUi.GetBool(result, "success", false))
            {
                _response = (string)result?["message"] ?? "选择失败";
            }
            else
            {
                var resp = result["response"];
                _response = resp == null || resp.Type == JTokenType.Null
                    ? "（你做出了选择，业力与心相随之悄然变化。）"
                    : resp.ToString();
            }
            _phase = Phase.Response;
            Refresh();
        }

        /// <summary>试玩取证辅助：一路推进对白直到出现选项（无选项则推进到底）。</summary>
        public void AdvanceToChoice()
        {
            int guard = 0;
            while (_phase == Phase.Dialogue && guard++ < 64) Next();
        }

        // ── 渲染 ──────────────────────────────────────────────────────

        void Refresh()
        {
            if (_root == null) return;
            RpgUi.ClearChildren(_controls);
            _progress.text = "";
            _hint.text = _phase == Phase.Dialogue ? "点「继续」或按空格推进对白" : "";

            switch (_phase)
            {
                case Phase.Dialogue:
                    RenderDialogue();
                    break;
                case Phase.Choice:
                    RenderChoice();
                    break;
                case Phase.Response:
                    _speaker.text = "心念";
                    _body.text = _response ?? "";
                    SetPortrait(RpgUi.Violet, "心");
                    BigButton("继续 ▶", () => Next());
                    break;
                default:
                    _speaker.text = "本关剧情";
                    _body.text = string.IsNullOrEmpty(_response)
                        ? "这一关的剧情到此为止。返回可继续选择其它关卡。"
                        : _response + "\n\n（本关剧情到此为止。）";
                    SetPortrait(RpgUi.Muted, "终");
                    BigButton("返回 ↩", () => _rpg.Back());
                    break;
            }
        }

        void RenderDialogue()
        {
            var line = _lines[Mathf.Clamp(_line, 0, _lines.Count - 1)];
            var speaker = RpgUi.GetStr(line, "speaker", "旁白");
            _speaker.text = SpeakerLabel(speaker);
            _body.text = RpgUi.GetStr(line, "text", "");
            SetPortrait(SpeakerColor(speaker), SpeakerInitial(speaker));
            _progress.text = $"对白 {_line + 1}/{_lines.Count}" +
                             (_panels != null && _panels.Count > 0 ? " · 之后将出现抉择" : "");
            BigButton("继续 ▶", () => Next());
        }

        void RenderChoice()
        {
            var panel = _panels != null && _panel < _panels.Count ? _panels[_panel] as JObject : null;
            _speaker.text = "抉择";
            _body.text = RpgUi.GetStr(panel, "prompt", "你将如何选择？");
            SetPortrait(RpgUi.Gold, "择");
            _progress.text = $"抉择 {_panel + 1}/{(_panels == null ? 1 : _panels.Count)}";

            var options = panel?["options"] as JArray;
            if (options == null) { BigButton("继续 ▶", () => Next()); return; }

            for (int i = 0; i < options.Count; i++)
            {
                int captured = i;
                var opt = options[i] as JObject;
                var btn = RpgUi.ClickableCard(_controls, RpgUi.Card, () => Choose(captured));
                var root = btn.gameObject;
                RpgUi.Place(RpgUi.Rect(root), 0f, i * 84f, 1520f, 76f);

                var t1 = Txt(root.transform, RpgUi.GetStr(opt, "text", ""), 23, TextAnchor.MiddleLeft, RpgUi.Ink);
                RpgUi.Place(t1.rectTransform, 22f, 8f, 1180f, 32f);

                var hint = RpgUi.GetStr(opt, "hint", "");
                var effect = EffectLabel(opt?["effect"] as JObject);
                var t2 = Txt(root.transform, $"（{hint}）   {effect}", 18, TextAnchor.MiddleLeft, RpgUi.Amber);
                RpgUi.Place(t2.rectTransform, 22f, 42f, 1460f, 26f);
            }
        }

        void BigButton(string label, System.Action onClick)
        {
            var btn = Btn(_controls, label, 24, new Color(0.18f, 0.28f, 0.42f, 1f), onClick);
            RpgUi.Place(RpgUi.Rect(btn.gameObject), 0f, 0f, 300f, 60f);
        }

        void SetPortrait(Color color, string initial)
        {
            _portrait.color = new Color(color.r, color.g, color.b, 0.35f);
            // 头像位无美术资源：用带首字的色块占位（与「程序化 UI」总方针一致）。
            var existing = _portrait.transform.Find("Initial");
            if (existing == null)
            {
                var t = CheatUiKit.Text(_portrait.transform, initial, 60, TextAnchor.MiddleCenter, RpgUi.Ink);
                t.name = "Initial";
                RpgUi.Stretch(t.rectTransform);
            }
            else
            {
                existing.GetComponent<Text>().text = initial;
            }
        }

        static string SpeakerLabel(string speaker)
        {
            switch (speaker)
            {
                case "narrator": return "旁白";
                case "guide": return "向导";
                case "protagonist": return "林夜";
                default: return speaker;
            }
        }

        static string SpeakerInitial(string speaker)
        {
            if (string.IsNullOrEmpty(speaker)) return "？";
            if (speaker == "narrator") return "叙";
            var label = SpeakerLabel(speaker);
            return label.Length > 0 ? label.Substring(0, 1) : "？";
        }

        static Color SpeakerColor(string speaker)
        {
            switch (speaker)
            {
                case "guide": return RpgUi.Cyan;
                case "narrator": return RpgUi.Muted;
                case "林夜":
                case "protagonist": return RpgUi.Green;
                default: return RpgUi.Violet;
            }
        }

        static string EffectLabel(JObject effect)
        {
            if (effect == null || effect.Count == 0) return "无属性变化";
            var parts = new List<string>();
            foreach (var prop in effect.Properties())
            {
                var name = prop.Name switch
                {
                    "enlightenment" => "悟道",
                    "corruption" => "堕落",
                    "rationality" => "理性",
                    "emotion" => "情感",
                    _ => prop.Name,
                };
                int v = prop.Value.Type == JTokenType.Integer ? prop.Value.Value<int>() : 0;
                parts.Add(v >= 0 ? $"{name} +{v}" : $"{name} {v}");
            }
            return string.Join("，", parts);
        }

        Text Txt(Transform parent, string text, int size, TextAnchor anchor, Color color)
            => CheatUiKit.Text(parent, text, size, anchor, color);

        Button Btn(Transform parent, string label, int size, Color bg, System.Action onClick)
            => CheatUiKit.Button(parent, label, size, bg, onClick);
    }
}
