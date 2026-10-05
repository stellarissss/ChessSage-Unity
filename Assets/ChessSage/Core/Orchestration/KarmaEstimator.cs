using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>
    /// 编排层业力评估器。对齐 legacy-web/shared/karma_assessor_base.py：
    /// 在意图解析并行阶段用 LLM 预估「业障」值（1~120），并维护本地业力/识破状态。
    ///
    /// 说明：Core 内另有 <c>ChessSage.Core.Samsara.KarmaAssessor</c>（Samsara 服务端镜像的
    /// 关卡业力状态机）；本类聚焦编排阶段所需的 LLM 评估与单次上限读取，命名刻意区分以避免歧义。
    /// </summary>
    public sealed class KarmaEstimator
    {
        // ── prompt 模板（公共头部 + 象棋示例 + 局势调整 + 尾部）──

        const string PromptHead = @"
你是一个业力评估AI。你的任务是评估玩家作弊指令产生的""业障""值（业力增加量）。

## 背景
- 业力 = 玩家作弊产生的业障，初始为50，安全阈值为120
- 业力 ≤ 120 时安全；超出120的部分会非线性增加识破概率
- 超出越多，识破概率增长越快（非线性）
- 作弊越强力，业障越重
- 下棋事件（吃子/将军/三连等）会减少业力（消业）

## 当前状态
棋类: {game_type}
作弊指令: ""{instruction}""
意图分类: {intent_class}
当前局势: {board_summary}
当前业力: {karma}/{max_karma}（越接近上限越危险）
单次增加上限: {max_single}（超出此值的指令将被直接拦截，不予执行）

## 评估标准（必须严格遵守）

### 基础分类价目表
- E 类（聊天/搞笑）：1 点（固定）
- D 类（界面修改/外观）：8-23 点
- A 类（机制修改）：30-60 点
- B 类（棋盘变换/棋子位置）：23-53 点
- C 类（规则修改/棋子走法）：45-90 点
- C+ 类（创建新棋子）：75-120 点

### 强度倍数（乘以基础价）
- 改 1 个棋子/1 条规则：×1.0
- 改 2 个棋子/2 条规则：×2.0
- 改 3 个及以上：×3.0
";

        const string ExamplesText = @"
### 象棋具体示例（必须参考）
- ""把我的马变成炮""：53 点（C 类 ×1.0）
";

        const string PromptAdjust = @"
### 局势调整
- 玩家大优时（优势 >50%）：×1.2（更重）
- 玩家劣势时（优势 <30%）：×0.9（稍轻）
";

        const string PromptTail = @"
### 边界约束（绝对不可违反）
- 最低 1 点（即使评估为 0 或负数，也必须输出 1）
- 最高 120 点（即单次上限；即使评估超过 120，也必须输出 120）

输出一个整数，不要任何解释。
";

        const string ExtraPrompt = "";

        /// <summary>棋类标识（日志 / 缓存键 / prompt 占位）。</summary>
        public string GameType;

        readonly LlmClient _client;
        readonly Dictionary<string, int> _cache = new Dictionary<string, int>();
        readonly Random _random = new Random();

        int _localKarma = 50;
        int _localKarmaMax = 120;
        int _localKarmaSingleMax = 120;
        readonly int _initialKarma = 50;
        double _realmDetection;
        string _currentRealm = "human";

        /// <summary>一次性技能（stealth_t2a / stealth_t3a）本关已消耗记录。</summary>
        readonly HashSet<string> _usedOneTimeSkills = new HashSet<string>();

        public KarmaEstimator(LlmClient client = null, string gameType = "xiangqi")
        {
            _client = client ?? new LlmClient();
            GameType = string.IsNullOrEmpty(gameType) ? "xiangqi" : gameType;
        }

        /// <summary>设置 API 密钥（同步到 LLM 客户端）。</summary>
        public void SetApiKey(string apiKey)
        {
            if (_client != null) _client.ApiKey = apiKey;
        }

        /// <summary>设置棋类标识。</summary>
        public void SetGameType(string gameType)
        {
            GameType = gameType;
        }

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

        public int GetLocalKarma() => _localKarma;
        public int GetLocalKarmaMax() => _localKarmaMax;
        public double GetRealmDetection() => _realmDetection;

        /// <summary>单次业力上限（业力门控读取此值判断是否拦截）。</summary>
        public int LocalKarmaSingleMax => _localKarmaSingleMax;

        string FullPromptTemplate() => PromptHead + ExamplesText + PromptAdjust + ExtraPrompt + PromptTail;

        string BuildPrompt(string instruction, string intentClass, string boardSummary)
        {
            // 先替换其余占位符，最后替换 {instruction}，避免指令正文中的花括号被二次替换。
            var template = FullPromptTemplate();
            template = template.Replace("{game_type}", GameType ?? string.Empty);
            template = template.Replace("{intent_class}", intentClass ?? string.Empty);
            template = template.Replace("{board_summary}", boardSummary ?? string.Empty);
            template = template.Replace("{karma}", _localKarma.ToString(CultureInfo.InvariantCulture));
            template = template.Replace("{max_karma}", _localKarmaMax.ToString(CultureInfo.InvariantCulture));
            template = template.Replace("{max_single}", _localKarmaSingleMax.ToString(CultureInfo.InvariantCulture));
            template = template.Replace("{instruction}", instruction ?? string.Empty);
            return template;
        }

        /// <summary>
        /// 评估业力增加量（1~120）。与意图解析并行调用，不依赖 Samsara API。
        /// E 类固定 1 点；无 API Key 或调用失败时走 fallback 价目表。
        /// </summary>
        public async Task<int> AssessAsync(
            string instruction,
            string intentClass,
            string boardSummary,
            JObject skillModifiers = null,
            CancellationToken cancellationToken = default)
        {
            if (skillModifiers == null) skillModifiers = new JObject();

            if (intentClass == Classification.E) return 1;

            var cacheKey = $"{GameType}:{instruction}:{intentClass}";
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

            int amount;
            if (!_client.IsAvailable)
            {
                amount = FallbackAssess(intentClass);
            }
            else
            {
                var prompt = BuildPrompt(instruction, intentClass, boardSummary);
                var result = await _client.ChatUserOnlyAsync(
                    prompt,
                    temperature: 0.3,
                    maxTokens: 512,
                    noThink: true,
                    timeoutSeconds: 30.0,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                amount = result.Ok && int.TryParse((result.Content ?? string.Empty).Trim(), out var parsed)
                    ? parsed
                    : FallbackAssess(intentClass);
            }

            // 技能折扣 / 道级折扣（与原文一致）
            if (JsonHelpers.GetBool(skillModifiers, "efficiency_fraud", false) && ShouldDiscount())
                amount = (int)(amount * 0.7);

            if ((_currentRealm == "hell" || _currentRealm == "hungry")
                && JsonHelpers.GetBool(skillModifiers, "hell_hungry_discount", false))
                amount = (int)(amount * 0.75);

            if ((_currentRealm == "heaven" || _currentRealm == "asura")
                && JsonHelpers.GetBool(skillModifiers, "heaven_asura_discount", false))
                amount = (int)(amount * 0.75);

            amount = PostAssessHook(amount, intentClass, skillModifiers);

            amount = Math.Max(1, Math.Min(amount, 120));
            _cache[cacheKey] = amount;
            return amount;
        }

        /// <summary>棋类专属业力调整钩子（默认无操作，对应 Python _post_assess_hook）。</summary>
        int PostAssessHook(int amount, string intentClass, JObject skillModifiers) => amount;

        /// <summary>无 LLM 时的价目表兜底。</summary>
        public int FallbackAssess(string intentClass)
        {
            switch (intentClass)
            {
                case "E": return 1;
                case "D": return 10;
                case "A": return 30;
                case "B": return 25;
                case "C": return 40;
                case "C+": return 80;
                default: return 30;
            }
        }

        bool ShouldDiscount() => _random.NextDouble() < 0.3;

        /// <summary>
        /// 增加业力（作弊产生业障）。返回 {actual_increased, is_overdraft,
        /// overdraft_amount, new_karma, success}；超出单次上限时 success=false 且不改变业力。
        /// </summary>
        public JObject IncreaseKarma(int amount, JObject skillModifiers = null)
        {
            if (skillModifiers == null) skillModifiers = new JObject();

            var maxSingle = _localKarmaSingleMax + JsonHelpers.GetInt(skillModifiers, "karma_single_max_bonus", 0);
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

            var threshold = _localKarmaMax + JsonHelpers.GetInt(skillModifiers, "karma_max_bonus", 0);
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

        /// <summary>减少业力（下棋消业）。返回实际减少量；最小为 0。</summary>
        public int DecreaseKarma(int amount, JObject skillModifiers = null)
        {
            if (skillModifiers == null) skillModifiers = new JObject();
            var multiplier = JsonHelpers.GetDouble(skillModifiers, "karma_recover_multiplier", 1.0);
            var actual = (int)(amount * multiplier);
            var old = _localKarma;
            _localKarma = Math.Max(0, _localKarma - actual);
            return old - _localKarma;
        }

        /// <summary>退还业力（作弊失败时全额退还）。1:1 退还，无加成。</summary>
        public void RefundKarma(int amount, JObject skillModifiers = null)
        {
            _localKarma = Math.Max(0, _localKarma - amount);
        }

        /// <summary>向后兼容别名。</summary>
        public JObject ConsumeKarma(int amount, JObject skillModifiers = null) => IncreaseKarma(amount, skillModifiers);

        /// <summary>向后兼容别名。</summary>
        public int RecoverKarma(int amount, JObject skillModifiers = null) => DecreaseKarma(amount, skillModifiers);

        /// <summary>计算识破概率增长 Δ = C × O^α（C 默认 0.1，α 默认 1.5）。</summary>
        public double CalculateDetectionDelta(double overshootAmount, JObject skillModifiers = null)
        {
            if (skillModifiers == null) skillModifiers = new JObject();
            if (overshootAmount <= 0) return 0.0;

            var c = JsonHelpers.GetDouble(skillModifiers, "detection_coefficient", 0.1);
            var alpha = JsonHelpers.GetDouble(skillModifiers, "detection_alpha", 1.5);
            var delta = c * Math.Pow(overshootAmount, alpha);

            if (JsonHelpers.GetBool(skillModifiers, "mist_fog", false) && _realmDetection > 70)
            {
                if (_random.NextDouble() < 0.3) return 0.0;
            }
            return delta;
        }

        /// <summary>
        /// 处理业力超阈值，更新识破概率并做概率结算。
        /// 返回 {detected, delta, current, escaped?, reset?, message?, skip?}。
        /// </summary>
        public JObject HandleOverdraft(double overshootAmount, JObject skillModifiers = null)
        {
            if (skillModifiers == null) skillModifiers = new JObject();

            if (overshootAmount <= 0)
            {
                return new JObject
                {
                    ["detected"] = false,
                    ["delta"] = 0.0,
                    ["current"] = _realmDetection,
                };
            }

            if (JsonHelpers.GetBool(skillModifiers, "first_overdraft_skip", false)
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

            var delta = CalculateDetectionDelta(overshootAmount, skillModifiers);
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

            var roll = _random.NextDouble() * 100;
            var detected = roll < current;

            if (detected)
            {
                if (JsonHelpers.GetBool(skillModifiers, "golden_escape", false)
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
                ["detected"] = detected,
                ["delta"] = delta,
                ["current"] = current,
            };
        }

        /// <summary>
        /// 重置关卡业力（每个关卡开始时调用）。
        /// 业力 = max(0, 初始值 - 净身减免) + 本道溢出叠加。
        /// </summary>
        public void ResetLevelKarma(JObject skillModifiers = null, int carryover = 0)
        {
            if (skillModifiers == null) skillModifiers = new JObject();
            var reduction = JsonHelpers.GetInt(skillModifiers, "initial_karma_reduction", 0);
            var baseKarma = Math.Max(0, _initialKarma - reduction);
            _localKarma = baseKarma + Math.Max(0, carryover);
            _cache.Clear();
            _usedOneTimeSkills.Clear();
        }

        /// <summary>获取当前业力和识破状态（键名与 Python get_state 一致）。</summary>
        public JObject GetState(JObject skillModifiers = null)
        {
            if (skillModifiers == null) skillModifiers = new JObject();
            return new JObject
            {
                ["karma"] = new JObject
                {
                    ["current"] = _localKarma,
                    ["max"] = _localKarmaMax + JsonHelpers.GetInt(skillModifiers, "karma_max_bonus", 0),
                    ["single_max"] = _localKarmaSingleMax + JsonHelpers.GetInt(skillModifiers, "karma_single_max_bonus", 0),
                    ["initial"] = Math.Max(0, _initialKarma - JsonHelpers.GetInt(skillModifiers, "initial_karma_reduction", 0)),
                },
                ["detection"] = _realmDetection,
                ["realm"] = _currentRealm,
            };
        }
    }
}