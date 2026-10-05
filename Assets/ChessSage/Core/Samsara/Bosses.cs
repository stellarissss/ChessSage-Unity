using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 守道者（Boss）系统（对应 legacy-web/samsara/bosses.py 的 BossSystem）。
    /// 机械字段来自 boss_definitions.json，守道者名字以 story.json 为权威源覆盖。
    /// </summary>
    public sealed class BossSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _bosses;

        public BossSystem(SamsaraState state, string bossDefinitionsPath, string storyPath)
        {
            _state = state;
            _bosses = SamsaraJson.LoadObject(bossDefinitionsPath) ?? DefaultBosses();
            OverlayStoryNames(storyPath);
        }

        private static JObject DefaultBosses()
        {
            return new JObject
            {
                ["hell"] = new JObject
                {
                    ["id"] = "flipper",
                    ["name"] = "翻覆者",
                    ["title"] = "地狱道守道者",
                    ["skill"] = "flip",
                    ["skill_description"] = "修改规则有30%概率被翻转效果",
                    ["ai_personality"] = "aggressive",
                    ["icon"] = "☯",
                },
                ["hungry"] = new JObject
                {
                    ["id"] = "glutton",
                    ["name"] = "饕餮者",
                    ["title"] = "饿鬼道守道者",
                    ["skill"] = "take_more",
                    ["skill_description"] = "每作弊2次，额外偷改1次",
                    ["ai_personality"] = "aggressive",
                    ["icon"] = "👹",
                },
                ["animal"] = new JObject
                {
                    ["id"] = "orderer",
                    ["name"] = "秩序者",
                    ["title"] = "畜生道守道者",
                    ["skill"] = "rank_lock",
                    ["skill_description"] = "高等级棋子修改业力消耗额外+20点",
                    ["ai_personality"] = "defensive",
                    ["icon"] = "🐅",
                },
                ["human"] = new JObject
                {
                    ["id"] = "calculator",
                    ["name"] = "算计者",
                    ["title"] = "人道守道者",
                    ["skill"] = JValue.CreateNull(),
                    ["skill_description"] = "无特殊技能，最公平的对决",
                    ["ai_personality"] = "normal",
                    ["icon"] = "🧠",
                },
                ["asura"] = new JObject
                {
                    ["id"] = "chaos",
                    ["name"] = "狂乱者",
                    ["title"] = "阿修罗道守道者",
                    ["skill"] = "chaos",
                    ["skill_description"] = "作弊后25%概率随机规则变化",
                    ["ai_personality"] = "aggressive_random",
                    ["icon"] = "⚔️",
                },
                ["heaven"] = new JObject
                {
                    ["id"] = "zen",
                    ["name"] = "禅定者",
                    ["title"] = "天道守道者",
                    ["skill"] = "purify",
                    ["skill_description"] = "每局3次，直接清除最近1条修改",
                    ["ai_personality"] = "defensive_hard",
                    ["icon"] = "☸️",
                    ["skill_uses"] = 3,
                },
            };
        }

        /// <summary>从 story.json.realms.{realm}.guardian.name 覆盖守道者名。</summary>
        private void OverlayStoryNames(string storyPath)
        {
            var story = SamsaraJson.LoadObject(storyPath);
            var realms = story == null ? null : story["realms"] as JObject;
            if (realms == null) return;
            foreach (var prop in _bosses.Properties())
            {
                var boss = prop.Value as JObject;
                var realmData = realms[prop.Name] as JObject;
                var guardian = realmData == null ? null : realmData["guardian"] as JObject;
                var guardianName = SamsaraJson.GetString(guardian, "name", null);
                if (!string.IsNullOrEmpty(guardianName)) boss["name"] = guardianName;
            }
        }

        public JObject GetCurrentBoss()
        {
            var realm = _state.GetString("current_realm", "hell");
            return _bosses[realm] as JObject;
        }

        /// <summary>按当前道的守道者技能做触发判定，返回 {triggered, skill, effect?}。</summary>
        public JObject TriggerBossSkill(int cheatCount)
        {
            var realm = _state.GetString("current_realm", "hell");
            var boss = _bosses[realm] as JObject;
            if (boss == null || boss["skill"] == null || boss["skill"].Type == JTokenType.Null)
            {
                return new JObject { ["triggered"] = false };
            }

            var modifiers = _state.GetSkillModifiers();
            var reduction = SamsaraJson.GetBool(modifiers, "boss_skill_reduction", false) ? 0.3 : 0.0;
            var skill = SamsaraJson.GetString(boss, "skill", "");

            var result = new JObject
            {
                ["triggered"] = false,
                ["skill"] = skill,
            };

            switch (skill)
            {
                case "flip":
                    if (SamsaraRandom.Chance(0.3 - reduction))
                    {
                        result["triggered"] = true;
                        result["effect"] = "规则效果被翻转";
                    }
                    break;
                case "take_more":
                    if (cheatCount > 0 && cheatCount % 2 == 0)
                    {
                        result["triggered"] = true;
                        result["effect"] = "Boss额外偷改了一次";
                    }
                    break;
                case "rank_lock":
                    result["triggered"] = true;
                    result["effect"] = "高等级棋子修改费用+20";
                    break;
                case "chaos":
                    if (SamsaraRandom.Chance(0.25 - reduction))
                    {
                        result["triggered"] = true;
                        result["effect"] = "随机规则变化";
                    }
                    break;
                case "purify":
                    var uses = SamsaraJson.GetInt(boss, "skill_uses", 3);
                    if (uses > 0)
                    {
                        boss["skill_uses"] = uses - 1;
                        result["triggered"] = true;
                        result["effect"] = "清除了最近1条修改";
                    }
                    break;
            }
            return result;
        }

        /// <summary>重置所有守道者技能次数（关卡开始时调用）。</summary>
        public void ResetBossSkills()
        {
            foreach (var prop in _bosses.Properties())
            {
                var boss = prop.Value as JObject;
                if (boss != null && boss["skill_uses"] != null) boss["skill_uses"] = 3;
            }
        }
    }
}