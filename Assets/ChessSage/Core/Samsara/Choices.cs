using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 选择系统（对应 legacy-web/samsara/choices.py 的 ChoiceSystem）。
    /// 处理剧情中的玩家选择：应用 alignment / karma 效果、记录选择历史、返回后续对白。
    /// 剧情真源为 story.json。
    /// </summary>
    public sealed class ChoiceSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _story;

        public ChoiceSystem(SamsaraState state, string storyPath)
        {
            _state = state;
            _story = SamsaraJson.LoadObject(storyPath) ?? new JObject();
        }

        /// <summary>获取某道的剧情数据（不存在返回空对象）。</summary>
        public JObject GetRealm(string realm)
        {
            var realms = _story["realms"] as JObject;
            var realmData = realms == null ? null : realms[realm] as JObject;
            return realmData ?? new JObject();
        }

        /// <summary>获取某关卡的剧情数据（不存在返回空对象）。</summary>
        public JObject GetLevel(string realm, string level)
        {
            var levels = GetRealm(realm)["levels"] as JObject;
            var levelData = levels == null ? null : levels[level] as JObject;
            return levelData ?? new JObject();
        }

        /// <summary>获取某关卡的选择面板列表。</summary>
        public JArray GetChoicesForLevel(string realm, string level)
        {
            var choices = GetLevel(realm, level)["choices"] as JArray;
            return choices ?? new JArray();
        }

        /// <summary>
        /// 应用玩家选择，返回 {success, effect_applied, response, ending, is_final, option_text, hint}。
        /// 失败时返回 {success:false, message}。
        /// </summary>
        public JObject ApplyChoice(string realm, string level, int choiceIndex, int optionIndex)
        {
            var choices = GetChoicesForLevel(realm, level);
            if (choiceIndex >= choices.Count)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["message"] = "选择面板不存在",
                };
            }

            var choicePanel = choices[choiceIndex] as JObject;
            var options = choicePanel == null ? null : choicePanel["options"] as JArray;
            if (options == null || optionIndex >= options.Count)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["message"] = "选项不存在",
                };
            }

            var option = options[optionIndex] as JObject ?? new JObject();
            var effect = option["effect"] as JObject ?? new JObject();

            // 应用 alignment 效果。
            if (effect.Count > 0) _state.AddAlignment(effect);

            // 记录选择。
            _state.RecordChoice(new JObject
            {
                ["realm"] = realm,
                ["level"] = level,
                ["choice_index"] = choiceIndex,
                ["option_index"] = optionIndex,
                ["option_text"] = SamsaraJson.GetString(option, "text", ""),
                ["hint"] = SamsaraJson.GetString(option, "hint", ""),
                ["effect"] = effect.DeepClone(),
            });

            // 更新 story_progress。
            _state.UpdateStoryProgress(new JObject
            {
                ["last_choice_made"] = new JObject
                {
                    ["realm"] = realm,
                    ["level"] = level,
                    ["choice_index"] = choiceIndex,
                    ["option_index"] = optionIndex,
                },
            });

            return new JObject
            {
                ["success"] = true,
                ["effect_applied"] = effect.DeepClone(),
                ["response"] = option["response"] == null ? JValue.CreateNull() : option["response"].DeepClone(),
                ["ending"] = option["ending"] == null ? JValue.CreateNull() : option["ending"].DeepClone(),
                ["is_final"] = SamsaraJson.GetBool(choicePanel, "is_final", false),
                ["option_text"] = SamsaraJson.GetString(option, "text", ""),
                ["hint"] = SamsaraJson.GetString(option, "hint", ""),
            };
        }

        /// <summary>某选择面板是否应跳过（识破路径下跳过最终选择）。</summary>
        public bool ShouldSkipChoice(string realm, string level, int choiceIndex)
        {
            var choices = GetChoicesForLevel(realm, level);
            if (choiceIndex >= choices.Count) return false;
            var choicePanel = choices[choiceIndex] as JObject;
            if (SamsaraJson.GetBool(choicePanel, "skip_if_exposure_path", false))
            {
                return _state.IsExposurePathTriggered();
            }
            return false;
        }

        /// <summary>返回当前 alignment 摘要（含主导项 dominant）。</summary>
        public JObject GetAlignmentSummary()
        {
            var align = _state.GetAlignment();
            return new JObject
            {
                ["enlightenment"] = SamsaraJson.GetInt(align, "enlightenment", 0),
                ["corruption"] = SamsaraJson.GetInt(align, "corruption", 0),
                ["rationality"] = SamsaraJson.GetInt(align, "rationality", 0),
                ["emotion"] = SamsaraJson.GetInt(align, "emotion", 0),
                ["dominant"] = GetDominantAlignment(align),
            };
        }

        /// <summary>返回主导 alignment：enlightenment / corruption / neutral。</summary>
        private static string GetDominantAlignment(JObject align)
        {
            var enlightenment = SamsaraJson.GetInt(align, "enlightenment", 0);
            var corruption = SamsaraJson.GetInt(align, "corruption", 0);
            if (enlightenment > corruption) return "enlightenment";
            if (corruption > enlightenment) return "corruption";
            return "neutral";
        }
    }
}