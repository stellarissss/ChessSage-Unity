using ChessSage.Core.Samsara;
using ChessSage.Presentation.Cheat;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ChessSage.Presentation.Samsara
{
    /// <summary>
    /// 记忆相册（P1）：把 <see cref="MemoryFragmentSystem"/> 的 6 道记忆碎片
    /// 以卡片网格呈现——已解锁显示碎片标题 + 旁白摘要；未解锁显示解锁条件。
    ///
    /// 碎片真源为 story.json 各道的 <c>memory_fragment</c> 段（标题/旁白/解锁条件），
    /// 解锁状态为 <c>memory_fragments_unlocked</c>。界面不自创文案。
    /// </summary>
    internal sealed class MemoryAlbumPanel
    {
        readonly SamsaraRpg _rpg;
        GameObject _root;
        Transform _grid;
        Text _sub;

        public GameObject Root => _root;

        public MemoryAlbumPanel(SamsaraRpg rpg) { _rpg = rpg; }

        public GameObject Build(Transform parent)
        {
            _root = RpgUi.Backdrop("MemoryAlbum", parent);

            var title = Txt("记忆相册 · 六道回声", 40, TextAnchor.MiddleCenter, RpgUi.Gold);
            RpgUi.Place(title.rectTransform, 0f, 24f, 1920f, 54f);

            _sub = Txt("", 22, TextAnchor.MiddleCenter, RpgUi.Muted);
            RpgUi.Place(_sub.rectTransform, 0f, 88f, 1920f, 30f);

            var grid = RpgUi.NewUi("Grid", _root.transform);
            RpgUi.Place(RpgUi.Rect(grid), 90f, 150f, 1740f, 700f);
            _grid = grid.transform;

            var back = Btn(_root.transform, "← 返回", 24, new Color(0.20f, 0.22f, 0.28f, 1f), _rpg.Back);
            RpgUi.Place(RpgUi.Rect(back.gameObject), 60f, 986f, 200f, 62f);

            _root.SetActive(false);
            return _root;
        }

        public void Refresh()
        {
            if (_root == null || _rpg.Engine == null) return;
            RpgUi.ClearChildren(_grid);

            var frags = _rpg.Engine.GetMemoryFragments();
            int unlocked = 0;
            for (int i = 0; i < frags.Count; i++)
            {
                var frag = (JObject)frags[i];
                bool isUnlocked = RpgUi.GetBool(frag, "unlocked", false);
                if (isUnlocked) unlocked++;
                BuildCard(frag, i, isUnlocked);
            }
            _sub.text = $"已收集 {unlocked}/{frags.Count} · 每道「全程无作弊通关」可解锁一段记忆";
        }

        void BuildCard(JObject frag, int index, bool unlocked)
        {
            var realm = RpgUi.GetStr(frag, "realm", "");
            var title = RpgUi.GetStr(frag, "title", "（未知记忆）");
            var realmName = SamsaraConstants.RealmName(realm);

            int col = index % 3;
            int row = index / 3;
            float x = col * 580f;
            float y = row * 350f;

            var card = RpgUi.NewUi("Frag", _grid);
            RpgUi.Place(RpgUi.Rect(card), x, y, 550f, 330f);
            var img = card.AddComponent<Image>();
            img.color = unlocked ? RpgUi.Card : RpgUi.CardLocked;

            var accent = RpgUi.NewUi("Accent", card.transform);
            RpgUi.Place(RpgUi.Rect(accent), 0f, 0f, 8f, 330f);
            accent.AddComponent<Image>().color = unlocked ? RpgUi.Violet : RpgUi.Locked;

            var head = CheatUiKit.Text(card.transform, $"{realmName} · 记忆碎片", 22, TextAnchor.MiddleLeft,
                unlocked ? RpgUi.Violet : RpgUi.Muted);
            RpgUi.Place(head.rectTransform, 26f, 16f, 500f, 30f);

            var name = CheatUiKit.Text(card.transform, unlocked ? title : "？？？", 28, TextAnchor.MiddleLeft,
                unlocked ? RpgUi.Ink : RpgUi.Locked);
            RpgUi.Place(name.rectTransform, 26f, 50f, 500f, 40f);

            string body;
            if (unlocked)
            {
                var data = _rpg.Engine.MemoryFragments.GetFragmentData(realm);
                var narration = RpgUi.GetStr(data, "narration", "");
                body = string.IsNullOrEmpty(narration) ? title : narration;
            }
            else
            {
                body = "解锁条件：全程无作弊通关「" + realmName + "」。\n（记忆碎片会在结局判定中成为关键线索。）";
            }

            var bodyText = CheatUiKit.Text(card.transform, body, 19, TextAnchor.UpperLeft,
                unlocked ? new Color(0.82f, 0.86f, 0.92f, 1f) : RpgUi.Muted);
            RpgUi.Place(bodyText.rectTransform, 26f, 100f, 500f, 210f);
        }

        Text Txt(string text, int size, TextAnchor anchor, Color color)
            => CheatUiKit.Text(_root.transform, text, size, anchor, color);

        Button Btn(Transform parent, string label, int size, Color bg, System.Action onClick)
            => CheatUiKit.Button(parent, label, size, bg, onClick);
    }
}
