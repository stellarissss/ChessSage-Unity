using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 技能树系统（对应 legacy-web/samsara/skills.py 的 SkillSystem）。
    /// 技能树真源为 skill_tree.json，缺失时回退内置默认树。
    /// </summary>
    public sealed class SkillSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _skillTree;

        public SkillSystem(SamsaraState state, string skillTreePath)
        {
            _state = state;
            _skillTree = SamsaraJson.LoadObject(skillTreePath) ?? DefaultSkillTree();
        }

        private static JObject TierLeaf(string id, string name, string description, int cost)
        {
            return new JObject
            {
                ["id"] = id,
                ["name"] = name,
                ["description"] = description,
                ["cost"] = cost,
            };
        }

        private static JObject Option(string id, string name, string description, int cost)
        {
            return TierLeaf(id, name, description, cost);
        }

        private static JObject DefaultSkillTree()
        {
            var branches = new JObject();

            branches["karma_capacity"] = new JObject
            {
                ["name"] = "业力掌控",
                ["description"] = "提升业力安全阈值与消业效率",
                ["icon"] = "💫",
                ["tiers"] = new JObject
                {
                    ["1"] = TierLeaf("karma_capacity_t1", "安全阈值+20", "业力安全阈值+20（120→140）", 1),
                    ["2"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("karma_capacity_t2a", "单次上限+30", "单次作弊业力增加上限+30", 1),
                            Option("karma_capacity_t2b", "业力潮汐", "所有消业事件+30%", 1),
                        },
                    },
                    ["3"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("karma_capacity_t3a", "缓冲", "识破非线性指数从1.5降至1.3", 1),
                            Option("karma_capacity_t3b", "净身", "初始业力从50降至25", 1),
                        },
                    },
                },
            };

            branches["stealth"] = new JObject
            {
                ["name"] = "隐匿之术",
                ["description"] = "降低识破风险，提升逃跑能力",
                ["icon"] = "👻",
                ["tiers"] = new JObject
                {
                    ["1"] = TierLeaf("stealth_t1", "藏锋", "识破惩罚系数从0.1降至0.07", 1),
                    ["2"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("stealth_t2a", "首次透支免判", "每局第一次透支的识破判定跳过", 1),
                            Option("stealth_t2b", "连续规避", "连续3回合不作弊后，下次透支惩罚减半", 1),
                        },
                    },
                    ["3"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("stealth_t3a", "金蝉脱壳", "被识破后1次复活（识破概率回退到触发前的50%）", 1),
                            Option("stealth_t3b", "迷雾", "识破概率>70%后，每次结算有30%概率Δ=0", 1),
                        },
                    },
                },
            };

            branches["cheat_mastery"] = new JObject
            {
                ["name"] = "作弊精通",
                ["description"] = "解锁高级作弊能力，降低作弊消耗",
                ["icon"] = "🎲",
                ["tiers"] = new JObject
                {
                    ["1"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("cheat_mastery_t1a", "自定义棋子", "允许C+类作弊", 1),
                            Option("cheat_mastery_t1b", "前端修改", "允许D类作弊", 1),
                        },
                    },
                    ["2"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("cheat_mastery_t2a", "效率欺诈", "AI评估有30%概率降1档", 1),
                            Option("cheat_mastery_t2b", "高级规则", "允许修改胜利条件/核心规则", 1),
                        },
                    },
                    ["3"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("cheat_mastery_t3a", "白嫖", "每局1次：不消耗业力（透支时仍有惩罚）", 1),
                            Option("cheat_mastery_t3b", "深层作弊", "单次作弊业力增加上限+40", 1),
                        },
                    },
                },
            };

            branches["realm_insight"] = new JObject
            {
                ["name"] = "六道悟道",
                ["description"] = "针对特定道的特殊能力",
                ["icon"] = "🔮",
                ["tiers"] = new JObject
                {
                    ["1"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("realm_insight_t1a", "地狱之眼", "地狱道/饿鬼道作弊业力消耗-25%", 1),
                            Option("realm_insight_t1b", "天道之耳", "天道/阿修罗道透支惩罚-25%", 1),
                        },
                    },
                    ["2"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("realm_insight_t2a", "守道者之隙", "Boss技能触发概率-30%", 1),
                            Option("realm_insight_t2b", "轮回记忆", "每次轮回开局自带1个随机临时buff", 1),
                        },
                    },
                    ["3"] = new JObject
                    {
                        ["options"] = new JArray
                        {
                            Option("realm_insight_t3a", "超脱之种", "清业通关后，全局透支惩罚系数永久-0.05", 1),
                            Option("realm_insight_t3b", "六道轮转", "升降道时，额外1次免费作弊机会", 1),
                        },
                    },
                },
            };

            return new JObject { ["branches"] = branches };
        }

        /// <summary>返回带锁定状态的完整技能树。</summary>
        public JObject GetSkillTree()
        {
            var result = new JObject();
            var branches = _skillTree["branches"] as JObject;
            if (branches == null) return result;

            foreach (var branchProp in branches.Properties())
            {
                var branch = branchProp.Value as JObject;
                if (branch == null) continue;
                var branchData = new JObject
                {
                    ["name"] = SamsaraJson.GetString(branch, "name", ""),
                    ["description"] = SamsaraJson.GetString(branch, "description", ""),
                    ["icon"] = SamsaraJson.GetString(branch, "icon", ""),
                    ["tiers"] = new JObject(),
                };
                var tiersOut = (JObject)branchData["tiers"];
                var tiers = branch["tiers"] as JObject;
                if (tiers != null)
                {
                    foreach (var tierProp in tiers.Properties())
                    {
                        var tier = ParseTier(tierProp.Name);
                        var tierData = tierProp.Value as JObject;
                        if (tierData == null) continue;
                        var options = tierData["options"] as JArray;
                        if (options != null)
                        {
                            var tierOptions = new JArray();
                            foreach (var opt in options)
                            {
                                var optObj = opt as JObject;
                                var copy = SamsaraJson.Clone(optObj) ?? new JObject();
                                copy["unlocked"] = _state.IsSkillUnlocked(SamsaraJson.GetString(optObj, "id", ""), tier);
                                tierOptions.Add(copy);
                            }
                            tiersOut[tierProp.Name] = new JObject { ["options"] = tierOptions };
                        }
                        else
                        {
                            var copy = SamsaraJson.Clone(tierData) ?? new JObject();
                            copy["unlocked"] = _state.IsSkillUnlocked(SamsaraJson.GetString(tierData, "id", ""), tier);
                            tiersOut[tierProp.Name] = copy;
                        }
                    }
                }
                result[branchProp.Name] = branchData;
            }
            return result;
        }

        private static int? ParseTier(string value)
        {
            int tier;
            if (int.TryParse(value, out tier)) return tier;
            return null;
        }

        /// <summary>解锁技能（校验前置层、扣点并落地；失败时回滚技能点）。</summary>
        public bool UnlockSkill(string skillId, int tier)
        {
            var branches = _skillTree["branches"] as JObject;
            if (branches == null) return false;

            var branchId = SplitBranchId(skillId);
            var branch = branches[branchId] as JObject;
            if (branch == null) return false;

            // 幂等短路：已解锁的技能直接拒绝，避免重复扣点。
            if (_state.IsSkillUnlocked(skillId)) return false;

            var tierKey = tier.ToString();
            if (tier > 1)
            {
                var prevSkill = branchId + "_t" + (tier - 1);
                if (!_state.IsSkillUnlocked(prevSkill, tier - 1))
                {
                    var tiers = branch["tiers"] as JObject;
                    var prevTier = tiers == null ? null : tiers[(tier - 1).ToString()] as JObject;
                    if (prevTier != null && prevTier["options"] != null)
                    {
                        var hasPrev = false;
                        foreach (var opt in (JArray)prevTier["options"])
                        {
                            if (_state.IsSkillUnlocked(SamsaraJson.GetString(opt as JObject, "id", ""), tier - 1))
                            {
                                hasPrev = true;
                                break;
                            }
                        }
                        if (!hasPrev) return false;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            var allTiers = branch["tiers"] as JObject;
            var tierData = allTiers == null ? null : allTiers[tierKey] as JObject;
            if (tierData == null) return false;

            if (tierData["options"] != null)
            {
                foreach (var opt in (JArray)tierData["options"])
                {
                    var optObj = opt as JObject;
                    if (SamsaraJson.GetString(optObj, "id", "") == skillId)
                    {
                        var cost = SamsaraJson.GetInt(optObj, "cost", 1);
                        if (_state.SpendSkillPoint(cost))
                        {
                            if (_state.UnlockSkill(skillId, tier)) return true;
                            _state.RefundSkillPoint(cost);
                        }
                        return false;
                    }
                }
            }
            else if (SamsaraJson.GetString(tierData, "id", "") == skillId)
            {
                var cost = SamsaraJson.GetInt(tierData, "cost", 1);
                if (_state.SpendSkillPoint(cost))
                {
                    if (_state.UnlockSkill(skillId, tier)) return true;
                    _state.RefundSkillPoint(cost);
                }
            }
            return false;
        }

        private static string SplitBranchId(string skillId)
        {
            var id = skillId ?? string.Empty;
            var index = id.IndexOf("_t", StringComparison.Ordinal);
            return index < 0 ? id : id.Substring(0, index);
        }

        /// <summary>返回尚未解锁的可选技能列表。</summary>
        public JArray GetAvailableSkills()
        {
            var available = new JArray();
            var branches = _skillTree["branches"] as JObject;
            if (branches == null) return available;

            foreach (var branchProp in branches.Properties())
            {
                var branch = branchProp.Value as JObject;
                if (branch == null) continue;
                var branchName = SamsaraJson.GetString(branch, "name", "");
                var tiers = branch["tiers"] as JObject;
                if (tiers == null) continue;

                foreach (var tierProp in tiers.Properties())
                {
                    var tier = ParseTier(tierProp.Name);
                    if (tier == null) continue;
                    var tierData = tierProp.Value as JObject;
                    if (tierData == null) continue;
                    var options = tierData["options"] as JArray;
                    if (options != null)
                    {
                        foreach (var opt in options)
                        {
                            var optObj = opt as JObject;
                            var id = SamsaraJson.GetString(optObj, "id", "");
                            if (!_state.IsSkillUnlocked(id, tier)) AddAvailable(available, optObj, id, branchProp.Name, branchName, tier.Value);
                        }
                    }
                    else
                    {
                        var id = SamsaraJson.GetString(tierData, "id", "");
                        if (!_state.IsSkillUnlocked(id, tier)) AddAvailable(available, tierData, id, branchProp.Name, branchName, tier.Value);
                    }
                }
            }
            return available;
        }

        private static void AddAvailable(JArray target, JObject source, string id, string branchId, string branchName, int tier)
        {
            target.Add(new JObject
            {
                ["id"] = id,
                ["branch"] = branchId,
                ["branch_name"] = branchName,
                ["tier"] = tier,
                ["name"] = SamsaraJson.GetString(source, "name", ""),
                ["description"] = SamsaraJson.GetString(source, "description", ""),
                ["cost"] = SamsaraJson.GetInt(source, "cost", 1),
            });
        }
    }
}