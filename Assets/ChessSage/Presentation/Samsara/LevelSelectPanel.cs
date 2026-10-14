using ChessSage.Presentation.Cheat;   // 复用 CheatUiKit（同程序集）
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// 选关界面：左列六道（<see cref="ChessSage.Core.Samsara.SamsaraConstants.Realms"/>），右列所选道的关卡。
    ///
    /// 【数据真源】关卡与状态全部来自 Core 的 LevelSystem（真源 level_pools.json）；本界面不含任何
    /// 关卡常量，因此新增/调整关卡只改配置即可，界面自动跟随。道名一律取配置里的 <c>name</c>
    /// （与 Core/Samsara 既有文案一致），不在此处自创译名。
    ///
    /// 【进入路径】「进入」= 先写入 Samsara 状态（current_realm / current_level），再经组合根
    /// <c>Bootstrap.LaunchVariant</c> 启动对应棋种——表现层不 new 任何 Core 会话对象。
    /// </summary>
    internal sealed class LevelSelectPanel
    {
        readonly SamsaraRpg _rpg;
        GameObject _root;
        Transform _realmList, _levelList;
        Text _realmHeader, _realmSub;
        string _selectedRealm = "hell";

        public GameObject Root => _root;

        public LevelSelectPanel(SamsaraRpg rpg) { _rpg = rpg; }

        public GameObject Build(Transform parent)
        {
            _root = RpgUi.Backdrop("LevelSelect", parent);

            var title = Txt(_root.transform, "六道轮回 · 择道", 46, TextAnchor.MiddleCenter, RpgUi.Gold);
            RpgUi.Place(title.rectTransform, 0f, 26f, 1920f, 58f);

            var sub = Txt(_root.transform, "选择一道，查看关卡、目标与回合上限；点「进入」即刻开局。", 22,
                TextAnchor.MiddleCenter, RpgUi.Muted);
            RpgUi.Place(sub.rectTransform, 0f, 90f, 1920f, 30f);

            BuildRealmList();
            BuildLevelPane();
            BuildNav();

            _root.SetActive(false);
            return _root;
        }

        void BuildRealmList()
        {
            var holder = RpgUi.NewUi("RealmList", _root.transform);
            RpgUi.Place(RpgUi.Rect(holder), 60f, 150f, 520f, 806f);
            _realmList = holder.transform;
        }

        void BuildLevelPane()
        {
            var holder = RpgUi.NewUi("LevelPane", _root.transform);
            RpgUi.Place(RpgUi.Rect(holder), 620f, 150f, 1240f, 806f);

            var bg = RpgUi.NewUi("PaneBg", holder.transform);
            RpgUi.Stretch(RpgUi.Rect(bg));
            var img = bg.AddComponent<Image>();
            img.color = RpgUi.Panel;
            img.raycastTarget = false;

            _realmHeader = Txt(holder.transform, "", 34, TextAnchor.MiddleLeft, RpgUi.Ink);
            RpgUi.Place(_realmHeader.rectTransform, 28f, 18f, 1180f, 44f);

            _realmSub = Txt(holder.transform, "", 21, TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(_realmSub.rectTransform, 28f, 62f, 1180f, 28f);

            var list = RpgUi.NewUi("Levels", holder.transform);
            RpgUi.Place(RpgUi.Rect(list), 20f, 104f, 1200f, 700f);
            _levelList = list.transform;
        }

        void BuildNav()
        {
            var back = Btn(_root.transform, "← 返回", 24, new Color(0.20f, 0.22f, 0.28f, 1f), _rpg.Close);
            RpgUi.Place(RpgUi.Rect(back.gameObject), 60f, 986f, 200f, 62f);

            float x = 932f;
            AddNav("技能树", x, () => _rpg.ShowSkills()); x += 236f;
            AddNav("剧情对话", x, () => _rpg.ShowStory("hell", 2)); x += 236f;
            AddNav("结局演出", x, () => _rpg.ShowEnding("true_me")); x += 236f;
            AddNav("记忆相册", x, () => _rpg.ShowMemory());
        }

        void AddNav(string label, float x, System.Action onClick)
        {
            var btn = Btn(_root.transform, label, 23, new Color(0.14f, 0.18f, 0.26f, 1f), onClick);
            RpgUi.Place(RpgUi.Rect(btn.gameObject), x, 986f, 220f, 62f);
        }

        public void Refresh()
        {
            if (_root == null || _rpg.Engine == null) return;
            RebuildRealmList();
            RebuildLevelPane();
        }

        void RebuildRealmList()
        {
            RpgUi.ClearChildren(_realmList);
            var progress = _rpg.Engine.Levels.GetAllRealmsProgress();

            int i = 0;
            foreach (var token in progress)
            {
                var realm = (JObject)token;
                var id = (string)realm["realm"];
                var name = (string)realm["name"];
                var icon = (string)realm["icon"];
                var gameType = (string)realm["game_type"];
                int total = RpgUi.GetInt(realm, "total_levels");
                int passed = RpgUi.GetInt(realm, "levels_passed");
                bool completed = RpgUi.GetBool(realm, "completed");

                bool selected = id == _selectedRealm;
                var bg = selected ? new Color(0.20f, 0.26f, 0.38f, 1f) : RpgUi.Card;
                var card = RpgUi.ClickableCard(_realmList, bg, () => { _selectedRealm = id; Refresh(); });
                var root = card.gameObject;
                RpgUi.Place(RpgUi.Rect(root), 0f, i * 130f, 520f, 120f);

                var accentColor = completed ? RpgUi.Green : (passed > 0 ? RpgUi.Amber : RpgUi.Muted);
                var bar = RpgUi.NewUi("Accent", root.transform);
                RpgUi.Place(RpgUi.Rect(bar), 0f, 0f, 8f, 120f);
                bar.AddComponent<Image>().color = accentColor;

                var t1 = Txt(root.transform, $"{icon}  {name}", 28, TextAnchor.MiddleLeft, RpgUi.Ink);
                RpgUi.Stretch(t1.rectTransform, 26f, 14f, 12f, 62f);

                var status = completed ? "已通关" : passed > 0 ? "进行中" : "未开始";
                var t2 = Txt(root.transform, $"{GameTypeName(gameType)} · 进度 {passed}/{total} · {status}",
                    20, TextAnchor.MiddleLeft, RpgUi.Muted);
                RpgUi.Stretch(t2.rectTransform, 26f, 62f, 12f, 14f);
                i++;
            }
        }

        void RebuildLevelPane()
        {
            var realmData = _rpg.Engine.Levels.GetRealmLevels(_selectedRealm);
            var name = realmData == null ? "" : (string)realmData["name"];
            var icon = realmData == null ? "" : (string)realmData["icon"];
            var gameType = realmData == null ? "" : (string)realmData["game_type"];
            var levels = realmData == null ? null : realmData["levels"] as JArray;
            int passed = realmData == null ? 0 : RpgUi.GetInt(realmData, "levels_passed");

            _realmHeader.text = $"{icon}  {name}";
            _realmSub.text = $"{GameTypeName(gameType)} · 已通关 {passed}/{(levels == null ? 0 : levels.Count)}";

            RpgUi.ClearChildren(_levelList);
            if (levels == null) return;

            for (int i = 0; i < levels.Count; i++)
            {
                var lv = (JObject)levels[i];
                int idx = RpgUi.GetInt(lv, "index", i);
                BuildLevelRow(lv, idx, i);
            }
        }

        void BuildLevelRow(JObject lv, int index, int row)
        {
            var name = (string)lv["name"];
            var type = (string)lv["type"];
            var status = (string)lv["status"];
            int difficulty = RpgUi.GetInt(lv, "difficulty", 1);
            int turnLimit = RpgUi.GetInt(lv, "turn_limit", 20);
            var objective = lv["objective"] as JObject ?? new JObject();

            Color accent = status == "completed" ? RpgUi.Green
                : status == "current" ? RpgUi.Amber : RpgUi.Locked;
            string statusText = status == "completed" ? "已通关" : status == "current" ? "进行中" : "未解锁";

            var card = RpgUi.ClickableCard(_levelList, RpgUi.Card, () => _rpg.EnterLevel(_selectedRealm, index));
            var root = card.gameObject;
            RpgUi.Place(RpgUi.Rect(root), 0f, row * 116f, 1200f, 108f);

            var bar = RpgUi.NewUi("Accent", root.transform);
            RpgUi.Place(RpgUi.Rect(bar), 0f, 0f, 8f, 108f);
            bar.AddComponent<Image>().color = accent;

            var nameText = Txt(root.transform, $"{index + 1}. {name}", 27, TextAnchor.MiddleLeft, RpgUi.Ink);
            RpgUi.Place(nameText.rectTransform, 26f, 14f, 800f, 36f);

            var objText = Txt(root.transform, "目标：" + RpgUi.ObjectiveLabel(objective), 21,
                TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(objText.rectTransform, 26f, 52f, 800f, 28f);

            var metaText = Txt(root.transform, $"回合上限 {turnLimit} · 难度 {RpgUi.Stars(difficulty)} · {TypeName(type)}",
                19, TextAnchor.MiddleLeft, RpgUi.Muted);
            RpgUi.Place(metaText.rectTransform, 26f, 80f, 800f, 26f);

            var badge = Txt(root.transform, statusText, 20, TextAnchor.MiddleCenter, accent);
            RpgUi.Place(badge.rectTransform, 846f, 30f, 140f, 48f);

            var enter = Btn(root.transform, "进入 ▶", 22, new Color(0.18f, 0.40f, 0.30f, 1f),
                () => _rpg.EnterLevel(_selectedRealm, index));
            RpgUi.Place(RpgUi.Rect(enter.gameObject), 1000f, 28f, 184f, 52f);
        }

        // ── 文本 / 按钮工具（复用 CheatUiKit 的字体与样式）─────────────────
        Text Txt(Transform parent, string text, int size, TextAnchor anchor, Color color)
            => CheatUiKit.Text(parent, text, size, anchor, color);

        Button Btn(Transform parent, string label, int size, Color bg, System.Action onClick)
            => CheatUiKit.Button(parent, label, size, bg, onClick);

        // ── 展示名映射（仅显示，不参与逻辑）────────────────────────────
        internal static string GameTypeName(string gameType)
        {
            switch (gameType)
            {
                case "xiangqi": return "中国象棋";
                case "dongwuqi": return "斗兽棋";
                case "tiaoqi": return "中国跳棋";
                case "wuziqi": return "五子棋";
                case "weiqi": return "围棋";
                case "heibaiqi": return "黑白棋";
                default: return gameType;
            }
        }

        static string TypeName(string type)
        {
            switch (type)
            {
                case "standard": return "标准";
                case "puzzle": return "残局";
                case "boss": return "守道者";
                case "objective": return "目标关";
                default: return type;
            }
        }
    }
}
