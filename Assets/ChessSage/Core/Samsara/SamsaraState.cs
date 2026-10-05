using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 六道轮回持久化状态机（对应 legacy-web/samsara/state.py 的 SamsaraState）。
    /// 状态以 JObject 保存，字段名保持 snake_case 与 Python/JSON 真源一致。
    /// </summary>
    public sealed class SamsaraState
    {
        private readonly string _globalConfigDir;
        private readonly string _savePath;
        private readonly HashSet<string> _skillIds;

        // 权威技能 id 的历史冗余后缀形如 "{id}_t{tier}"，需剥离还原权威 id。
        private static readonly Regex SkillSuffixPattern = new Regex(@"^(.+?)_t\d+$", RegexOptions.Compiled);

        private JObject _data;

        public SamsaraState(string globalConfigDir, string savePath = null)
        {
            _globalConfigDir = globalConfigDir ?? string.Empty;
            _savePath = savePath;
            _skillIds = LoadSkillIds();
            _data = LoadInitialData();
            InitDefaults();
        }

        /// <summary>存档文件路径（保存目标）。</summary>
        public string SavePath { get { return _savePath; } }

        // ══════════════════════════════════════════════════════════
        // 加载 / 默认化 / 持久化
        // ══════════════════════════════════════════════════════════

        private JObject LoadInitialData()
        {
            if (!string.IsNullOrEmpty(_savePath) && File.Exists(_savePath))
            {
                var saved = SamsaraJson.LoadObject(_savePath);
                if (saved != null) return saved;
            }
            var template = SamsaraJson.LoadObject(Path.Combine(_globalConfigDir, "samsara_state.json"));
            return template ?? new JObject();
        }

        private HashSet<string> LoadSkillIds()
        {
            var ids = new HashSet<string>();
            var tree = SamsaraJson.LoadObject(Path.Combine(_globalConfigDir, "skill_tree.json"));
            var branches = tree == null ? null : tree["branches"] as JObject;
            if (branches == null) return ids;
            foreach (var branchProp in branches.Properties())
            {
                var branch = branchProp.Value as JObject;
                var tiers = branch == null ? null : branch["tiers"] as JObject;
                if (tiers == null) continue;
                foreach (var tierProp in tiers.Properties())
                {
                    var tierData = tierProp.Value as JObject;
                    if (tierData == null) continue;
                    var options = tierData["options"] as JArray;
                    if (options != null)
                    {
                        foreach (var opt in options)
                        {
                            var id = SamsaraJson.GetString(opt as JObject, "id", null);
                            if (!string.IsNullOrEmpty(id)) ids.Add(id);
                        }
                    }
                    else
                    {
                        var id = SamsaraJson.GetString(tierData, "id", null);
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
                }
            }
            return ids;
        }

        /// <summary>把任意历史格式的技能键规范化为技能树权威 id（state.py normalize_skill_key）。</summary>
        public string NormalizeSkillKey(string skillId)
        {
            var sid = (skillId ?? string.Empty).Trim();
            if (_skillIds.Contains(sid)) return sid;
            var match = SkillSuffixPattern.Match(sid);
            if (match.Success && _skillIds.Contains(match.Groups[1].Value)) return match.Groups[1].Value;
            return sid;
        }

        private static JObject DefaultAlignment()
        {
            return new JObject
            {
                ["enlightenment"] = 0,
                ["corruption"] = 0,
                ["rationality"] = 0,
                ["emotion"] = 0,
            };
        }

        private static JObject DefaultDetectionState()
        {
            return new JObject
            {
                ["is_detected"] = false,
                ["detection_locked"] = false,
                ["trigger_boss_on_complete"] = false,
                ["exposure_path_triggered"] = false,
            };
        }

        private static JObject DefaultEndingsUnlocked()
        {
            return new JObject
            {
                ["enlightenment"] = false,
                ["corruption"] = false,
                ["samsara"] = false,
                ["true_me"] = false,
                ["exposed"] = false,
            };
        }

        private static JObject DefaultMemoryFragments()
        {
            var frags = new JObject();
            foreach (var realm in SamsaraConstants.Realms) frags[realm] = false;
            return frags;
        }

        private static JObject DefaultTiandaoBossState()
        {
            return new JObject
            {
                ["defeated"] = false,
                ["attempt_count"] = 0,
                ["current_battle_active"] = false,
            };
        }

        private static JObject DefaultStoryProgress()
        {
            return new JObject
            {
                ["prologue_seen"] = false,
                ["current_dialogue_realm"] = JValue.CreateNull(),
                ["current_dialogue_level"] = JValue.CreateNull(),
                ["last_choice_made"] = JValue.CreateNull(),
            };
        }

        private static JObject DefaultRealmProgress()
        {
            var progress = new JObject();
            foreach (var realm in SamsaraConstants.Realms)
            {
                progress[realm] = new JObject
                {
                    ["completed"] = false,
                    ["levels_passed"] = 0,
                    ["no_cheat_full_clear"] = false,
                };
            }
            return progress;
        }

        private void InitDefaults()
        {
            var defaults = new JObject();
            defaults["version"] = 3;
            defaults["current_realm"] = "hell";
            defaults["current_level"] = 0;
            defaults["skill_points"] = 0;
            defaults["karma_max"] = 120;
            defaults["karma_single_max"] = 120;
            defaults["initial_karma"] = 50;
            defaults["realm_overshoot_carryover"] = 0;
            var realmDetections = new JObject();
            foreach (var realm in SamsaraConstants.Realms) realmDetections[realm] = 0.0;
            defaults["realm_detections"] = realmDetections;
            defaults["skills"] = new JObject();
            defaults["one_time_skill_usage"] = new JObject();
            defaults["current_turn"] = 0;
            defaults["turn_limit"] = 20;
            defaults["cheat_count"] = 0;
            defaults["overdraft_count"] = 0;
            defaults["no_cheat_this_level"] = true;
            defaults["total_levels_completed"] = 0;
            defaults["bosses_defeated"] = new JArray();
            defaults["sandbox_unlocked"] = new JArray();
            defaults["sandbox_mode"] = false;
            defaults["realm_progress"] = DefaultRealmProgress();
            defaults["level_karma"] = 50;
            defaults["alignment"] = DefaultAlignment();
            defaults["detection_state"] = DefaultDetectionState();
            defaults["memory_fragments_unlocked"] = DefaultMemoryFragments();
            defaults["choices_made"] = new JArray();
            defaults["prayer_count"] = 0;
            defaults["endings_unlocked"] = DefaultEndingsUnlocked();
            defaults["tiandao_boss_state"] = DefaultTiandaoBossState();
            defaults["story_progress"] = DefaultStoryProgress();
            defaults["playthrough_count"] = 1;
            defaults["last_modified"] = SamsaraJson.NowIso();

            foreach (var prop in defaults.Properties())
            {
                if (_data[prop.Name] == null) _data[prop.Name] = prop.Value.DeepClone();
            }

            // 技能键规范化：旧格式 "{id}_t{tier}" 就地迁移为权威 id。
            var normalizedSkills = new JObject();
            var skillsChanged = false;
            var existingSkills = _data["skills"] as JObject ?? new JObject();
            foreach (var prop in existingSkills.Properties())
            {
                var norm = NormalizeSkillKey(prop.Name);
                if (norm != prop.Name) skillsChanged = true;
                var meta = prop.Value as JObject;
                if (normalizedSkills[norm] != null)
                {
                    // 同一技能出现两种格式：保留更早的 unlocked_at。
                    var oldTs = SamsaraJson.GetString(normalizedSkills[norm] as JObject, "unlocked_at", string.Empty);
                    var newTs = SamsaraJson.GetString(meta, "unlocked_at", string.Empty);
                    if (!string.IsNullOrEmpty(newTs) && (string.IsNullOrEmpty(oldTs) || string.CompareOrdinal(newTs, oldTs) < 0))
                        normalizedSkills[norm] = meta.DeepClone();
                }
                else
                {
                    normalizedSkills[norm] = prop.Value.DeepClone();
                }
            }
            if (skillsChanged || normalizedSkills.Count != existingSkills.Count)
            {
                _data["skills"] = normalizedSkills;
                Save();
            }

            // 迁移：v1→v2 业障模型。
            if (SamsaraJson.GetInt(_data, "karma_max", 120) == 150) _data["karma_max"] = 120;
            if (SamsaraJson.GetInt(_data, "karma_single_max", 120) != 120) _data["karma_single_max"] = 120;
            if (_data["initial_karma"] == null) _data["initial_karma"] = 50;
            if (_data["realm_overshoot_carryover"] == null) _data["realm_overshoot_carryover"] = 0;

            if (SamsaraJson.GetInt(_data, "version", 1) < 2)
            {
                _data["version"] = 2;
                _data["level_karma"] = SamsaraJson.GetInt(_data, "initial_karma", 50);
            }
            // 迁移：v2→v3 RPG 字段。
            if (SamsaraJson.GetInt(_data, "version", 1) < 3) _data["version"] = 3;
            // 迁移：v3→v4 业力上限统一为 120，回收历史自动下发的 karma_capacity_t1。
            if (SamsaraJson.GetInt(_data, "version", 1) < 4)
            {
                _data["version"] = 4;
                var skills = _data["skills"] as JObject;
                if (skills != null && skills["karma_capacity_t1"] != null) skills.Remove("karma_capacity_t1");
            }

            // 补齐 realm_progress 子字段（向后兼容）。
            var realmProgress = _data["realm_progress"] as JObject;
            if (realmProgress == null)
            {
                realmProgress = new JObject();
                _data["realm_progress"] = realmProgress;
            }
            foreach (var realm in SamsaraConstants.Realms)
            {
                var entry = realmProgress[realm] as JObject;
                if (entry == null) entry = new JObject();
                if (entry["no_cheat_full_clear"] == null) entry["no_cheat_full_clear"] = false;
                realmProgress[realm] = entry;
            }

            if (existingSkills.Count == 0)
            {
                var starter = new JObject();
                starter["stealth_t1"] = new JObject
                {
                    ["unlocked_at"] = SamsaraJson.NowIso(),
                    ["tier"] = 1,
                };
                _data["skills"] = starter;
                Save();
            }
        }

        /// <summary>写回存档（更新 last_modified）。savePath 为空时仅更新内存状态。</summary>
        public void Save()
        {
            _data["last_modified"] = SamsaraJson.NowIso();
            if (string.IsNullOrEmpty(_savePath)) return;
            var dir = Path.GetDirectoryName(_savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_savePath, _data.ToString(Newtonsoft.Json.Formatting.Indented));
        }

        /// <summary>从存档文件重新载入（state.py 构造函数加载逻辑 + _init_defaults）。</summary>
        public void Load()
        {
            if (string.IsNullOrEmpty(_savePath) || !File.Exists(_savePath)) return;
            var loaded = SamsaraJson.LoadObject(_savePath);
            if (loaded == null) return;
            _data = loaded;
            InitDefaults();
        }

        // ══════════════════════════════════════════════════════════
        // 通用读写（state.py get / set / update / get_full_state）
        // ══════════════════════════════════════════════════════════

        public JToken Get(string key, JToken fallback = null)
        {
            var token = _data[key];
            return token ?? fallback;
        }

        public void Set(string key, JToken value)
        {
            _data[key] = value;
            Save();
        }

        public void Update(JObject data)
        {
            if (data == null) return;
            foreach (var prop in data.Properties()) _data[prop.Name] = prop.Value.DeepClone();
            Save();
        }

        public JObject GetFullState()
        {
            return (JObject)_data.DeepClone();
        }

        public int GetInt(string key, int fallback) { return SamsaraJson.GetInt(_data, key, fallback); }
        public double GetDouble(string key, double fallback) { return SamsaraJson.GetDouble(_data, key, fallback); }
        public bool GetBool(string key, bool fallback) { return SamsaraJson.GetBool(_data, key, fallback); }
        public string GetString(string key, string fallback) { return SamsaraJson.GetString(_data, key, fallback); }

        // ══════════════════════════════════════════════════════════
        // 单局状态（state.py reset_level_state / record_level_end / advance_level / set_realm）
        // ══════════════════════════════════════════════════════════

        /// <summary>关卡开始：业力 = 初始值 + 本道溢出叠加，并清零本关计数。</summary>
        public void ResetLevelState()
        {
            var modifiers = GetSkillModifiers();
            var reduction = SamsaraJson.GetInt(modifiers, "initial_karma_reduction", 0);
            var baseKarma = Math.Max(0, GetInt("initial_karma", 50) - reduction);
            var carryover = GetInt("realm_overshoot_carryover", 0);
            _data["level_karma"] = baseKarma + carryover;
            _data["current_turn"] = 0;
            _data["cheat_count"] = 0;
            _data["overdraft_count"] = 0;
            _data["no_cheat_this_level"] = true;
            // 一次性技能为“每关一次”，关卡开始时重置。
            var usage = _data["one_time_skill_usage"] as JObject;
            if (usage != null && usage.Count > 0) _data["one_time_skill_usage"] = new JObject();
            Save();
        }

        /// <summary>关卡结束：溢出量 = max(0, level_karma - (karma_max + 加成))，叠加到本道下一局。</summary>
        public int RecordLevelEnd()
        {
            var threshold = GetInt("karma_max", 120);
            var modifiers = GetSkillModifiers();
            threshold += SamsaraJson.GetInt(modifiers, "karma_max_bonus", 0);
            var current = GetInt("level_karma", 0);
            var overshoot = Math.Max(0, current - threshold);
            _data["realm_overshoot_carryover"] = overshoot;
            Save();
            return overshoot;
        }

        /// <summary>清零本道溢出叠加（换道/被识破时调用）。</summary>
        public void ClearOvershootCarryover()
        {
            _data["realm_overshoot_carryover"] = 0;
            Save();
        }

        public void AdvanceLevel()
        {
            _data["current_level"] = GetInt("current_level", 0) + 1;
            Save();
        }

        /// <summary>切换道：清零溢出叠加（章节=道，换道不携带业力溢出）。</summary>
        public void SetRealm(string realm)
        {
            _data["current_realm"] = realm;
            _data["current_level"] = 0;
            _data["realm_overshoot_carryover"] = 0;
            Save();
        }

        public int GetCurrentLevel() { return GetInt("current_level", 0); }
        public string GetCurrentRealm() { return GetString("current_realm", "hell"); }
        public int GetTotalLevelsCompleted() { return GetInt("total_levels_completed", 0); }

        // ══════════════════════════════════════════════════════════
        // 技能点与技能（state.py add/spend/refund/unlock/is_skill_unlocked）
        // ══════════════════════════════════════════════════════════

        public int GetSkillPoints() { return GetInt("skill_points", 0); }

        public void AddSkillPoint(int count = 1)
        {
            _data["skill_points"] = GetInt("skill_points", 0) + count;
            Save();
        }

        public bool SpendSkillPoint(int count = 1)
        {
            if (GetInt("skill_points", 0) >= count)
            {
                _data["skill_points"] = GetInt("skill_points", 0) - count;
                Save();
                return true;
            }
            return false;
        }

        /// <summary>退还技能点（扣点成功但解锁未生效时的回滚路径）。</summary>
        public void RefundSkillPoint(int count = 1)
        {
            _data["skill_points"] = GetInt("skill_points", 0) + count;
            Save();
        }

        /// <summary>解锁技能：以技能树权威 id 作为存储键。</summary>
        public bool UnlockSkill(string skillId, int tier)
        {
            var skillKey = NormalizeSkillKey(skillId);
            var skills = EnsureSkills();
            if (skills[skillKey] != null) return false;
            skills[skillKey] = new JObject
            {
                ["unlocked_at"] = SamsaraJson.NowIso(),
                ["tier"] = tier,
            };
            Save();
            return true;
        }

        /// <summary>判断技能是否已解锁（tier 仅保留兼容签名）。</summary>
        public bool IsSkillUnlocked(string skillId, int? tier = null)
        {
            var skills = _data["skills"] as JObject;
            return skills != null && skills[NormalizeSkillKey(skillId)] != null;
        }

        /// <summary>消耗一次性技能。首次消耗返回 true；已消耗过返回 false（幂等）。</summary>
        public bool ConsumeOneTimeSkill(string skillId, string context = "")
        {
            var skillKey = NormalizeSkillKey(skillId);
            var usage = _data["one_time_skill_usage"] as JObject;
            if (usage == null)
            {
                usage = new JObject();
                _data["one_time_skill_usage"] = usage;
            }
            if (usage[skillKey] != null) return false;
            usage[skillKey] = new JObject
            {
                ["used_at"] = SamsaraJson.NowIso(),
                ["context"] = context ?? string.Empty,
            };
            Save();
            return true;
        }

        public bool IsOneTimeSkillUsed(string skillId)
        {
            var usage = _data["one_time_skill_usage"] as JObject;
            return usage != null && usage[NormalizeSkillKey(skillId)] != null;
        }

        private JObject EnsureSkills()
        {
            var skills = _data["skills"] as JObject;
            if (skills == null)
            {
                skills = new JObject();
                _data["skills"] = skills;
            }
            return skills;
        }

        // ══════════════════════════════════════════════════════════
        // 作弊 / 透支计数
        // ══════════════════════════════════════════════════════════

        public void RecordCheat()
        {
            _data["cheat_count"] = GetInt("cheat_count", 0) + 1;
            _data["no_cheat_this_level"] = false;
            Save();
        }

        public void RecordOverdraft()
        {
            _data["overdraft_count"] = GetInt("overdraft_count", 0) + 1;
            Save();
        }

        // ══════════════════════════════════════════════════════════
        // 业力（state.py increase/decrease/refund）
        // ══════════════════════════════════════════════════════════

        /// <summary>增加业力，返回 (实际增加量, 是否溢出, 溢出量)。</summary>
        public Tuple<int, bool, double> IncreaseKarma(int amount)
        {
            _data["level_karma"] = GetInt("level_karma", 0) + amount;
            var threshold = GetInt("karma_max", 120);
            var overshoot = Math.Max(0, GetInt("level_karma", 0) - threshold);
            Save();
            return Tuple.Create(amount, overshoot > 0, (double)overshoot);
        }

        /// <summary>减少业力（下棋消业/退还），最小为 0，返回实际减少量。</summary>
        public int DecreaseKarma(int amount)
        {
            var old = GetInt("level_karma", 0);
            var updated = Math.Max(0, old - amount);
            _data["level_karma"] = updated;
            Save();
            return old - updated;
        }

        /// <summary>退还业力（作弊失败时全额退还）。</summary>
        public void RefundKarma(int amount)
        {
            DecreaseKarma(amount);
        }

        public int GetKarma() { return GetInt("level_karma", 0); }

        public int GetKarmaMax()
        {
            var modifiers = GetSkillModifiers();
            return GetInt("karma_max", 120) + SamsaraJson.GetInt(modifiers, "karma_max_bonus", 0);
        }

        public int GetKarmaSingleMax()
        {
            var modifiers = GetSkillModifiers();
            return GetInt("karma_single_max", 120) + SamsaraJson.GetInt(modifiers, "karma_single_max_bonus", 0);
        }

        // ══════════════════════════════════════════════════════════
        // 识破（state.py detection 相关）
        // ══════════════════════════════════════════════════════════

        private JObject EnsureRealmDetections()
        {
            var detections = _data["realm_detections"] as JObject;
            if (detections == null)
            {
                detections = new JObject();
                foreach (var realm in SamsaraConstants.Realms) detections[realm] = 0.0;
                _data["realm_detections"] = detections;
            }
            return detections;
        }

        public void SetDetection(double value)
        {
            var realm = GetString("current_realm", "hell");
            EnsureRealmDetections()[realm] = value;
            Save();
        }

        public double GetDetection()
        {
            var realm = GetString("current_realm", "hell");
            return SamsaraJson.GetDouble(EnsureRealmDetections(), realm, 0.0);
        }

        public void IncrementDetection(double delta)
        {
            var realm = GetString("current_realm", "hell");
            var detections = EnsureRealmDetections();
            var current = SamsaraJson.GetDouble(detections, realm, 0.0);
            detections[realm] = Math.Min(current + delta, 100.0);
            Save();
        }

        public double GetRealmDetection(string realm)
        {
            return SamsaraJson.GetDouble(EnsureRealmDetections(), realm, 0.0);
        }

        public void SetRealmDetection(string realm, double value)
        {
            EnsureRealmDetections()[realm] = value;
            Save();
        }

        public JObject GetDetectionState()
        {
            var state = _data["detection_state"] as JObject;
            if (state == null)
            {
                state = DefaultDetectionState();
                _data["detection_state"] = state;
            }
            return state;
        }

        public bool IsDetectionLocked()
        {
            return SamsaraJson.GetBool(GetDetectionState(), "detection_locked", false);
        }

        public bool IsExposurePathTriggered()
        {
            return SamsaraJson.GetBool(GetDetectionState(), "exposure_path_triggered", false);
        }

        /// <summary>被识破：标记识破状态、锁死概率、清零各道识破并触发 Boss 战路径。</summary>
        public void ResetOnDetection()
        {
            _data["detection_state"] = new JObject
            {
                ["is_detected"] = true,
                ["detection_locked"] = true,
                ["trigger_boss_on_complete"] = true,
                ["exposure_path_triggered"] = true,
            };
            var detections = EnsureRealmDetections();
            foreach (var realm in SamsaraConstants.Realms) detections[realm] = 0.0;
            Save();
        }

        // ══════════════════════════════════════════════════════════
        // 回合（state.py turn 相关）
        // ══════════════════════════════════════════════════════════

        public void IncrementTurn()
        {
            _data["current_turn"] = GetInt("current_turn", 0) + 1;
            Save();
        }

        public void ResetTurn()
        {
            _data["current_turn"] = 0;
            Save();
        }

        public void SetTurnLimit(int limit)
        {
            _data["turn_limit"] = limit;
            Save();
        }

        public int GetTurnLimit() { return GetInt("turn_limit", 20); }
        public int GetCurrentTurn() { return GetInt("current_turn", 0); }

        // ══════════════════════════════════════════════════════════
        // 道进度 / Boss（state.py realm/boss 相关）
        // ══════════════════════════════════════════════════════════

        public void MarkRealmCompleted(string realm)
        {
            var entry = EnsureRealmProgressEntry(realm);
            entry["completed"] = true;
            Save();
        }

        public void IncrementRealmLevelsPassed(string realm)
        {
            var entry = EnsureRealmProgressEntry(realm);
            entry["levels_passed"] = SamsaraJson.GetInt(entry, "levels_passed", 0) + 1;
            Save();
        }

        /// <summary>标记某道全程无作弊通关（用于记忆碎片解锁条件）。</summary>
        public void MarkRealmNoCheatClear(string realm)
        {
            var entry = EnsureRealmProgressEntry(realm);
            entry["no_cheat_full_clear"] = true;
            Save();
        }

        public bool IsRealmNoCheatClear(string realm)
        {
            var progress = _data["realm_progress"] as JObject;
            var entry = progress == null ? null : progress[realm] as JObject;
            return SamsaraJson.GetBool(entry, "no_cheat_full_clear", false);
        }

        private JObject EnsureRealmProgressEntry(string realm)
        {
            var progress = _data["realm_progress"] as JObject;
            if (progress == null)
            {
                progress = new JObject();
                _data["realm_progress"] = progress;
            }
            var entry = progress[realm] as JObject;
            if (entry == null)
            {
                entry = new JObject
                {
                    ["completed"] = false,
                    ["levels_passed"] = 0,
                    ["no_cheat_full_clear"] = false,
                };
                progress[realm] = entry;
            }
            return entry;
        }

        public void MarkBossDefeated(string bossId)
        {
            var defeated = _data["bosses_defeated"] as JArray;
            if (defeated == null)
            {
                defeated = new JArray();
                _data["bosses_defeated"] = defeated;
            }
            if (!defeated.Contains(bossId))
            {
                defeated.Add(bossId);
                Save();
            }
        }

        public bool IsBossDefeated(string bossId)
        {
            var defeated = _data["bosses_defeated"] as JArray;
            return defeated != null && defeated.Contains(bossId);
        }

        // ══════════════════════════════════════════════════════════
        // 技能修饰符（state.py get_skill_modifiers）
        // ══════════════════════════════════════════════════════════

        /// <summary>汇总当前技能树解锁后的效果修饰符（键名与 Python 完全一致）。</summary>
        public JObject GetSkillModifiers()
        {
            var karmaMaxBonus = 0;
            var karmaSingleMaxBonus = 0;
            var karmaRecoverMultiplier = 1.0;
            var detectionCoefficient = 0.1;
            var detectionAlpha = 1.5;
            var firstOverdraftSkip = false;
            var consecutiveAvoid = false;
            var goldenEscape = false;
            var mistFog = false;
            var efficiencyFraud = false;
            var freeCheatCount = 0;
            var initialKarmaReduction = 0;
            var hellHungryDiscount = false;
            var heavenAsuraDiscount = false;
            var bossSkillReduction = false;
            var reincarnationBuff = false;
            var transcendenceBonus = 0.0;
            var freeCheatOnRealmChange = false;

            var skills = new JObject();
            var rawSkills = _data["skills"] as JObject;
            if (rawSkills != null)
            {
                foreach (var prop in rawSkills.Properties())
                {
                    var norm = NormalizeSkillKey(prop.Name);
                    if (skills[norm] == null) skills[norm] = prop.Value.DeepClone();
                }
            }
            var usedOneTime = _data["one_time_skill_usage"] as JObject ?? new JObject();

            if (skills["karma_capacity_t1"] != null) karmaMaxBonus += 20;
            if (skills["karma_capacity_t2a"] != null) karmaSingleMaxBonus += 30;
            if (skills["karma_capacity_t2b"] != null) karmaRecoverMultiplier = 1.3;
            if (skills["karma_capacity_t3a"] != null) detectionAlpha = 1.3;
            if (skills["karma_capacity_t3b"] != null) initialKarmaReduction = 25;
            if (skills["stealth_t1"] != null) detectionCoefficient = 0.07;
            if (skills["stealth_t2a"] != null && usedOneTime["stealth_t2a"] == null) firstOverdraftSkip = true;
            if (skills["stealth_t2b"] != null) consecutiveAvoid = true;
            if (skills["stealth_t3a"] != null && usedOneTime["stealth_t3a"] == null) goldenEscape = true;
            if (skills["stealth_t3b"] != null) mistFog = true;
            if (skills["cheat_mastery_t2a"] != null) efficiencyFraud = true;
            if (skills["cheat_mastery_t3a"] != null) freeCheatCount += 1;
            if (skills["cheat_mastery_t3b"] != null) karmaSingleMaxBonus += 40;
            if (skills["realm_insight_t1a"] != null) hellHungryDiscount = true;
            if (skills["realm_insight_t1b"] != null) heavenAsuraDiscount = true;
            if (skills["realm_insight_t2a"] != null) bossSkillReduction = true;
            if (skills["realm_insight_t2b"] != null) reincarnationBuff = true;
            if (skills["realm_insight_t3a"] != null) transcendenceBonus -= 0.05;
            if (skills["realm_insight_t3b"] != null) freeCheatOnRealmChange = true;

            var modifiers = new JObject();
            modifiers["karma_max_bonus"] = karmaMaxBonus;
            modifiers["karma_single_max_bonus"] = karmaSingleMaxBonus;
            modifiers["karma_recover_multiplier"] = karmaRecoverMultiplier;
            modifiers["detection_coefficient"] = detectionCoefficient;
            modifiers["detection_alpha"] = detectionAlpha;
            modifiers["first_overdraft_skip"] = firstOverdraftSkip;
            modifiers["consecutive_avoid"] = consecutiveAvoid;
            modifiers["golden_escape"] = goldenEscape;
            modifiers["mist_fog"] = mistFog;
            modifiers["efficiency_fraud"] = efficiencyFraud;
            modifiers["free_cheat_count"] = freeCheatCount;
            modifiers["initial_karma_reduction"] = initialKarmaReduction;
            modifiers["hell_hungry_discount"] = hellHungryDiscount;
            modifiers["heaven_asura_discount"] = heavenAsuraDiscount;
            modifiers["boss_skill_reduction"] = bossSkillReduction;
            modifiers["reincarnation_buff"] = reincarnationBuff;
            modifiers["transcendence_bonus"] = transcendenceBonus;
            modifiers["free_cheat_on_realm_change"] = freeCheatOnRealmChange;
            return modifiers;
        }

        public int GetRealmIndex()
        {
            var realm = GetString("current_realm", "hell");
            var index = Array.IndexOf(SamsaraConstants.Realms, realm);
            return index < 0 ? 0 : index;
        }

        // ══════════════════════════════════════════════════════════
        // 沙盒（state.py sandbox 相关）
        // ══════════════════════════════════════════════════════════

        public bool IsSandboxUnlocked(string realm)
        {
            var unlocked = _data["sandbox_unlocked"] as JArray;
            return unlocked != null && unlocked.Contains(realm);
        }

        public void UnlockSandbox(string realm)
        {
            var unlocked = _data["sandbox_unlocked"] as JArray;
            if (unlocked == null)
            {
                unlocked = new JArray();
                _data["sandbox_unlocked"] = unlocked;
            }
            if (!unlocked.Contains(realm))
            {
                unlocked.Add(realm);
                Save();
            }
        }

        public void SetSandboxMode(bool enabled)
        {
            _data["sandbox_mode"] = enabled;
            Save();
        }

        public bool IsSandboxMode()
        {
            return GetBool("sandbox_mode", false);
        }

        /// <summary>返回当前技能树解锁的所有作弊分类（基础 E/F/A/B/C 恒可用）。</summary>
        public HashSet<string> GetAllowedClassifications()
        {
            var allowed = new HashSet<string> { "E", "F", "A", "B", "C" };
            if (IsSkillUnlocked("cheat_mastery_t1a", 1)) allowed.Add("C+");
            if (IsSkillUnlocked("cheat_mastery_t1b", 1)) allowed.Add("D");
            return allowed;
        }

        // ══════════════════════════════════════════════════════════
        // RPG 字段访问（state.py RPG 段）
        // ══════════════════════════════════════════════════════════

        public JObject GetAlignment()
        {
            var align = _data["alignment"] as JObject;
            if (align == null)
            {
                align = DefaultAlignment();
                _data["alignment"] = align;
            }
            return align;
        }

        /// <summary>根据 effect 调整 alignment；支持 karma_delta 追加单局业力。</summary>
        public void AddAlignment(JObject effect)
        {
            if (effect == null) return;
            var align = GetAlignment();
            foreach (var key in new[] { "enlightenment", "corruption", "rationality", "emotion" })
            {
                if (effect[key] != null)
                    align[key] = SamsaraJson.GetInt(align, key, 0) + SamsaraJson.GetInt(effect, key, 0);
            }
            if (effect["karma_delta"] != null && SamsaraJson.GetInt(effect, "karma_delta", 0) != 0)
                IncreaseKarma(SamsaraJson.GetInt(effect, "karma_delta", 0));
            Save();
        }

        public JObject GetMemoryFragmentsUnlocked()
        {
            var frags = _data["memory_fragments_unlocked"] as JObject;
            if (frags == null)
            {
                frags = DefaultMemoryFragments();
                _data["memory_fragments_unlocked"] = frags;
            }
            return frags;
        }

        public bool UnlockMemoryFragment(string realm)
        {
            var frags = GetMemoryFragmentsUnlocked();
            if (frags[realm] == null) return false;
            if (frags[realm].Value<bool>()) return false;
            frags[realm] = true;
            Save();
            return true;
        }

        public JArray GetChoicesMade()
        {
            var choices = _data["choices_made"] as JArray;
            if (choices == null)
            {
                choices = new JArray();
                _data["choices_made"] = choices;
            }
            return choices;
        }

        public void RecordChoice(JObject choiceRecord)
        {
            if (choiceRecord == null) return;
            var record = new JObject();
            record["timestamp"] = SamsaraJson.NowIso();
            foreach (var prop in choiceRecord.Properties())
                record[prop.Name] = prop.Value.DeepClone();
            GetChoicesMade().Add(record);
            Save();
        }

        public int GetPrayerCount() { return GetInt("prayer_count", 0); }

        /// <summary>增加祈求次数；一旦祈求则标记识破路径。</summary>
        public int IncrementPrayer()
        {
            _data["prayer_count"] = GetInt("prayer_count", 0) + 1;
            var detectionState = GetDetectionState();
            if (!SamsaraJson.GetBool(detectionState, "exposure_path_triggered", false))
            {
                detectionState["exposure_path_triggered"] = true;
                detectionState["trigger_boss_on_complete"] = true;
            }
            Save();
            return GetInt("prayer_count", 0);
        }

        public JObject GetEndingsUnlocked()
        {
            var endings = _data["endings_unlocked"] as JObject;
            if (endings == null)
            {
                endings = DefaultEndingsUnlocked();
                _data["endings_unlocked"] = endings;
            }
            return endings;
        }

        public bool UnlockEnding(string endingId)
        {
            var endings = GetEndingsUnlocked();
            if (endings[endingId] == null) return false;
            if (endings[endingId].Value<bool>()) return false;
            endings[endingId] = true;
            Save();
            return true;
        }

        public JObject GetTiandaoBossState()
        {
            var state = _data["tiandao_boss_state"] as JObject;
            if (state == null)
            {
                state = DefaultTiandaoBossState();
                _data["tiandao_boss_state"] = state;
            }
            return state;
        }

        public void UpdateTiandaoBossState(JObject patch)
        {
            if (patch == null) return;
            var state = GetTiandaoBossState();
            foreach (var prop in patch.Properties()) state[prop.Name] = prop.Value.DeepClone();
            Save();
        }

        public JObject GetStoryProgress()
        {
            var progress = _data["story_progress"] as JObject;
            if (progress == null)
            {
                progress = DefaultStoryProgress();
                _data["story_progress"] = progress;
            }
            return progress;
        }

        public void UpdateStoryProgress(JObject patch)
        {
            if (patch == null) return;
            var progress = GetStoryProgress();
            foreach (var prop in patch.Properties()) progress[prop.Name] = prop.Value.DeepClone();
            Save();
        }

        /// <summary>六道是否全部通关。</summary>
        public bool AllRealmsCompleted()
        {
            var progress = _data["realm_progress"] as JObject;
            foreach (var realm in SamsaraConstants.Realms)
            {
                var entry = progress == null ? null : progress[realm] as JObject;
                if (!SamsaraJson.GetBool(entry, "completed", false)) return false;
            }
            return true;
        }

        public bool AllMemoryFragmentsCollected()
        {
            var frags = GetMemoryFragmentsUnlocked();
            foreach (var realm in SamsaraConstants.Realms)
            {
                if (!SamsaraJson.GetBool(frags, realm, false)) return false;
            }
            return true;
        }

        /// <summary>是否应触发识破路径（六道通关 + 祈求 ≥ 1 次）。</summary>
        public bool CheckExposurePath()
        {
            if (!AllRealmsCompleted()) return false;
            return GetPrayerCount() >= 1;
        }

        public void IncrementPlaythrough()
        {
            _data["playthrough_count"] = GetInt("playthrough_count", 1) + 1;
            Save();
        }

        // ══════════════════════════════════════════════════════════
        // 存档重置（state.py reset_all：软档 / 硬档）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 重置存档。full=false 为软档（保留技能/技能点/结局/记忆碎片/沙盒），
        /// full=true 为硬档（全部默认化，仅保留 version，旧档备份为 .bak）。
        /// </summary>
        public void ResetAll(bool full = false)
        {
            try
            {
                if (!string.IsNullOrEmpty(_savePath) && File.Exists(_savePath))
                    File.WriteAllBytes(_savePath + ".bak", File.ReadAllBytes(_savePath));
            }
            catch (Exception)
            {
                // 备份失败不应阻止重置。
            }

            if (full)
            {
                var version = GetInt("version", 3);
                _data = new JObject();
                _data["version"] = version;
                InitDefaults();
                ResetLevelState();
                Save();
                return;
            }

            var keepSkills = SamsaraJson.Clone(_data["skills"] as JObject) ?? new JObject();
            var keepSkillPoints = GetInt("skill_points", 0);
            var keepKarmaMax = GetInt("karma_max", 120);
            var keepKarmaSingleMax = GetInt("karma_single_max", 120);
            var keepInitialKarma = GetInt("initial_karma", 50);
            var keepEndings = SamsaraJson.Clone(_data["endings_unlocked"] as JObject) ?? DefaultEndingsUnlocked();
            var keepFrags = SamsaraJson.Clone(_data["memory_fragments_unlocked"] as JObject) ?? DefaultMemoryFragments();
            var keepSandbox = (_data["sandbox_unlocked"] as JArray ?? new JArray()).DeepClone();
            var keepPlaythrough = GetInt("playthrough_count", 1);
            var version2 = GetInt("version", 3);

            _data = new JObject();
            _data["version"] = version2;
            InitDefaults();

            _data["skill_points"] = keepSkillPoints;
            _data["karma_max"] = keepKarmaMax;
            _data["karma_single_max"] = keepKarmaSingleMax;
            _data["initial_karma"] = keepInitialKarma;
            _data["endings_unlocked"] = keepEndings;
            _data["memory_fragments_unlocked"] = keepFrags;
            _data["sandbox_unlocked"] = keepSandbox;

            var mergedSkills = SamsaraJson.Clone(_data["skills"] as JObject) ?? new JObject();
            foreach (var prop in keepSkills.Properties()) mergedSkills[prop.Name] = prop.Value.DeepClone();
            _data["skills"] = mergedSkills;

            _data["current_realm"] = "hell";
            _data["current_level"] = 0;
            _data["realm_overshoot_carryover"] = 0;
            _data["playthrough_count"] = keepPlaythrough + 1;
            _data["alignment"] = DefaultAlignment();
            _data["detection_state"] = DefaultDetectionState();
            var realmDetections = new JObject();
            foreach (var realm in SamsaraConstants.Realms) realmDetections[realm] = 0.0;
            _data["realm_detections"] = realmDetections;
            _data["choices_made"] = new JArray();
            _data["total_levels_completed"] = 0;
            _data["bosses_defeated"] = new JArray();
            _data["prayer_count"] = 0;
            _data["tiandao_boss_state"] = DefaultTiandaoBossState();
            _data["story_progress"] = DefaultStoryProgress();
            _data["realm_progress"] = DefaultRealmProgress();
            ResetLevelState();
            Save();
        }

        // ══════════════════════════════════════════════════════════
        // 前端状态（state.py get_frontend_state）
        // ══════════════════════════════════════════════════════════

        /// <summary>返回前端（总坛/棋类）共同需要的精简状态字段。</summary>
        public JObject GetFrontendState()
        {
            var modifiers = GetSkillModifiers();
            var allowed = new List<string>(GetAllowedClassifications());
            allowed.Sort(StringComparer.Ordinal);
            var allowedArray = new JArray();
            foreach (var item in allowed) allowedArray.Add(item);

            var realmDetections = new JObject();
            foreach (var realmKey in SamsaraConstants.Realms) realmDetections[realmKey] = GetRealmDetection(realmKey);

            var realm = GetString("current_realm", "hell");

            var result = new JObject();
            result["karma"] = GetKarma();
            result["karma_max"] = GetInt("karma_max", 120) + SamsaraJson.GetInt(modifiers, "karma_max_bonus", 0);
            result["karma_single_max"] = GetInt("karma_single_max", 120) + SamsaraJson.GetInt(modifiers, "karma_single_max_bonus", 0);
            result["detection"] = GetDetection();
            result["realm_detections"] = realmDetections;
            result["current_realm"] = realm;
            result["current_realm_name"] = SamsaraConstants.RealmName(realm) == "" ? "地狱道" : SamsaraConstants.RealmName(realm);
            result["current_level"] = GetInt("current_level", 0);
            result["level_turn_limit"] = GetInt("turn_limit", 20);
            result["current_turn"] = GetInt("current_turn", 0);
            result["total_levels_completed"] = GetInt("total_levels_completed", 0);
            result["skill_points"] = GetInt("skill_points", 0);
            result["no_cheat_this_level"] = GetBool("no_cheat_this_level", true);
            result["cheat_count"] = GetInt("cheat_count", 0);
            result["overdraft_count"] = GetInt("overdraft_count", 0);
            result["sandbox_mode"] = GetBool("sandbox_mode", false);
            result["allowed_classifications"] = allowedArray;
            result["skill_modifiers"] = modifiers;
            result["alignment"] = GetAlignment();
            result["detection_state"] = GetDetectionState();
            result["memory_fragments_unlocked"] = GetMemoryFragmentsUnlocked();
            result["endings_unlocked"] = GetEndingsUnlocked();
            result["realm_progress"] = _data["realm_progress"] as JObject ?? new JObject();
            result["sandbox_unlocked"] = _data["sandbox_unlocked"] as JArray ?? new JArray();
            result["prayer_count"] = GetPrayerCount();
            result["version"] = GetInt("version", 3);
            return result;
        }
    }
}