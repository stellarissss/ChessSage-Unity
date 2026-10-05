using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 结局系统（对应 legacy-web/samsara/endings.py 的 EndingSystem）。
    /// 判定优先级：exposed &gt; true_me &gt; enlightenment / corruption / samsara，最后按 alignment 兜底。
    /// </summary>
    public sealed class EndingSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _story;

        public EndingSystem(SamsaraState state, string storyPath)
        {
            _state = state;
            _story = SamsaraJson.LoadObject(storyPath) ?? new JObject();
        }

        private JObject EndingsSection()
        {
            return _story["endings"] as JObject ?? new JObject();
        }

        /// <summary>
        /// 获取结局剧情数据，注入 runtime 字段 prayer_count 并替换对白中的 {prayer_count} 占位符。
        /// </summary>
        public JObject GetEndingData(string endingId)
        {
            var data = EndingsSection()[endingId] as JObject;
            if (data == null || data.Count == 0) return new JObject();

            var result = SamsaraJson.Clone(data);
            var prayerCount = _state.GetPrayerCount();
            result["prayer_count"] = prayerCount;

            var placeholders = new Dictionary<string, string> { { "prayer_count", prayerCount.ToString() } };
            foreach (var key in new[] { "dialogues_before", "dialogues", "epilogue_dialogues" })
            {
                var dialogues = result[key] as JArray;
                if (dialogues == null) continue;
                foreach (var item in dialogues)
                {
                    var d = item as JObject;
                    if (d == null) continue;
                    var text = d["text"];
                    if (text == null || text.Type != JTokenType.String) continue;
                    d["text"] = SamsaraJson.SubstitutePlaceholders(text.Value<string>(), placeholders);
                }
            }
            return result;
        }

        /// <summary>返回所有结局的解锁状态。</summary>
        public JObject GetAllEndingsStatus()
        {
            var unlocked = _state.GetEndingsUnlocked();
            var result = new JObject();
            foreach (var prop in unlocked.Properties())
            {
                var data = GetEndingData(prop.Name);
                result[prop.Name] = new JObject
                {
                    ["unlocked"] = SamsaraJson.GetBool(unlocked, prop.Name, false),
                    ["name"] = SamsaraJson.GetString(data, "name", prop.Name),
                    ["type"] = SamsaraJson.GetString(data, "type", ""),
                };
            }
            return result;
        }

        /// <summary>识破结局：使用过祈求 + 天道Boss战胜利。</summary>
        public bool CheckExposedEnding()
        {
            var detectionState = _state.GetDetectionState();
            var boss = _state.GetTiandaoBossState();
            return _state.GetPrayerCount() >= 1
                && SamsaraJson.GetBool(detectionState, "exposure_path_triggered", false)
                && SamsaraJson.GetBool(boss, "defeated", false);
        }

        /// <summary>真我结局：全记忆碎片 + 悟道线最终选择 + 无祈祷 + 悟道值 ≥ 9。</summary>
        public bool CheckTrueMeEnding(string finalChoice = null)
        {
            if (_state.GetPrayerCount() > 0) return false;
            if (!_state.AllMemoryFragmentsCollected()) return false;
            if (!string.IsNullOrEmpty(finalChoice) && finalChoice != "enlightenment") return false;
            var align = _state.GetAlignment();
            if (SamsaraJson.GetInt(align, "enlightenment", 0) < 9) return false;
            return true;
        }

        /// <summary>悟道结局：悟道值 - 堕落值 ≥ 3 + 选悟道 + 业力 &lt; 100 + 无祈祷。</summary>
        public bool CheckEnlightenmentEnding(string finalChoice = null)
        {
            if (_state.GetPrayerCount() > 0) return false;
            if (!string.IsNullOrEmpty(finalChoice) && finalChoice != "enlightenment") return false;
            var align = _state.GetAlignment();
            var e = SamsaraJson.GetInt(align, "enlightenment", 0);
            var c = SamsaraJson.GetInt(align, "corruption", 0);
            if (e - c < 3) return false;
            if (_state.GetKarma() >= 100) return false;
            return true;
        }

        /// <summary>堕落结局：堕落值 - 悟道值 ≥ 3 + 选堕落 + 业力 &gt; 200 + 无祈祷。</summary>
        public bool CheckCorruptionEnding(string finalChoice = null)
        {
            if (_state.GetPrayerCount() > 0) return false;
            if (!string.IsNullOrEmpty(finalChoice) && finalChoice != "corruption") return false;
            var align = _state.GetAlignment();
            var e = SamsaraJson.GetInt(align, "enlightenment", 0);
            var c = SamsaraJson.GetInt(align, "corruption", 0);
            if (c - e < 3) return false;
            if (_state.GetKarma() <= 200) return false;
            return true;
        }

        /// <summary>轮回结局：|悟道 - 堕落| &lt; 3 + 选轮回 + 100 ≤ 业力 ≤ 200 + 无祈祷。</summary>
        public bool CheckSamsaraEnding(string finalChoice = null)
        {
            if (_state.GetPrayerCount() > 0) return false;
            if (!string.IsNullOrEmpty(finalChoice) && finalChoice != "samsara") return false;
            var align = _state.GetAlignment();
            var e = SamsaraJson.GetInt(align, "enlightenment", 0);
            var c = SamsaraJson.GetInt(align, "corruption", 0);
            if (Math.Abs(e - c) >= 3) return false;
            var karma = _state.GetKarma();
            if (karma < 100 || karma > 200) return false;
            return true;
        }

        /// <summary>综合判定结局，返回 {ending_id, ending_data, reason}。</summary>
        public JObject DetermineEnding(string finalChoice = null, bool noCheatFinal = true)
        {
            // 优先级 1：识破结局。
            if (CheckExposedEnding())
            {
                _state.UnlockEnding("exposed");
                return EndingResult("exposed", "使用真心祈求 + 天道Boss战胜利 → 识破结局");
            }

            // 优先级 2：真我结局。
            if (CheckTrueMeEnding(finalChoice) && noCheatFinal)
            {
                _state.UnlockEnding("true_me");
                return EndingResult("true_me", "全记忆碎片 + 悟道线 + 无作弊 + 无祈求 → 真我结局");
            }

            // 优先级 3：悟道结局。
            if (CheckEnlightenmentEnding(finalChoice))
            {
                _state.UnlockEnding("enlightenment");
                return EndingResult("enlightenment", "悟道线 + 悟道值领先 + 业力低 → 悟道结局");
            }

            // 优先级 4：堕落结局。
            if (CheckCorruptionEnding(finalChoice))
            {
                _state.UnlockEnding("corruption");
                return EndingResult("corruption", "堕落线 + 堕落值领先 + 业力高 → 堕落结局");
            }

            // 优先级 5：轮回结局。
            if (CheckSamsaraEnding(finalChoice))
            {
                _state.UnlockEnding("samsara");
                return EndingResult("samsara", "轮回选择 + alignment 接近 + 业力中 → 轮回结局");
            }

            // 兜底：根据 alignment 强制判定（无最终选择时）。
            var align = _state.GetAlignment();
            var e = SamsaraJson.GetInt(align, "enlightenment", 0);
            var c = SamsaraJson.GetInt(align, "corruption", 0);

            if (_state.GetPrayerCount() > 0)
            {
                return new JObject
                {
                    ["ending_id"] = JValue.CreateNull(),
                    ["ending_data"] = JValue.CreateNull(),
                    ["reason"] = "已使用祈求但未完成天道Boss战，无法判定结局",
                };
            }

            if (e > c)
            {
                _state.UnlockEnding("enlightenment");
                return EndingResult("enlightenment", "兜底：悟道值高于堕落值 → 悟道结局");
            }
            if (c > e)
            {
                _state.UnlockEnding("corruption");
                return EndingResult("corruption", "兜底：堕落值高于悟道值 → 堕落结局");
            }
            _state.UnlockEnding("samsara");
            return EndingResult("samsara", "兜底：alignment 均衡 → 轮回结局");
        }

        private JObject EndingResult(string endingId, string reason)
        {
            return new JObject
            {
                ["ending_id"] = endingId,
                ["ending_data"] = GetEndingData(endingId),
                ["reason"] = reason,
            };
        }

        /// <summary>是否应触发天道Boss战（六道通关 + 祈求 ≥ 1）。</summary>
        public bool ShouldTriggerTiandaoBoss()
        {
            if (!_state.AllRealmsCompleted()) return false;
            return _state.GetPrayerCount() >= 1;
        }

        /// <summary>返回当前状态下的结局预览（不锁定）。</summary>
        public JObject GetEndingPreview()
        {
            var align = _state.GetAlignment();
            var prayerCount = _state.GetPrayerCount();
            var allRealms = _state.AllRealmsCompleted();
            var allFrags = _state.AllMemoryFragmentsCollected();

            Func<string, string> endingName = eid => SamsaraJson.GetString(EndingsSection()[eid] as JObject, "name", eid);

            if (prayerCount > 0)
            {
                if (allRealms)
                {
                    return new JObject
                    {
                        ["predicted"] = "exposed",
                        ["name"] = endingName("exposed"),
                        ["condition"] = "六道通关 + 使用过祈求 → 天道Boss战 → 识破结局",
                    };
                }
                return new JObject
                {
                    ["predicted"] = "exposed_pending",
                    ["name"] = "识破路径（待通关）",
                    ["condition"] = "已祈求 " + prayerCount + " 次，通关六道后触发天道Boss战",
                };
            }

            if (allFrags && SamsaraJson.GetInt(align, "enlightenment", 0) >= 9)
            {
                return new JObject
                {
                    ["predicted"] = "true_me",
                    ["name"] = endingName("true_me"),
                    ["condition"] = "全记忆碎片 + 悟道值≥9 + 无祈求",
                };
            }

            var e = SamsaraJson.GetInt(align, "enlightenment", 0);
            var c = SamsaraJson.GetInt(align, "corruption", 0);
            if (e - c >= 3)
            {
                return new JObject
                {
                    ["predicted"] = "enlightenment",
                    ["name"] = endingName("enlightenment"),
                    ["condition"] = "悟道值(" + e + ") - 堕落值(" + c + ") ≥ 3 + 无祈求",
                };
            }
            if (c - e >= 3)
            {
                return new JObject
                {
                    ["predicted"] = "corruption",
                    ["name"] = endingName("corruption"),
                    ["condition"] = "堕落值(" + c + ") - 悟道值(" + e + ") ≥ 3 + 无祈求",
                };
            }
            return new JObject
            {
                ["predicted"] = "samsara",
                ["name"] = endingName("samsara"),
                ["condition"] = "悟道值(" + e + ") ≈ 堕落值(" + c + ") + 无祈求",
            };
        }
    }
}