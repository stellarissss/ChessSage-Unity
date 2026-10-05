using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 本地业力评估器（对应 legacy-web/shared/karma_assessor_base.py 的本地业障模型）。
    /// 与 Samsara 服务端并行运行，仅供棋类进程内做业力/识破的即时估算；
    /// 所有折扣、上限、公式与 Python 完全一致（键名 snake_case）。
    /// </summary>
    public sealed class KarmaAssessor
    {
        private int _localKarma = 50;
        private int _localKarmaMax = 120;
        private int _localKarmaSingleMax = 120;
        private readonly int _initialKarma = 50;
        private double _realmDetection;
        private string _currentRealm = "human";

        // 一次性技能（stealth_t2a 首次透支免判 / stealth_t3a 金蝉脱壳）本关已消耗记录。
        private readonly HashSet<string> _usedOneTimeSkills = new HashSet<string>();

        /// <summary>设置本地业力状态（关卡内变量）。</summary>
        public void SetLocalKarmaState(int karma, int karmaMax, int singleMax)
        {
            _localKarma = karma;
            _localKarmaMax = karmaMax;
            _localKarmaSingleMax = singleMax;
        }

        /// <summary>设置道级识破概率（全局变量）。</summary>
        public void SetRealmDetection(double detection, string realm)
        {
            _realmDetection = detection;
            _currentRealm = realm;
        }

        public int GetLocalKarma() { return _localKarma; }
        public int GetLocalKarmaMax() { return _localKarmaMax; }
        public double GetRealmDetection() { return _realmDetection; }

        /// <summary>
        /// 增加业力（作弊产生业障）。返回 {actual_increased, is_overdraft,
        /// overdraft_amount, new_karma, success}；超出单次上限时 success=false 且不改变业力。
        /// </summary>
        public JObject IncreaseKarma(int amount, JObject skillModifiers)
        {
            if (skillModifiers == null) skillModifiers = new JObject();

            var maxSingle = _localKarmaSingleMax + SamsaraJson.GetInt(skillModifiers, "karma_single_max_bonus", 0);
            if (amount > maxSingle)
            {
                return new JObject
                {
                    ["actual_increased"] = 0,
                    ["is_overdraft"] = false,
                    ["overdraft_amount"] = 0,
                    ["new_karma"] = _localKarma,
                    ["success"] = false,
                };
            }

            var threshold = _localKarmaMax + SamsaraJson.GetInt(skillModifiers, "karma_max_bonus", 0);
            _localKarma += amount;
            var overshoot = Math.Max(0, _localKarma - threshold);

            return new JObject
            {
                ["actual_increased"] = amount,
                ["is_overdraft"] = overshoot > 0,
                ["overdraft_amount"] = (double)overshoot,
                ["new_karma"] = _localKarma,
                ["success"] = true,
            };
        }

        /// <summary>
        /// 减少业力（下棋消业）。返回 {actual_decreased, new_karma}；最小业力为 0。
        /// </summary>
        public JObject DecreaseKarma(int amount, JObject skillModifiers)
        {
            if (skillModifiers == null) skillModifiers = new JObject();
            var multiplier = SamsaraJson.GetDouble(skillModifiers, "karma_recover_multiplier", 1.0);
            var actual = (int)(amount * multiplier);
            var old = _localKarma;
            _localKarma = Math.Max(0, _localKarma - actual);
            return new JObject
            {
                ["actual_decreased"] = old - _localKarma,
                ["new_karma"] = _localKarma,
            };
        }

        /// <summary>退还业力（作弊失败时全额退还），1:1 退还无加成。</summary>
        public void RefundKarma(int amount, JObject skillModifiers)
        {
            _localKarma = Math.Max(0, _localKarma - amount);
        }

        /// <summary>计算识破概率增长 Δ = C × O^α（C 默认 0.1，α 默认 1.5）。</summary>
        public double CalculateDetectionDelta(double overshootAmount, JObject skillModifiers)
        {
            if (skillModifiers == null) skillModifiers = new JObject();
            if (overshootAmount <= 0) return 0.0;

            var c = SamsaraJson.GetDouble(skillModifiers, "detection_coefficient", 0.1);
            var alpha = SamsaraJson.GetDouble(skillModifiers, "detection_alpha", 1.5);
            var delta = c * Math.Pow(overshootAmount, alpha);

            // 雾隐：高识破时概率不累积。
            if (SamsaraJson.GetBool(skillModifiers, "mist_fog", false) && _realmDetection > 70)
            {
                if (SamsaraRandom.Chance(0.3)) return 0.0;
            }
            return delta;
        }

        /// <summary>
        /// 处理业力超阈值，更新本地识破概率并做概率结算。
        /// 返回 {detected, delta, current, escaped?, reset?, message?, skip?}。
        /// </summary>
        public JObject HandleOverdraft(int overshoot, JObject skillModifiers)
        {
            if (skillModifiers == null) skillModifiers = new JObject();

            if (overshoot <= 0)
            {
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = _realmDetection,
                };
            }

            // 一次性技能（每关一次）：本关已消耗则不再生效。
            if (SamsaraJson.GetBool(skillModifiers, "first_overdraft_skip", false)
                && !_usedOneTimeSkills.Contains("stealth_t2a"))
            {
                _usedOneTimeSkills.Add("stealth_t2a");
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = _realmDetection,
                    ["skip"] = true,
                };
            }

            var delta = CalculateDetectionDelta(overshoot, skillModifiers);
            if (delta <= 0)
            {
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = _realmDetection,
                };
            }

            _realmDetection = Math.Min(_realmDetection + delta, 100.0);
            var current = _realmDetection;

            var roll = SamsaraRandom.NextDouble() * 100;
            var detected = roll < current;

            if (detected)
            {
                if (SamsaraJson.GetBool(skillModifiers, "golden_escape", false)
                    && !_usedOneTimeSkills.Contains("stealth_t3a"))
                {
                    _usedOneTimeSkills.Add("stealth_t3a");
                    _realmDetection = current * 0.5;
                    return new JObject
                    {
                        ["detected"] = false,
                        ["delta"] = delta,
                        ["current"] = current * 0.5,
                        ["escaped"] = true,
                    };
                }
                return new JObject
                {
                    ["detected"] = true,
                    ["delta"] = delta,
                    ["current"] = 0.0,
                    ["reset"] = true,
                    ["message"] = "天道识破 · 妄改天规者，罚入轮回",
                };
            }

            return new JObject
            {
                ["detected"] = false,
                ["delta"] = delta,
                ["current"] = current,
            };
        }

        /// <summary>返回当前业力与识破状态（键名与 Python get_state 一致）。</summary>
        public JObject GetState(JObject skillModifiers)
        {
            if (skillModifiers == null) skillModifiers = new JObject();
            var state = new JObject();
            state["karma"] = new JObject
            {
                ["current"] = _localKarma,
                ["max"] = _localKarmaMax + SamsaraJson.GetInt(skillModifiers, "karma_max_bonus", 0),
                ["single_max"] = _localKarmaSingleMax + SamsaraJson.GetInt(skillModifiers, "karma_single_max_bonus", 0),
                ["initial"] = Math.Max(0, _initialKarma - SamsaraJson.GetInt(skillModifiers, "initial_karma_reduction", 0)),
            };
            state["detection"] = _realmDetection;
            state["realm"] = _currentRealm;
            return state;
        }
    }
}