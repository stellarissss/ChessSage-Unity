using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 六道轮回引擎（供 Unity 表现层调用的统一入口）。
    /// 装配各子系统（业力 / 识破 / 技能 / 关卡 / 进度 / 结局 / 目标 / 回合 / 记忆碎片 /
    /// 守道者 / 选择 / 天道 Boss），并代理所需的状态读写 API。
    /// 全部状态以 JObject 持久化，键名保持 snake_case。
    /// </summary>
    public sealed class SamsaraEngine
    {
        private readonly string _globalConfigDir;

        /// <summary>
        /// 构造引擎：从 globalConfigDir 读取 global/*.json 真源，
        /// 并从 savePath 载入存档（不存在则用 samsara_state.json 模板新建）。
        /// </summary>
        public SamsaraEngine(string globalConfigDir, string savePath = null)
        {
            _globalConfigDir = globalConfigDir ?? string.Empty;

            State = new SamsaraState(_globalConfigDir, savePath);
            Karma = new KarmaSystem(State, P("karma_events.json"));
            Assessor = new KarmaAssessor();
            Detection = new DetectionModel(State, P("story.json"));
            Skills = new SkillSystem(State, P("skill_tree.json"));
            Levels = new LevelSystem(State, P("level_pools.json"), P("puzzles.json"));
            Progression = new ProgressionSystem(State);
            Endings = new EndingSystem(State, P("story.json"));
            Objectives = new ObjectiveSystem(State, P("objective_types.json"));
            TurnLimit = new TurnLimitSystem(State);
            MemoryFragments = new MemoryFragmentSystem(State, P("story.json"));
            Bosses = new BossSystem(State, P("boss_definitions.json"), P("story.json"));
            Choices = new ChoiceSystem(State, P("story.json"));
            HeavenBoss = new HeavenBossSystem(State, P("tiandao_boss.json"), P("story.json"));
        }

        private string P(string fileName)
        {
            return Path.Combine(_globalConfigDir, fileName);
        }

        // ══════════════════════════════════════════════════════════
        // 子系统（表现层可直接访问完整能力）
        // ══════════════════════════════════════════════════════════

        public SamsaraState State { get; private set; }
        public KarmaSystem Karma { get; private set; }
        public KarmaAssessor Assessor { get; private set; }
        public DetectionModel Detection { get; private set; }
        public SkillSystem Skills { get; private set; }
        public LevelSystem Levels { get; private set; }
        public ProgressionSystem Progression { get; private set; }
        public EndingSystem Endings { get; private set; }
        public ObjectiveSystem Objectives { get; private set; }
        public TurnLimitSystem TurnLimit { get; private set; }
        public MemoryFragmentSystem MemoryFragments { get; private set; }
        public BossSystem Bosses { get; private set; }
        public ChoiceSystem Choices { get; private set; }
        public HeavenBossSystem HeavenBoss { get; private set; }

        // ══════════════════════════════════════════════════════════
        // 状态与持久化
        // ══════════════════════════════════════════════════════════

        /// <summary>前端精简状态（等价 samsara /api/state 的响应结构）。</summary>
        public JObject GetState()
        {
            return State.GetFrontendState();
        }

        /// <summary>完整持久化状态（state.py get_full_state 的深拷贝）。</summary>
        public JObject GetFullState()
        {
            return State.GetFullState();
        }

        public void Save()
        {
            State.Save();
        }

        public void Load()
        {
            State.Load();
        }

        // ══════════════════════════════════════════════════════════
        // 业力
        // ══════════════════════════════════════════════════════════

        /// <summary>增加业力，返回 (实际增加量, 是否溢出, 溢出量)。</summary>
        public Tuple<int, bool, double> IncreaseKarma(int amount)
        {
            return State.IncreaseKarma(amount);
        }

        /// <summary>减少业力（最小为 0），返回实际减少量。</summary>
        public int DecreaseKarma(int amount)
        {
            return State.DecreaseKarma(amount);
        }

        /// <summary>退还业力（作弊失败时全额退还）。</summary>
        public void RefundKarma(int amount)
        {
            State.RefundKarma(amount);
        }

        public int GetKarma()
        {
            return State.GetKarma();
        }

        public int GetKarmaMax()
        {
            return State.GetKarmaMax();
        }

        public int GetKarmaSingleMax()
        {
            return State.GetKarmaSingleMax();
        }

        // ══════════════════════════════════════════════════════════
        // 识破
        // ══════════════════════════════════════════════════════════

        public void SetDetection(double value)
        {
            State.SetDetection(value);
        }

        public double GetDetection()
        {
            return State.GetDetection();
        }

        public void IncrementDetection(double delta)
        {
            State.IncrementDetection(delta);
        }

        public double GetRealmDetection(string realm)
        {
            return State.GetRealmDetection(realm);
        }

        public bool IsDetectionLocked()
        {
            return State.IsDetectionLocked();
        }

        public void SetRealmDetection(string realm, double value)
        {
            State.SetRealmDetection(realm, value);
        }

        // ══════════════════════════════════════════════════════════
        // 技能
        // ══════════════════════════════════════════════════════════

        public int GetSkillPoints()
        {
            return State.GetSkillPoints();
        }

        public bool UnlockSkill(string skillId, int tier)
        {
            return Skills.UnlockSkill(skillId, tier);
        }

        public bool SpendSkillPoint(int count = 1)
        {
            return State.SpendSkillPoint(count);
        }

        public JObject GetSkillModifiers()
        {
            return State.GetSkillModifiers();
        }

        public bool ConsumeOneTimeSkill(string skillId, string context = "")
        {
            return State.ConsumeOneTimeSkill(skillId, context);
        }

        // ══════════════════════════════════════════════════════════
        // 关卡 / 进度
        // ══════════════════════════════════════════════════════════

        public string GetCurrentRealm()
        {
            return State.GetCurrentRealm();
        }

        public int GetCurrentLevel()
        {
            return State.GetCurrentLevel();
        }

        public void AdvanceLevel()
        {
            State.AdvanceLevel();
        }

        /// <summary>关卡结束：计算本局业力溢出并叠加到本道下一局，返回溢出量。</summary>
        public int RecordLevelEnd()
        {
            return State.RecordLevelEnd();
        }

        public void MarkRealmCompleted(string realm)
        {
            State.MarkRealmCompleted(realm);
        }

        public void MarkBossDefeated(string bossId)
        {
            State.MarkBossDefeated(bossId);
        }

        public bool IsBossDefeated(string bossId)
        {
            return State.IsBossDefeated(bossId);
        }

        public void ResetLevelState()
        {
            State.ResetLevelState();
        }

        public JObject GetLevelPool(string realm)
        {
            return Levels.GetLevelPool(realm);
        }

        public JObject GetCurrentLevelConfig()
        {
            return Levels.GetCurrentLevel();
        }

        // ══════════════════════════════════════════════════════════
        // 结局
        // ══════════════════════════════════════════════════════════

        /// <summary>综合判定结局，返回 {ending_id, ending_data, reason}。</summary>
        public JObject EvaluateEnding(string finalChoice = null, bool noCheatFinal = true)
        {
            return Endings.DetermineEnding(finalChoice, noCheatFinal);
        }

        /// <summary>仅返回判定出的结局 id（无法判定时返回 null）。</summary>
        public string EvaluateEndingId(string finalChoice = null, bool noCheatFinal = true)
        {
            var result = EvaluateEnding(finalChoice, noCheatFinal);
            return SamsaraJson.GetString(result, "ending_id", null);
        }

        // ══════════════════════════════════════════════════════════
        // 目标
        // ══════════════════════════════════════════════════════════

        /// <summary>当前关卡目标（缺省 checkmate）。</summary>
        public JObject GetCurrentObjective()
        {
            var level = Levels.GetCurrentLevel();
            var objective = level == null ? null : level["objective"] as JObject;
            return objective ?? new JObject { ["type"] = "checkmate" };
        }

        /// <summary>校验当前目标是否达成。</summary>
        public bool CheckObjective(JObject boardState)
        {
            var result = Objectives.Check(GetCurrentObjective(), boardState);
            return SamsaraJson.GetBool(result, "completed", false);
        }

        /// <summary>校验当前目标，返回完整结果 {completed, progress, message, ...}。</summary>
        public JObject CheckObjectiveDetail(JObject boardState)
        {
            return Objectives.Check(GetCurrentObjective(), boardState);
        }

        /// <summary>记录当前关卡目标完成状态（目标系统无持久化，完成记录写入 story_progress）。</summary>
        public void CompleteObjective()
        {
            var objective = GetCurrentObjective();
            State.UpdateStoryProgress(new JObject
            {
                ["last_objective_completed"] = SamsaraJson.GetString(objective, "type", "checkmate"),
            });
        }

        // ══════════════════════════════════════════════════════════
        // 回合上限
        // ══════════════════════════════════════════════════════════

        public void IncrementTurn()
        {
            State.IncrementTurn();
        }

        public void ResetTurn()
        {
            State.ResetTurn();
        }

        public int GetTurnLimit()
        {
            return State.GetTurnLimit();
        }

        public bool IsTurnLimitExceeded()
        {
            return TurnLimit.IsOver();
        }

        // ══════════════════════════════════════════════════════════
        // 记忆碎片
        // ══════════════════════════════════════════════════════════

        /// <summary>全部 6 道记忆碎片状态。</summary>
        public JArray GetMemoryFragments()
        {
            return MemoryFragments.GetAllFragmentsStatus();
        }

        /// <summary>尝试解锁某道记忆碎片，返回是否解锁成功。</summary>
        public bool UnlockMemoryFragment(string realm)
        {
            var result = MemoryFragments.TryUnlock(realm);
            return SamsaraJson.GetBool(result, "success", false);
        }

        /// <summary>尝试解锁某道记忆碎片，返回详情 {success, already_unlocked, fragment, reason}。</summary>
        public JObject TryUnlockMemoryFragment(string realm)
        {
            return MemoryFragments.TryUnlock(realm);
        }

        // ══════════════════════════════════════════════════════════
        // 业力齐平 / alignment
        // ══════════════════════════════════════════════════════════

        public JObject GetAlignment()
        {
            return State.GetAlignment();
        }

        public void AddAlignment(JObject effect)
        {
            State.AddAlignment(effect);
        }

        // ══════════════════════════════════════════════════════════
        // 记录
        // ══════════════════════════════════════════════════════════

        public void RecordCheat()
        {
            State.RecordCheat();
        }

        public void RecordOverdraft()
        {
            State.RecordOverdraft();
        }

        public void RecordChoice(JObject choiceRecord)
        {
            State.RecordChoice(choiceRecord);
        }

        public JArray GetChoicesMade()
        {
            return State.GetChoicesMade();
        }

        // ══════════════════════════════════════════════════════════
        // 沙盒
        // ══════════════════════════════════════════════════════════

        public bool IsSandboxUnlocked(string realm)
        {
            return State.IsSandboxUnlocked(realm);
        }

        public void UnlockSandbox(string realm)
        {
            State.UnlockSandbox(realm);
        }

        public bool IsSandboxMode()
        {
            return State.IsSandboxMode();
        }

        public void SetSandboxMode(bool enabled)
        {
            State.SetSandboxMode(enabled);
        }

        /// <summary>当前技能树解锁的作弊分类集合（基础 E/F/A/B/C 恒可用）。</summary>
        public HashSet<string> GetAllowedClassifications()
        {
            return State.GetAllowedClassifications();
        }
    }
}