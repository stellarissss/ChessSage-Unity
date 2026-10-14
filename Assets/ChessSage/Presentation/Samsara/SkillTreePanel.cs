using System.Collections.Generic;
using ChessSage.Presentation.Cheat;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// 技能树界面：4 分支 × 3 层（真源 skill_tree.json / Core.SkillSystem）。
    ///
    /// 【状态单源】节点「已解锁 / 可解锁 / 锁定」由 Core 的
    /// <c>SamsaraEngine.GetSkillTreeView</c> 计算（字段 <c>state</c>），本界面只负责上色与点击——
    /// 不自己重算前置，避免「界面说可点、实际解锁失败」。
    ///
    /// 【业力折扣】顶部实时汇总 <c>GetSkillModifiers</c> 的生效项（业力减免、识破系数、免费作弊等），
    /// 让「解锁这些技能到底改变了什么」一目了然。
    /// </summary>
    internal sealed class SkillTreePanel
    {
        readonly SamsaraRpg _rpg;
        GameObject _root;
        Text _pointsText, _discountText;
        Transform _columns;

        public GameObject Root => _root;

        public SkillTreePanel(SamsaraRpg rpg) { _rpg = rpg; }

        public GameObject Build(Transform parent)
        {
            _root = RpgUi.Backdrop("SkillTree", parent);

            var title = Txt(_root.transform, "技能树 · 四道修行", 44, TextAnchor.MiddleCenter, RpgUi.Gold);
            RpgUi.Place(title.rectTransform, 0f, 22f, 1920f, 56f);

            _pointsText = Txt(_root.transform, "", 24, TextAnchor.MiddleLeft, RpgUi.Ink);
            RpgUi.Place(_pointsText.rectTransform, 60f, 90f, 560f, 34f);

            _discountText = Txt(_root.transform, "", 20, TextAnchor.MiddleRight, RpgUi.Muted);
            _discountText.supportRichText = true;
            RpgUi.Place(_discountText.rectTransform, 600f, 90f, 1260f, 34f);

            var hint = Txt(_root.transform, "点「可解锁」节点消耗技能点；灰色节点需先解锁上一层任一技能。", 19,
                TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(hint.rectTransform, 60f, 126f, 1800f, 28f);

            var cols = RpgUi.NewUi("Columns", _root.transform);
            RpgUi.Place(RpgUi.Rect(cols), 50f, 166f, 1820f, 806f);
            _columns = cols.transform;

            var back = Btn(_root.transform, "← 返回", 24, new Color(0.20f, 0.22f, 0.28f, 1f), _rpg.Back);
            RpgUi.Place(RpgUi.Rect(back.gameObject), 60f, 986f, 200f, 62f);

            _root.SetActive(false);
            return _root;
        }

        public void Refresh()
        {
            if (_root == null || _rpg.Engine == null) return;

            int points = _rpg.Engine.GetSkillPoints();
            _pointsText.text = $"剩余技能点：{points}";
            _discountText.text = BuildDiscountSummary(_rpg.Engine.GetSkillModifiers());

            RpgUi.ClearChildren(_columns);
            var view = _rpg.Engine.GetSkillTreeView();
            int i = 0;
            foreach (var branchProp in view.Properties())
            {
                BuildColumn((JObject)branchProp.Value, i, points);
                i++;
            }
        }

        void BuildColumn(JObject branch, int index, int points)
        {
            var name = (string)branch["name"];
            var desc = (string)branch["description"];
            var icon = (string)branch["icon"];

            var holder = RpgUi.NewUi("Branch", _columns);
            RpgUi.Place(RpgUi.Rect(holder), index * 460f, 0f, 440f, 806f);

            var head = RpgUi.NewUi("Head", holder.transform);
            RpgUi.Place(RpgUi.Rect(head), 0f, 0f, 440f, 100f);
            head.AddComponent<Image>().color = RpgUi.Panel;

            var nameText = Txt(head.transform, $"{icon}  {name}", 25, TextAnchor.MiddleLeft, RpgUi.Ink);
            RpgUi.Stretch(nameText.rectTransform, 16f, 12f, 12f, 52f);

            var descText = Txt(head.transform, desc ?? "", 17, TextAnchor.UpperLeft, RpgUi.Muted);
            RpgUi.Stretch(descText.rectTransform, 16f, 46f, 12f, 10f);

            var tiers = branch["tiers"] as JObject;
            if (tiers == null) return;

            float y = 110f;
            foreach (var tierProp in tiers.Properties())
            {
                var tierData = (JObject)tierProp.Value;
                int tier = RpgUi.GetInt(tierData, "tier", 1);

                var label = Txt(holder.transform, $"第 {tier} 层", 19, TextAnchor.MiddleLeft, RpgUi.Muted);
                RpgUi.Place(label.rectTransform, 6f, y, 428f, 24f);
                y += 26f;

                var options = tierData["options"] as JArray;
                if (options != null)
                {
                    foreach (var o in options) { BuildNode(holder.transform, (JObject)o, y, points); y += 92f; }
                }
                else
                {
                    BuildNode(holder.transform, tierData, y, points);
                    y += 92f;
                }
                y += 6f;
            }
        }

        void BuildNode(Transform parent, JObject node, float y, int points)
        {
            var id = (string)node["id"];
            var name = (string)node["name"];
            var desc = (string)node["description"];
            int cost = RpgUi.GetInt(node, "cost", 1);
            int tier = RpgUi.GetInt(node, "tier", 1);
            var state = (string)node["state"];

            bool unlocked = state == "unlocked";
            bool available = state == "available";
            bool affordable = points >= cost;
            bool interactable = available && affordable;

            Color accent = unlocked ? RpgUi.Green
                : available ? (affordable ? RpgUi.Cyan : RpgUi.Amber)
                : RpgUi.Locked;
            Color bg = unlocked ? new Color(0.10f, 0.18f, 0.13f, 1f)
                : available ? RpgUi.Card
                : RpgUi.CardLocked;

            var btn = RpgUi.ClickableCard(parent, bg, interactable ? (System.Action)(() => Unlock(id, tier)) : null);
            var root = btn.gameObject;
            RpgUi.Place(RpgUi.Rect(root), 0f, y, 440f, 86f);
            btn.interactable = interactable;

            var bar = RpgUi.NewUi("Accent", root.transform);
            RpgUi.Place(RpgUi.Rect(bar), 0f, 0f, 6f, 86f);
            bar.AddComponent<Image>().color = accent;

            var nameText = Txt(root.transform, name, 22, TextAnchor.MiddleLeft, unlocked ? RpgUi.Green : RpgUi.Ink);
            RpgUi.Place(nameText.rectTransform, 16f, 8f, 330f, 30f);

            string stateText = unlocked ? "已解锁" : available ? (affordable ? $"{cost} 点" : $"{cost} 点·不足") : "未解锁";
            Color stateColor = unlocked ? RpgUi.Green : available ? (affordable ? RpgUi.Gold : RpgUi.Red) : RpgUi.Locked;
            var costText = Txt(root.transform, stateText, 17, TextAnchor.MiddleRight, stateColor);
            RpgUi.Place(costText.rectTransform, 352f, 8f, 80f, 30f);

            var descText = Txt(root.transform, desc ?? "", 16, TextAnchor.UpperLeft, RpgUi.Muted);
            RpgUi.Place(descText.rectTransform, 16f, 40f, 414f, 42f);
        }

        void Unlock(string id, int tier)
        {
            if (_rpg.Engine == null) return;
            if (_rpg.Engine.UnlockSkill(id, tier))
            {
                Debug.Log($"[六道] 已解锁技能：{id}（第 {tier} 层）");
                Refresh();
            }
        }

        /// <summary>把生效的技能修饰符翻译成一行「业力折扣」摘要（仅显示，不参与逻辑）。</summary>
        static string BuildDiscountSummary(JObject mod)
        {
            var items = new List<string>();
            int karmaMaxBonus = RpgUi.GetInt(mod, "karma_max_bonus");
            if (karmaMaxBonus > 0) items.Add($"业力安全线 +{karmaMaxBonus}");
            int single = RpgUi.GetInt(mod, "karma_single_max_bonus");
            if (single > 0) items.Add($"单次业力上限 +{single}");
            double rec = RpgUi.GetDouble(mod, "karma_recover_multiplier", 1.0);
            if (rec > 1.0001) items.Add($"消业效率 +{Mathf.RoundToInt((float)((rec - 1) * 100))}%");
            int initRed = RpgUi.GetInt(mod, "initial_karma_reduction");
            if (initRed > 0) items.Add($"初始业力 -{initRed}");
            if (RpgUi.GetBool(mod, "hell_hungry_discount")) items.Add("地狱/饿鬼道 作弊消耗 -25%");
            if (RpgUi.GetBool(mod, "heaven_asura_discount")) items.Add("天道/阿修罗道 透支惩罚 -25%");
            if (RpgUi.GetBool(mod, "boss_skill_reduction")) items.Add("守道者技能触发 -30%");
            if (RpgUi.GetBool(mod, "free_cheat_on_realm_change")) items.Add("升/降道 +1 次免费作弊");
            int free = RpgUi.GetInt(mod, "free_cheat_count");
            if (free > 0) items.Add($"每局白嫖 ×{free}");
            double alpha = RpgUi.GetDouble(mod, "detection_alpha", 1.5);
            if (alpha < 1.4999) items.Add($"识破指数 {alpha:0.0}");
            double coef = RpgUi.GetDouble(mod, "detection_coefficient", 0.1);
            if (coef < 0.0999) items.Add($"识破惩罚系数 {coef:0.00}");

            if (items.Count == 0)
                return "<color=#9AA0AC>当前无业力折扣 · 解锁「业力掌控 / 六道悟道 / 隐匿之术」可获得</color>";
            return "业力折扣：  " + string.Join("   ·   ", items);
        }

        Text Txt(Transform parent, string text, int size, TextAnchor anchor, Color color)
            => CheatUiKit.Text(parent, text, size, anchor, color);

        Button Btn(Transform parent, string label, int size, Color bg, System.Action onClick)
            => CheatUiKit.Button(parent, label, size, bg, onClick);
    }
}
