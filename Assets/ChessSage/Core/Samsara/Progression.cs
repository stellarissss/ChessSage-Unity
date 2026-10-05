using System;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 进度与结算系统（对应 legacy-web/samsara/progression.py 的 ProgressionSystem）。
    /// 负责关卡胜负结算、技能点奖励与道内/道间推进。
    /// </summary>
    public sealed class ProgressionSystem
    {
        private readonly SamsaraState _state;

        public ProgressionSystem(SamsaraState state)
        {
            _state = state;
        }

        /// <summary>结算一局关卡（胜负均计算溢出叠加）。</summary>
        public JObject ResolveLevel(bool won, bool noCheat, bool bossDefeated = false)
        {
            var rewards = new JObject
            {
                ["skill_points"] = 0,
                ["bonus_reasons"] = new JArray(),
            };
            var reasons = (JArray)rewards["bonus_reasons"];

            // 关卡结束（无论胜负）：计算本局业力溢出，叠加到本道下一局。
            var overshoot = _state.RecordLevelEnd();
            rewards["overshoot_carryover"] = overshoot;

            if (!won) return rewards;

            if (_state.IsSandboxMode())
            {
                rewards["skill_points"] = 1;
                reasons.Add("沙盒模式胜利");
                _state.AddSkillPoint(1);
                return rewards;
            }

            var skillPoints = 1;
            reasons.Add("基础通关奖励");
            if (noCheat)
            {
                skillPoints += 2;
                reasons.Add("无AI通关奖励");
            }
            if (!(_state.GetInt("overdraft_count", 0) > 0))
            {
                skillPoints += 1;
                reasons.Add("未透支奖励");
            }

            var currentRealm = _state.GetString("current_realm", "hell");
            if (bossDefeated && !_state.IsBossDefeated("boss_" + currentRealm))
            {
                skillPoints += 1;
                reasons.Add("首次击败Boss");
                _state.MarkBossDefeated("boss_" + currentRealm);
            }

            rewards["skill_points"] = skillPoints;
            _state.AddSkillPoint(skillPoints);
            _state.Set("total_levels_completed", _state.GetInt("total_levels_completed", 0) + 1);

            _state.IncrementRealmLevelsPassed(currentRealm);
            var progressRoot = _state.Get("realm_progress") as JObject;
            var progress = progressRoot == null ? null : progressRoot[currentRealm] as JObject;
            var levelsPassed = SamsaraJson.GetInt(progress, "levels_passed", 0);
            var totalLevels = GetRealmLevelCount(currentRealm);
            if (levelsPassed >= totalLevels)
            {
                _state.MarkRealmCompleted(currentRealm);
                _state.UnlockSandbox(currentRealm);
                rewards["sandbox_unlocked"] = true;
            }
            return rewards;
        }

        /// <summary>道内完成后推进道（无透支跳 2 道，否则跳 1 道；末道则超脱）。</summary>
        public JObject AdvanceRealm()
        {
            var currentRealm = _state.GetString("current_realm", "hell");
            var currentIndex = _state.GetRealmIndex();
            var progressRoot = _state.Get("realm_progress") as JObject;
            var progress = progressRoot == null ? null : progressRoot[currentRealm] as JObject;
            var levelsPassed = SamsaraJson.GetInt(progress, "levels_passed", 0);
            var totalLevels = GetRealmLevelCount(currentRealm);

            var result = new JObject
            {
                ["advanced"] = false,
                ["new_realm"] = JValue.CreateNull(),
                ["direction"] = JValue.CreateNull(),
            };

            if (levelsPassed >= totalLevels)
            {
                _state.MarkRealmCompleted(currentRealm);
                if (currentIndex < SamsaraConstants.Realms.Length - 1)
                {
                    var overdraftCount = _state.GetInt("overdraft_count", 0);
                    int newIndex;
                    if (overdraftCount == 0)
                    {
                        newIndex = Math.Min(currentIndex + 2, SamsaraConstants.Realms.Length - 1);
                        result["direction"] = "up_2";
                    }
                    else
                    {
                        newIndex = currentIndex + 1;
                        result["direction"] = "up_1";
                    }
                    var newRealm = SamsaraConstants.Realms[newIndex];
                    _state.SetRealm(newRealm);
                    _state.ResetLevelState();
                    result["advanced"] = true;
                    result["new_realm"] = newRealm;

                    var modifiers = _state.GetSkillModifiers();
                    if (SamsaraJson.GetBool(modifiers, "free_cheat_on_realm_change", false))
                        result["free_cheat"] = true;
                }
                else
                {
                    result["advanced"] = true;
                    result["new_realm"] = "heaven";
                    result["direction"] = "transcend";
                }
            }
            return result;
        }

        /// <summary>倒退一道（已在第一道时不变化）。</summary>
        public JObject RetreatRealm()
        {
            var currentIndex = _state.GetRealmIndex();
            if (currentIndex > 0)
            {
                var newRealm = SamsaraConstants.Realms[currentIndex - 1];
                _state.SetRealm(newRealm);
                _state.ResetLevelState();
                return new JObject
                {
                    ["retreated"] = true,
                    ["new_realm"] = newRealm,
                };
            }
            return new JObject
            {
                ["retreated"] = false,
                ["new_realm"] = JValue.CreateNull(),
            };
        }

        private static int GetRealmLevelCount(string realm)
        {
            switch (realm)
            {
                case "hell": return 5;
                case "hungry": return 5;
                case "animal": return 5;
                case "human": return 6;
                case "asura": return 6;
                case "heaven": return 6;
                default: return 5;
            }
        }
    }
}