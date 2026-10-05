using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 识破系统（对应 legacy-web/samsara/detection.py 的 DetectionSystem）。
    /// 识破概率随业障溢出按 Δ = C × O^α 累积；每次结算用 random()*100 &lt; detection 判定。
    /// 判词文案从 story.json.detection 读取，缺省时使用内置默认。
    /// </summary>
    public sealed class DetectionModel
    {
        private const string DefaultJudgmentText = "天道识破 · 你不是渴求胜利的战士，是作弊成性的怪物。";
        private const string DefaultResetMessage = "天道识破 · 妄改天规者，罚入轮回";

        private readonly SamsaraState _state;
        private readonly JObject _story;

        public DetectionModel(SamsaraState state, string storyPath)
        {
            _state = state;
            _story = SamsaraJson.LoadObject(storyPath) ?? new JObject();
        }

        private JObject DetectionSection()
        {
            return _story["detection"] as JObject;
        }

        /// <summary>识破命中时的判词（story.json.detection.judgment_text）。</summary>
        public string GetJudgmentText()
        {
            return SamsaraJson.GetString(DetectionSection(), "judgment_text", DefaultJudgmentText);
        }

        /// <summary>/api/detection/reset 返回的判词（story.json.detection.reset_message）。</summary>
        public string GetResetMessage()
        {
            return SamsaraJson.GetString(DetectionSection(), "reset_message", DefaultResetMessage);
        }

        // ── 概率累积（业障溢出 → 识破概率上升） ──

        /// <summary>根据业障溢出量计算识破概率增量。</summary>
        public double CalculateDelta(double overdraftAmount)
        {
            if (overdraftAmount <= 0) return 0.0;
            var modifiers = _state.GetSkillModifiers();
            var c = SamsaraJson.GetDouble(modifiers, "detection_coefficient", 0.1);
            var alpha = SamsaraJson.GetDouble(modifiers, "detection_alpha", 1.5);
            var delta = c * System.Math.Pow(overdraftAmount, alpha);
            // 雾隐技能：高识破时有概率不累积。
            if (SamsaraJson.GetBool(modifiers, "mist_fog", false) && _state.GetDetection() > 70)
            {
                if (SamsaraRandom.Chance(0.3)) return 0.0;
            }
            return delta;
        }

        // ── 概率结算 ──

        /// <summary>核心概率判定：random*100 &lt; detection 即命中；锁死状态下永返 false。</summary>
        public bool Check(double currentDetection)
        {
            if (_state.IsDetectionLocked()) return false;
            if (currentDetection <= 0) return false;
            var roll = SamsaraRandom.NextDouble() * 100;
            return roll < currentDetection;
        }

        /// <summary>每次使用 AI 修改（真心祈求）后调用，返回判定结果详情。</summary>
        public JObject CheckOnPrayer()
        {
            var detectionState = _state.GetDetectionState();

            if (SamsaraJson.GetBool(detectionState, "is_detected", false))
            {
                return new JObject
                {
                    ["detected"] = false,
                    ["already_detected"] = true,
                    ["current"] = 0.0,
                    ["prayer_count"] = _state.GetPrayerCount(),
                };
            }

            var current = _state.GetDetection();
            var detected = Check(current);

            if (detected)
            {
                _state.ResetOnDetection();
                return new JObject
                {
                    ["detected"] = true,
                    ["current"] = 0.0,
                    ["locked"] = true,
                    ["message"] = GetJudgmentText(),
                    ["prayer_count"] = _state.GetPrayerCount(),
                    ["trigger_boss_on_complete"] = true,
                };
            }

            return new JObject
            {
                ["detected"] = false,
                ["current"] = current,
                ["locked"] = false,
                ["prayer_count"] = _state.GetPrayerCount(),
            };
        }

        /// <summary>
        /// 业障溢出时累积识破概率；不在溢出时直接判定识破，改为累积。
        /// 保留概率即时判定入口以兼容旧调用链。
        /// </summary>
        public JObject HandleOverdraft(double overdraftAmount)
        {
            if (overdraftAmount <= 0)
            {
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = _state.GetDetection(),
                };
            }

            if (_state.IsDetectionLocked())
            {
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = 0.0,
                    ["locked"] = true,
                };
            }

            var modifiers = _state.GetSkillModifiers();

            // 首次溢出豁免（一次性技能 stealth_t2a）。
            if (SamsaraJson.GetBool(modifiers, "first_overdraft_skip", false))
            {
                _state.ConsumeOneTimeSkill("stealth_t2a", "overdraft_skip");
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = _state.GetDetection(),
                    ["skip"] = true,
                };
            }

            var delta = CalculateDelta(overdraftAmount);
            if (delta <= 0)
            {
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = _state.GetDetection(),
                };
            }

            _state.IncrementDetection(delta);
            _state.RecordOverdraft();
            var current = _state.GetDetection();

            var detected = Check(current);

            if (detected)
            {
                // 金蝉脱壳技能：首次被识破可减半概率逃过（一次性技能 stealth_t3a）。
                if (SamsaraJson.GetBool(modifiers, "golden_escape", false))
                {
                    _state.ConsumeOneTimeSkill("stealth_t3a", "golden_escape");
                    _state.SetDetection(current * 0.5);
                    return new JObject
                    {
                        ["detected"] = false,
                        ["delta"] = delta,
                        ["current"] = current * 0.5,
                        ["escaped"] = true,
                    };
                }
                _state.ResetOnDetection();
                return new JObject
                {
                    ["detected"] = true,
                    ["delta"] = delta,
                    ["current"] = 0.0,
                    ["locked"] = true,
                    ["message"] = GetJudgmentText(),
                    ["trigger_boss_on_complete"] = true,
                };
            }

            return new JObject
            {
                ["detected"] = false,
                ["delta"] = delta,
                ["current"] = current,
            };
        }

        /// <summary>返回当前识破系统完整状态（供前端展示）。</summary>
        public JObject GetStatus()
        {
            var detectionState = _state.GetDetectionState();
            return new JObject
            {
                ["detection"] = _state.GetDetection(),
                ["is_detected"] = SamsaraJson.GetBool(detectionState, "is_detected", false),
                ["detection_locked"] = SamsaraJson.GetBool(detectionState, "detection_locked", false),
                ["trigger_boss_on_complete"] = SamsaraJson.GetBool(detectionState, "trigger_boss_on_complete", false),
                ["exposure_path_triggered"] = SamsaraJson.GetBool(detectionState, "exposure_path_triggered", false),
                ["prayer_count"] = _state.GetPrayerCount(),
            };
        }
    }
}