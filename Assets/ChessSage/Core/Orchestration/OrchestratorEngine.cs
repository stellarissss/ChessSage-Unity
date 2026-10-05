using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>对话日志记录器（对齐 Python ConversationLog）。</summary>
    public sealed class ConversationLog
    {
        readonly List<JObject> _logs = new List<JObject>();
        readonly int _maxLogs = 50;

        public int Count => _logs.Count;

        /// <summary>添加日志条目，自动补 timestamp 并裁剪到最近 50 条。</summary>
        public void AddLog(JObject logEntry)
        {
            if (logEntry == null) return;
            logEntry["timestamp"] = DateTime.Now.ToString("o");
            _logs.Add(logEntry);
            if (_logs.Count > _maxLogs) _logs.RemoveAt(0);
        }

        /// <summary>获取最近 N 条日志。</summary>
        public List<JObject> GetLogs(int count = 10)
        {
            if (count < 0) count = 0;
            var start = Math.Max(0, _logs.Count - count);
            return _logs.GetRange(start, _logs.Count - start);
        }

        /// <summary>获取最后一条日志。</summary>
        public JObject GetLastLog() => _logs.Count > 0 ? _logs[_logs.Count - 1] : null;

        /// <summary>清空日志。</summary>
        public void Clear() => _logs.Clear();
    }

    /// <summary>
    /// AI 编排引擎（顶层 RPG 模式）。忠实移植 legacy-web/shared/ai_orchestrator_base.py 的
    /// AIOrchestratorBase + RPGOrchestratorBase：
    ///
    /// 玩家自然语言 → 意图解析(LLM) + 业力评估(并行) → 分类规整 → 业力门控
    /// → （A/B/C/C+/D/D2）分派 CodeAI 生成 JSON Patch → Schema 硬校验 + 重试
    /// → 应用/合并到 board_state / pieces_* / rules / ui_config / board → 返回结果。
    ///
    /// 结果字段与 Python 保持一致：success / type / message / classification /
    /// estimated_karma_cost / modified_configs / karma_blocked 等。
    /// </summary>
    public sealed partial class OrchestratorEngine
    {
        // ── 棋类身份 ──
        public string GameType = "xiangqi";

        // ── side 命名体系 ──
        public string DefaultTurn = "red";
        public readonly Dictionary<string, string> SideToggle = new Dictionary<string, string>
        {
            ["red"] = "black",
            ["black"] = "red",
        };
        public string WinnerDefault = "red";
        public readonly Dictionary<string, string> SideLabels = new Dictionary<string, string>
        {
            ["red"] = "红方",
            ["black"] = "黑方",
        };
        public readonly string[] BoardSummaryOrder = { "red", "black" };
        public readonly (string configName, string label)[] SideConfigMap =
        {
            ("pieces_red", "红方"),
            ("pieces_black", "黑方"),
        };

        // ── 棋子类型体系 ──
        public static readonly Dictionary<string, string> PieceTypeMapping = new Dictionary<string, string>
        {
            ["pawn"] = "soldier",
            ["rook"] = "chariot",
            ["knight"] = "horse",
            ["bishop"] = "elephant",
            ["queen"] = "advisor",
            ["king"] = "general",
        };

        public static readonly HashSet<string> CpPredefinedTypes = new HashSet<string>
        {
            "chariot", "horse", "elephant", "advisor", "general", "cannon", "soldier",
        };

        public static readonly string[] A2ActionNames =
        {
            "modify_personality", "add_mechanism", "set_ai_personality",
            "freeze_ai", "ai_takeover", "random_move",
            "skip_turn", "extra_turn", "modify_mechanism", "player_control",
        };

        public static readonly string[] A2BoardStateActions =
        {
            "add_mechanism", "freeze_ai", "ai_takeover", "random_move",
            "skip_turn", "extra_turn", "modify_mechanism", "player_control",
        };

        // ── 移动原语体系 ──
        public static readonly string[] CpPrimitives = { "jump", "ray" };
        public static readonly string CpPrimitivesDesc = "jump/ray";
        public static readonly string CpPrimitivesPhrase = "jump/ray 原语";
        public static readonly (int w, int h) CpDefaultBounds = (9, 10);
        public static readonly string CpBoardDesc = "- 棋盘尺寸: 9 x 10";
        public static readonly string CpRangeErrorFmt = "(0-{0}, 0-{1})";

        public static readonly string[] CActionKeywords =
        {
            "modify_rule", "change_move", "add_ability", "create_custom_piece",
            "alter_movement", "modify_piece", "change_rule", "update_rule",
            "add_move", "remove_move", "change_capture", "modify_screens",
        };

        public static readonly string[] RuleChangeConfigNames = { "pieces" };

        // mechanisms 字段内各原语均为「列表」，并行 action 各自追加时应做并集而非覆盖。
        static readonly string[] MechanismListKeys =
        {
            "skip_turns", "ai_control", "random_moves",
            "extra_turns", "move_limits", "player_control",
        };

        // ── 运行态 ──
        public readonly LlmClient Client;
        public readonly KarmaEstimator KarmaAssessor;
        public string BaseDir;
        public bool CurrentThinking;
        public string ThinkingStage = "";
        public JObject TokenStats;

        readonly ConversationLog _logger = new ConversationLog();
        readonly Regex _jsonBlockRe = new Regex(@"```(?:json)?\s*\n?([\s\S]*?)\n?```", RegexOptions.Compiled);
        readonly object _tokenLock = new object();

        public OrchestratorEngine(LlmClient client = null, string baseDir = null)
        {
            Client = client ?? new LlmClient();
            KarmaAssessor = new KarmaEstimator(Client, GameType);
            BaseDir = baseDir;
            TokenStats = new JObject
            {
                ["total_prompt_tokens"] = 0,
                ["total_completion_tokens"] = 0,
                ["total_calls"] = 0,
                ["daily_stats"] = new JObject(),
                ["token_price_per_1k"] = 0.0015,
            };
            Client.TokenUsageCallback = RecordTokenUsage;
        }

        /// <summary>设置 API 密钥（同步到业力评估器）。</summary>
        public void SetApiKey(string apiKey)
        {
            Client.ApiKey = apiKey;
            KarmaAssessor.SetApiKey(apiKey);
        }

        /// <summary>获取最近 N 条日志。</summary>
        public List<JObject> GetLogs(int count = 10) => _logger.GetLogs(count);

        /// <summary>获取当前思考状态。</summary>
        public JObject GetThinkingStatus()
        {
            return new JObject { ["thinking"] = CurrentThinking, ["stage"] = ThinkingStage };
        }

        /// <summary>获取 token 消耗统计。</summary>
        public JObject GetTokenStats()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var dailyStats = TokenStats["daily_stats"] as JObject;
            var todayStats = dailyStats?[today] as JObject ?? new JObject();

            long totalPrompt = JsonHelpers.GetInt(TokenStats, "total_prompt_tokens");
            long totalCompletion = JsonHelpers.GetInt(TokenStats, "total_completion_tokens");
            long totalTokens = totalPrompt + totalCompletion;
            long todayPrompt = JsonHelpers.GetInt(todayStats, "prompt_tokens");
            long todayCompletion = JsonHelpers.GetInt(todayStats, "completion_tokens");
            long todayTokens = todayPrompt + todayCompletion;
            double pricePer1k = JsonHelpers.GetDouble(TokenStats, "token_price_per_1k", 0.0015);

            return new JObject
            {
                ["total_prompt_tokens"] = totalPrompt,
                ["total_completion_tokens"] = totalCompletion,
                ["total_tokens"] = totalTokens,
                ["total_calls"] = JsonHelpers.GetInt(TokenStats, "total_calls"),
                ["today_prompt_tokens"] = todayPrompt,
                ["today_completion_tokens"] = todayCompletion,
                ["today_tokens"] = todayTokens,
                ["today_calls"] = JsonHelpers.GetInt(todayStats, "calls"),
                ["estimated_cost_usd"] = Math.Round(totalTokens * pricePer1k / 1000, 4),
                ["today_cost_usd"] = Math.Round(todayTokens * pricePer1k / 1000, 4),
                ["token_price_per_1k_usd"] = pricePer1k,
            };
        }

        // ══════════════════════════════════════════════════════════════
        // 主流程：process_command
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 处理玩家指令的主流程。
        /// 返回 JObject：success / type(applied|partial|success|rejected|fun|error) /
        /// message / classification / modified_configs / estimated_karma_cost / karma_blocked 等。
        /// </summary>
        public async Task<JObject> ProcessCommandAsync(
            string command, JObject context, CancellationToken cancellationToken = default)
        {
            if (context == null) context = new JObject();
            if (!Client.IsAvailable)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "未设置API密钥，请先在设置中输入DeepSeek API Key",
                };
            }

            CurrentThinking = true;
            var logEntry = new JObject
            {
                ["user_input"] = command,
                ["stage"] = "started",
                ["intent_analysis"] = new JObject(),
                ["code_generation"] = new JObject(),
                ["final_result"] = new JObject(),
                ["errors"] = new JArray(),
            };

            var configs = context["configs"] as JObject ?? new JObject();

            ThinkingStage = "intent";

            JObject intent = null;
            double intentTime = 0;
            string intentRaw = null;
            JObject karmaState = null;
            try
            {
                var phase = await IntentAndKarmaPhaseAsync(command, context, logEntry, cancellationToken)
                    .ConfigureAwait(false);
                intent = phase.intent;
                intentTime = phase.intentTime;
                intentRaw = phase.intentRaw;
                karmaState = phase.karmaState;

                logEntry["intent_analysis"] = new JObject
                {
                    ["success"] = true,
                    ["elapsed_time"] = intentTime,
                    ["raw_output"] = intentRaw,
                    ["parsed_result"] = intent,
                };
            }
            catch (Exception e)
            {
                logEntry["intent_analysis"] = new JObject
                {
                    ["success"] = false,
                    ["error"] = e.Message,
                };
                (logEntry["errors"] as JArray).Add($"意图解析失败: {e.Message}");
                _logger.AddLog(logEntry);
                CurrentThinking = false;
                ThinkingStage = "";
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"意图解析失败: {e.Message}",
                    ["log_id"] = _logger.Count - 1,
                };
            }

            if (intent == null)
            {
                (logEntry["errors"] as JArray).Add("意图解析失败：无法解析AI响应");
                _logger.AddLog(logEntry);
                CurrentThinking = false;
                ThinkingStage = "";
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "意图解析失败：无法解析AI响应",
                    ["log_id"] = _logger.Count - 1,
                };
            }

            var classification = Classification.Normalize(JsonHelpers.GetString(intent, "classification"));
            logEntry["classification"] = classification;
            var costEnergy = Math.Max(0, Math.Min(10, JsonHelpers.GetInt(intent, "cost_energy")));

            // 业力门控（技能树拦截 → 补充评估 → karma 日志 → 上限拦截）
            var blocked = await KarmaGatePhaseAsync(
                command, classification, karmaState, context, logEntry, costEnergy, cancellationToken)
                .ConfigureAwait(false);
            if (blocked != null) return blocked;

            // 不可行请求
            if (!JsonHelpers.GetBool(intent, "feasible", false))
            {
                logEntry["final_result"] = new JObject
                {
                    ["type"] = "rejected",
                    ["reason"] = JsonHelpers.GetString(intent, "reasoning"),
                };
                _logger.AddLog(logEntry);
                CurrentThinking = false;
                ThinkingStage = "";
                var rejected = new JObject
                {
                    ["success"] = false,
                    ["type"] = "rejected",
                    ["message"] = JsonHelpers.GetString(intent, "response_to_player", "该操作无法实现"),
                    ["classification"] = classification,
                    ["cost_energy"] = costEnergy,
                    ["log_id"] = _logger.Count - 1,
                };
                MergeKarmaFields(rejected, karmaState);
                return rejected;
            }

            // E类搞笑
            if (classification == Classification.E)
            {
                logEntry["final_result"] = new JObject
                {
                    ["type"] = "fun",
                    ["response"] = JsonHelpers.GetString(intent, "response_to_player"),
                };
                _logger.AddLog(logEntry);
                CurrentThinking = false;
                ThinkingStage = "";
                var fun = new JObject
                {
                    ["success"] = true,
                    ["type"] = "fun",
                    ["message"] = JsonHelpers.GetString(intent, "response_to_player"),
                    ["classification"] = "E",
                    ["cost_energy"] = costEnergy,
                    ["log_id"] = _logger.Count - 1,
                };
                MergeKarmaFields(fun, karmaState);
                return fun;
            }

            // A类机制修改 - 混合处理（A1硬编码 + A2灵活编码）
            if (classification == Classification.A)
            {
                var result = await HandleActionAAsync(intent, configs, logEntry, cancellationToken)
                    .ConfigureAwait(false);
                logEntry["final_result"] = result;
                _logger.AddLog(logEntry);
                CurrentThinking = false;
                ThinkingStage = "";
                result["log_id"] = _logger.Count - 1;
                result["cost_energy"] = costEnergy;
                MergeKarmaFields(result, karmaState);
                return result;
            }

            // F类高级功能 - 直接标记不可行
            if (classification == Classification.F)
            {
                logEntry["final_result"] = new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "无法实现高级功能",
                };
                _logger.AddLog(logEntry);
                CurrentThinking = false;
                ThinkingStage = "";
                var fResult = new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "该功能需要修改核心引擎代码，暂时无法实现",
                    ["classification"] = "F",
                    ["cost_energy"] = costEnergy,
                    ["log_id"] = _logger.Count - 1,
                };
                MergeKarmaFields(fResult, karmaState);
                return fResult;
            }

            // B/C/D类 - 多action并行处理
            ThinkingStage = "code";
            var actions = intent["actions"] as JArray;
            if (actions == null || actions.Count == 0)
                actions = BuildLegacyActions(intent, classification);

            JObject mergedResult;
            if (actions == null || actions.Count == 0)
            {
                mergedResult = new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"未知分类: {classification}",
                };
            }
            else
            {
                var tasks = new List<Task<JObject>>();
                foreach (var token in actions)
                {
                    var action = token as JObject ?? new JObject();
                    tasks.Add(ExecuteActionAsync(action, configs, logEntry, cancellationToken));
                }
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                var resultList = new JArray();
                foreach (var r in results) resultList.Add(r);
                mergedResult = MergeActionResults(resultList, intent);
            }

            logEntry["final_result"] = mergedResult;
            _logger.AddLog(logEntry);
            CurrentThinking = false;
            ThinkingStage = "";
            mergedResult["log_id"] = _logger.Count - 1;
            mergedResult["cost_energy"] = costEnergy;
            MergeKarmaFields(mergedResult, karmaState);
            return mergedResult;
        }

        /// <summary>兼容旧格式：没有 actions 时，从 structured_instruction 构建单 action。</summary>
        static JArray BuildLegacyActions(JObject intent, string classification)
        {
            var instruction = intent["structured_instruction"] as JObject ?? new JObject();
            var nextPrompt = JsonHelpers.GetString(intent, "next_ai_prompt");
            var targetFiles = intent["target_files"] as JArray ?? new JArray();

            var action = new JObject();
            if (classification.StartsWith("D", StringComparison.Ordinal))
            {
                action["type"] = "D";
            }
            else if (classification == Classification.CPlus)
            {
                action["type"] = "C+";
            }
            else if (classification.StartsWith("B", StringComparison.Ordinal)
                     || classification.StartsWith("C", StringComparison.Ordinal))
            {
                action["type"] = classification.Substring(0, 1);
            }
            else
            {
                return null;
            }

            action["target_files"] = targetFiles;
            action["instruction"] = instruction;
            action["prompt"] = nextPrompt;

            var actions = new JArray();
            actions.Add(action);
            return actions;
        }

        /// <summary>执行单个 action，按类型分派到对应 handler。</summary>
        public async Task<JObject> ExecuteActionAsync(
            JObject action, JObject configs, JObject logEntry, CancellationToken cancellationToken = default)
        {
            var actionType = JsonHelpers.GetString(action, "type");
            var instruction = action["instruction"] as JObject ?? new JObject();
            var prompt = JsonHelpers.GetString(action, "prompt");

            // 将 action 包装为兼容旧格式的 action_intent
            var actionIntent = new JObject
            {
                ["structured_instruction"] = instruction,
                ["next_ai_prompt"] = prompt,
                ["target_files"] = action["target_files"] ?? new JArray(),
                ["side"] = action["side"] ?? JValue.CreateNull(),
            };

            switch (actionType)
            {
                case "B":
                    return await HandleActionBWithLogAsync(actionIntent, configs, logEntry, cancellationToken).ConfigureAwait(false);
                case "C":
                    return await HandleActionCWithLogAsync(actionIntent, configs, logEntry, cancellationToken).ConfigureAwait(false);
                case "C+":
                    return await HandleActionCpWithLogAsync(actionIntent, configs, logEntry, cancellationToken).ConfigureAwait(false);
                case "D":
                    var sections = instruction["target_sections"] as JArray;
                    if (sections != null && sections.Count > 0)
                        return await HandleActionD2WithLogAsync(actionIntent, configs, logEntry, cancellationToken).ConfigureAwait(false);
                    return await HandleActionDWithLogAsync(actionIntent, configs, logEntry, cancellationToken).ConfigureAwait(false);
                default:
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "error",
                        ["message"] = $"未知action类型: {actionType}",
                    };
            }
        }

        // ══════════════════════════════════════════════════════════════
        // karma 钩子层（RPG）
        // ══════════════════════════════════════════════════════════════

        /// <summary>步骤1：意图解析 + 业力评估（并行）。</summary>
        async Task<(JObject intent, double intentTime, string intentRaw, JObject karmaState)> IntentAndKarmaPhaseAsync(
            string command, JObject context, JObject logEntry, CancellationToken cancellationToken)
        {
            var configs = context["configs"] as JObject ?? new JObject();
            var boardSummary = GetBoardSummary(configs["board_state"] as JObject);
            var skillModifiers = context["skill_modifiers"] as JObject ?? new JObject();

            var intentTask = ParseIntentWithLogAsync(command, context, cancellationToken);
            var karmaTask = KarmaAssessor.AssessAsync(command, "", boardSummary, skillModifiers, cancellationToken);
            await Task.WhenAll(intentTask, karmaTask).ConfigureAwait(false);

            var intentResult = intentTask.Result;
            var estimatedKarmaCost = karmaTask.Result;

            var karmaState = new JObject
            {
                ["cost"] = estimatedKarmaCost,
                ["board_summary"] = boardSummary,
                ["skill_modifiers"] = skillModifiers,
            };
            return (intentResult.intent, intentResult.elapsed, intentResult.raw, karmaState);
        }

        /// <summary>技能树门控 → 业力补充评估 → karma 日志 → 业力上限拦截。</summary>
        async Task<JObject> KarmaGatePhaseAsync(
            string command, string classification, JObject karmaState,
            JObject context, JObject logEntry, int costEnergy, CancellationToken cancellationToken)
        {
            if (karmaState == null) return null;

            var estimatedKarmaCost = JsonHelpers.GetInt(karmaState, "cost");
            var skillModifiers = karmaState["skill_modifiers"] as JObject ?? new JObject();
            var boardSummary = JsonHelpers.GetString(karmaState, "board_summary");

            // 技能树门控：检查分类是否已解锁
            var allowedClassifications = context["allowed_classifications"] as JArray;
            if (allowedClassifications != null && !string.IsNullOrEmpty(classification)
                && !ContainsString(allowedClassifications, classification))
            {
                var skillGateMap = new Dictionary<string, string>
                {
                    ["C+"] = "自定义棋子（需在技能树中解锁「作弊精通 → 自定义棋子」）",
                    ["D"] = "前端修改（需在技能树中解锁「作弊精通 → 前端修改」）",
                };
                var gateReason = skillGateMap.TryGetValue(classification, out var m)
                    ? m
                    : $"分类 {classification} 未解锁";
                logEntry["final_result"] = new JObject
                {
                    ["type"] = "rejected",
                    ["reason"] = $"技能树未解锁：{gateReason}",
                };
                _logger.AddLog(logEntry);
                CurrentThinking = false;
                ThinkingStage = "";
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "rejected",
                    ["message"] = $"该操作被技能树拦截：{gateReason}",
                    ["classification"] = classification,
                    ["cost_energy"] = costEnergy,
                    ["estimated_karma_cost"] = 0,
                    ["log_id"] = _logger.Count - 1,
                };
            }

            // 分类已知后，用正确的分类补充评估一次
            if (!string.IsNullOrEmpty(classification) && estimatedKarmaCost > 0)
            {
                try
                {
                    estimatedKarmaCost = await KarmaAssessor.AssessAsync(
                        command, classification, boardSummary, skillModifiers, cancellationToken).ConfigureAwait(false);
                    karmaState["cost"] = estimatedKarmaCost;
                }
                catch
                {
                    // 补充评估失败时沿用并行阶段的估算值
                }
            }

            logEntry["karma_assessment"] = new JObject
            {
                ["cost"] = estimatedKarmaCost,
                ["intent_class"] = classification,
                ["game_type"] = GameType,
                ["parallel"] = true,
            };

            // 业力上限拦截：评估超出单次上限的作弊直接拦截（E类固定1点，不会被拦截）
            if (classification != Classification.E && estimatedKarmaCost > 0)
            {
                var maxSingle = KarmaAssessor.LocalKarmaSingleMax
                                + JsonHelpers.GetInt(skillModifiers, "karma_single_max_bonus", 0);
                if (estimatedKarmaCost > maxSingle)
                {
                    logEntry["final_result"] = new JObject
                    {
                        ["type"] = "rejected",
                        ["reason"] = $"业力评估 {estimatedKarmaCost} 超出单次上限 {maxSingle}，拦截",
                    };
                    _logger.AddLog(logEntry);
                    CurrentThinking = false;
                    ThinkingStage = "";
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "rejected",
                        ["message"] = $"作弊业力评估为 {estimatedKarmaCost} 点，超出单次上限 {maxSingle} 点，天道拦截·不予执行",
                        ["classification"] = classification,
                        ["cost_energy"] = costEnergy,
                        ["estimated_karma_cost"] = estimatedKarmaCost,
                        ["karma_blocked"] = true,
                        ["log_id"] = _logger.Count - 1,
                    };
                }
            }

            return null;
        }

        /// <summary>响应体附加字段（RPG）：estimated_karma_cost。</summary>
        static void MergeKarmaFields(JObject result, JObject karmaState)
        {
            if (karmaState == null || result == null) return;
            result["estimated_karma_cost"] = JsonHelpers.GetInt(karmaState, "cost");
        }

        // ══════════════════════════════════════════════════════════════
        // 基础设施
        // ══════════════════════════════════════════════════════════════

        void RecordTokenUsage(int promptTokens, int completionTokens)
        {
            lock (_tokenLock)
            {
                TokenStats["total_prompt_tokens"] = JsonHelpers.GetInt(TokenStats, "total_prompt_tokens") + promptTokens;
                TokenStats["total_completion_tokens"] = JsonHelpers.GetInt(TokenStats, "total_completion_tokens") + completionTokens;
                TokenStats["total_calls"] = JsonHelpers.GetInt(TokenStats, "total_calls") + 1;

                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var dailyStats = TokenStats["daily_stats"] as JObject ?? new JObject();
                TokenStats["daily_stats"] = dailyStats;
                if (!(dailyStats[today] is JObject day))
                {
                    day = new JObject { ["prompt_tokens"] = 0, ["completion_tokens"] = 0, ["calls"] = 0 };
                    dailyStats[today] = day;
                }
                day["prompt_tokens"] = JsonHelpers.GetInt(day, "prompt_tokens") + promptTokens;
                day["completion_tokens"] = JsonHelpers.GetInt(day, "completion_tokens") + completionTokens;
                day["calls"] = JsonHelpers.GetInt(day, "calls") + 1;
            }
            PersistTokenStats();
        }

        void PersistTokenStats()
        {
            if (string.IsNullOrEmpty(BaseDir)) return;
            try
            {
                var path = Path.Combine(BaseDir, "configs", "token_stats.json");
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, TokenStats.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch
            {
                // 统计写入失败不影响主流程
            }
        }

        /// <summary>调用 DeepSeek（system+user）；失败抛出异常（对齐 Python 的 resp.raise_for_status）。</summary>
        async Task<(string content, double elapsed)> CallDeepSeekAsync(
            string systemPrompt, string userPrompt, double temperature = 0.3, string model = null,
            CancellationToken cancellationToken = default)
        {
            var result = await Client.ChatAsync(
                systemPrompt, userPrompt,
                temperature: temperature,
                maxTokens: 8192,
                model: model,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "LLM 调用失败");
            return (result.Content ?? string.Empty, result.ElapsedSeconds);
        }

        /// <summary>从文本中提取 JSON（对象或数组）；失败返回 null。</summary>
        public JToken ExtractJsonToken(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return JToken.Parse(text.Trim());
            }
            catch
            {
                // 继续尝试代码块
            }

            foreach (Match m in _jsonBlockRe.Matches(text))
            {
                var candidate = m.Groups[1].Value.Trim();
                try { return JToken.Parse(candidate); }
                catch { /* 尝试下一个代码块 */ }
            }

            var first = text.IndexOf('{');
            var last = text.LastIndexOf('}');
            if (first != -1 && last != -1 && last > first)
            {
                try { return JToken.Parse(text.Substring(first, last - first + 1)); }
                catch { /* 忽略 */ }
            }

            return null;
        }

        /// <summary>从文本中提取 JSON 对象；失败返回 null。</summary>
        public JObject ExtractJson(string text)
        {
            var token = ExtractJsonToken(text);
            if (token is JObject obj) return obj;

            // 回退：提取第一个 { 到最后一个 }（对齐 Python _extract_json）
            if (string.IsNullOrEmpty(text)) return null;
            var first = text.IndexOf('{');
            var last = text.LastIndexOf('}');
            if (first != -1 && last != -1 && last > first)
            {
                try { return JObject.Parse(text.Substring(first, last - first + 1)); }
                catch { /* 忽略 */ }
            }
            return null;
        }

        /// <summary>从文本中提取 JSON Patch 数组；失败返回 null。</summary>
        public JArray ExtractJsonPatch(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                if (JToken.Parse(text.Trim()) is JArray direct) return direct;
            }
            catch
            {
                // 继续尝试代码块
            }

            foreach (Match m in _jsonBlockRe.Matches(text))
            {
                try
                {
                    if (JToken.Parse(m.Groups[1].Value.Trim()) is JArray arr) return arr;
                }
                catch { /* 尝试下一个代码块 */ }
            }

            var first = text.IndexOf('[');
            var last = text.LastIndexOf(']');
            if (first != -1 && last != -1 && last > first)
            {
                try
                {
                    if (JToken.Parse(text.Substring(first, last - first + 1)) is JArray arr) return arr;
                }
                catch { /* 忽略 */ }
            }

            return null;
        }

        /// <summary>尝试用 JSON Patch 模式或 diff 模式应用修改。</summary>
        (JObject newConfig, string error) ApplyPatchOrDiff(
            JObject originalConfig, string aiResponse, string configName, JObject logEntry)
        {
            var codeGenLog = logEntry["code_generation"] as JObject;
            if (codeGenLog == null)
            {
                codeGenLog = new JObject();
                logEntry["code_generation"] = codeGenLog;
            }

            var patch = ExtractJsonPatch(aiResponse);
            if (patch != null)
            {
                var (valid, err) = ChessSage.Core.Json.JsonPatch.IsValid(patch);
                if (valid)
                {
                    try
                    {
                        var newConfig = ChessSage.Core.Json.JsonPatch.Apply(originalConfig, patch) as JObject;
                        codeGenLog["patch_mode"] = "patch";
                        codeGenLog["patch_operations"] = patch.Count;
                        codeGenLog["patch_operations_detail"] = patch;
                        return (newConfig, null);
                    }
                    catch (Exception e)
                    {
                        codeGenLog["patch_apply_error"] = e.Message;
                    }
                }
            }

            var fullJson = ExtractJson(aiResponse);
            if (fullJson != null)
            {
                try
                {
                    var diffPatch = ChessSage.Core.Json.JsonPatch.GenerateDiff(originalConfig, fullJson);
                    var newConfig = ChessSage.Core.Json.JsonPatch.Apply(originalConfig, diffPatch) as JObject;
                    codeGenLog["patch_mode"] = "diff";
                    codeGenLog["diff_operations"] = diffPatch.Count;
                    codeGenLog["diff_operations_detail"] = diffPatch;
                    return (newConfig, null);
                }
                catch (Exception e)
                {
                    codeGenLog["diff_apply_error"] = e.Message;
                    return (null, $"应用diff失败: {e.Message}");
                }
            }

            return (null, "无法解析为JSON Patch或全量JSON");
        }

        /// <summary>合并多个 action 的执行结果。</summary>
        JObject MergeActionResults(JArray results, JObject intent)
        {
            var successCount = 0;
            var rejectedCount = 0;
            var errorCount = 0;
            foreach (var token in results)
            {
                if (!(token is JObject r)) continue;
                if (JsonHelpers.GetBool(r, "success")) successCount++;
                if (JsonHelpers.GetString(r, "type") == "rejected") rejectedCount++;
                else if (!JsonHelpers.GetBool(r, "success")) errorCount++;
            }

            var modifiedConfigs = new JObject();
            var allMessages = new List<string>();

            foreach (var token in results)
            {
                if (!(token is JObject r)) continue;
                if (r["modified_configs"] is JObject rc)
                {
                    foreach (var prop in rc.Properties())
                    {
                        if (prop.Name == "board_state" && modifiedConfigs.ContainsKey("board_state"))
                            DeepMergeBoardState(modifiedConfigs["board_state"] as JObject, prop.Value as JObject);
                        else
                            modifiedConfigs[prop.Name] = prop.Value.DeepClone();
                    }
                }
                var message = JsonHelpers.GetString(r, "message");
                if (!string.IsNullOrEmpty(message)) allMessages.Add(message);
            }

            if (successCount > 0 && errorCount == 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["type"] = modifiedConfigs.HasValues ? "applied" : "success",
                    ["message"] = JsonHelpers.GetString(intent, "response_to_player", "操作成功"),
                    ["modified_configs"] = modifiedConfigs,
                    ["action_results"] = results,
                };
            }
            if (successCount > 0 && errorCount > 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "partial",
                    ["message"] = JsonHelpers.GetString(intent, "response_to_player", "部分操作成功"),
                    ["modified_configs"] = modifiedConfigs,
                    ["action_results"] = results,
                };
            }
            if (rejectedCount == results.Count && results.Count > 0)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "rejected",
                    ["message"] = "所有操作均被拒绝",
                    ["action_results"] = results,
                };
            }
            return new JObject
            {
                ["success"] = false,
                ["type"] = "error",
                ["message"] = allMessages.Count > 0 ? string.Join("\n", allMessages) : "操作失败",
                ["action_results"] = results,
            };
        }

        /// <summary>合并两条机制原语列表（按 side 去重，保留先到者，后到者补充缺失字段/更长回合）。</summary>
        JArray MergeMechanismList(JArray baseList, JArray incoming)
        {
            var merged = new List<JObject>();
            if (baseList != null)
                foreach (var token in baseList)
                    if (token is JObject o) merged.Add((JObject)o.DeepClone());

            if (incoming != null)
            {
                foreach (var token in incoming)
                {
                    if (!(token is JObject item)) continue;
                    var sameSideIdx = -1;
                    for (int i = 0; i < merged.Count; i++)
                    {
                        if (JsonHelpers.GetString(merged[i], "side") == JsonHelpers.GetString(item, "side"))
                        {
                            sameSideIdx = i;
                            break;
                        }
                    }
                    if (sameSideIdx < 0)
                    {
                        merged.Add((JObject)item.DeepClone());
                        continue;
                    }

                    var existing = merged[sameSideIdx];
                    foreach (var prop in item.Properties())
                    {
                        if (!existing.ContainsKey(prop.Name)
                            || existing[prop.Name] == null
                            || existing[prop.Name].Type == JTokenType.Null
                            || (existing[prop.Name].Type == JTokenType.String && existing[prop.Name].Value<string>() == "")
                            || (existing[prop.Name] is JArray ea && ea.Count == 0))
                        {
                            existing[prop.Name] = prop.Value.DeepClone();
                        }
                    }

                    var incomingRemaining = item["remaining"];
                    if (incomingRemaining != null && incomingRemaining.Type == JTokenType.Integer
                        && incomingRemaining.Value<int>() != 0)
                    {
                        var cur = existing["remaining"];
                        var incomingVal = incomingRemaining.Value<int>();
                        if (cur == null || cur.Type != JTokenType.Integer
                            || cur.Value<int>() == 0 || incomingVal < 0
                            || incomingVal > cur.Value<int>())
                        {
                            existing["remaining"] = incomingVal;
                        }
                    }
                }
            }

            var result = new JArray();
            foreach (var o in merged) result.Add(o);
            return result;
        }

        /// <summary>深度合并两个 board_state（mechanisms 列表做并集，custom_rules_active 追加）。</summary>
        void DeepMergeBoardState(JObject baseObj, JObject overrideObj)
        {
            if (baseObj == null || overrideObj == null) return;
            foreach (var prop in overrideObj.Properties())
            {
                var key = prop.Name;
                var value = prop.Value;

                if (key == "custom_rules_active" && value is JArray customArr)
                {
                    if (!(baseObj[key] is JArray baseArr))
                    {
                        baseArr = new JArray();
                        baseObj[key] = baseArr;
                    }
                    foreach (var item in customArr) baseArr.Add(item.DeepClone());
                }
                else if (key == "mechanisms" && value is JObject mechanisms)
                {
                    if (!(baseObj[key] is JObject baseMech))
                    {
                        baseMech = new JObject();
                        baseObj[key] = baseMech;
                    }
                    foreach (var mProp in mechanisms.Properties())
                    {
                        if (IsMechanismListKey(mProp.Name) && mProp.Value is JArray mArr)
                        {
                            if (baseMech[mProp.Name] is JArray curArr)
                                baseMech[mProp.Name] = MergeMechanismList(curArr, mArr);
                            else
                                baseMech[mProp.Name] = mArr.DeepClone();
                        }
                        else if (!baseMech.ContainsKey(mProp.Name))
                        {
                            baseMech[mProp.Name] = mProp.Value.DeepClone();
                        }
                    }
                }
                else if (value is JObject vo && baseObj[key] is JObject bo)
                {
                    DeepMergeBoardState(bo, vo);
                }
                else
                {
                    baseObj[key] = value.DeepClone();
                }
            }
        }

        static bool IsMechanismListKey(string key)
        {
            foreach (var k in MechanismListKeys)
                if (k == key) return true;
            return false;
        }

        /// <summary>在棋盘中按 id 查找棋子。</summary>
        static JObject FindPiece(JObject board, string pieceId)
        {
            var pieces = board?["pieces"] as JArray;
            if (pieces == null) return null;
            foreach (var token in pieces)
                if (token is JObject p && JsonHelpers.GetString(p, "id") == pieceId)
                    return p;
            return null;
        }

        /// <summary>根据关键词识别棋盘变换操作类型（transform/add/remove/move/rotate/modify）。</summary>
        public static string DetectActionType(string actionStr)
        {
            if (string.IsNullOrEmpty(actionStr)) return "unknown";
            var lower = actionStr.ToLowerInvariant();

            string[] transformZh = { "变换", "变成", "变为", "变炮", "变车", "变马", "变种" };
            string[] transformEn = { "transform", "mutate", "evolve", "change_type", "piece_type" };
            foreach (var kw in transformZh) if (actionStr.Contains(kw)) return "transform";
            foreach (var kw in transformEn) if (lower.Contains(kw)) return "transform";

            string[] addZh = { "添加", "新增", "增加", "放置", "填满", "补充" };
            string[] addEn = { "add", "create", "spawn", "generate" };
            foreach (var kw in addZh) if (actionStr.Contains(kw)) return "add";
            foreach (var kw in addEn) if (lower.Contains(kw)) return "add";

            string[] removeZh = { "删除", "移除", "去掉", "消灭", "吃掉" };
            string[] removeEn = { "remove", "delete", "destroy", "kill" };
            foreach (var kw in removeZh) if (actionStr.Contains(kw)) return "remove";
            foreach (var kw in removeEn) if (lower.Contains(kw)) return "remove";

            string[] moveZh = { "移动", "移到", "挪到" };
            string[] moveEn = { "move", "shift", "relocate", "reposition" };
            foreach (var kw in moveZh) if (actionStr.Contains(kw)) return "move";
            foreach (var kw in moveEn) if (lower.Contains(kw)) return "move";

            string[] rotateZh = { "旋转", "翻转" };
            string[] rotateEn = { "rotate", "flip", "turn" };
            foreach (var kw in rotateZh) if (actionStr.Contains(kw)) return "rotate";
            foreach (var kw in rotateEn) if (lower.Contains(kw)) return "rotate";

            string[] modifyZh = { "修改", "改" };
            string[] modifyEn = { "modify", "change", "update", "edit" };
            foreach (var kw in modifyZh) if (actionStr.Contains(kw)) return "modify";
            foreach (var kw in modifyEn) if (lower.Contains(kw)) return "modify";

            return "unknown";
        }

        /// <summary>生成重试提示词，把错误信息附加到原提示词后。</summary>
        static string GenerateRetryPrompt(string originalPrompt, List<string> errors)
        {
            var errorText = string.Join("\n", errors.ConvertAll(e => $"- {e}"));
            return originalPrompt + "\n\n## ⚠️ 重要：之前的输出存在以下错误，请修正后重新输出\n\n错误列表：\n"
                   + errorText + "\n\n请仔细检查并修正上述错误，确保输出符合所有要求。";
        }

        /// <summary>无论校验是否通过，都尝试提取 patch/diff 操作记录到日志。</summary>
        void LogCodeDiffForDisplay(string resp, JObject codeGenLog, int attempt)
        {
            var prefix = attempt == 0 ? "" : $"retry_{attempt}_";

            var patch = ExtractJsonPatch(resp);
            if (patch != null)
            {
                SetIfAbsent(codeGenLog, prefix + "patch_mode", "patch");
                SetIfAbsent(codeGenLog, prefix + "patch_operations", patch.Count);
                SetIfAbsent(codeGenLog, prefix + "patch_operations_detail", patch);
                return;
            }

            var fullJson = ExtractJson(resp);
            if (fullJson != null)
            {
                SetIfAbsent(codeGenLog, prefix + "patch_mode", "diff");
                if (!codeGenLog.ContainsKey(prefix + "diff_operations_detail"))
                {
                    var summaryKeys = new List<string>();
                    var idx = 0;
                    foreach (var prop in fullJson.Properties())
                    {
                        summaryKeys.Add(prop.Name);
                        if (++idx >= 5) break;
                    }
                    var summary = new JArray();
                    foreach (var k in summaryKeys)
                        summary.Add(new JObject { ["op"] = "replace", ["path"] = "/" + k, ["value"] = "..." });
                    codeGenLog[prefix + "diff_operations_detail"] = summary;
                }
            }
        }

        static void SetIfAbsent(JObject obj, string key, JToken value)
        {
            if (!obj.ContainsKey(key)) obj[key] = value;
        }

        /// <summary>生成棋盘摘要（按顺序输出各阵营存活棋子数）。</summary>
        public string GetBoardSummary(JObject board)
        {
            var pieces = board?["pieces"] as JArray;
            var parts = new List<string>();
            foreach (var side in BoardSummaryOrder)
            {
                var count = 0;
                if (pieces != null)
                    foreach (var token in pieces)
                        if (token is JObject p && JsonHelpers.GetBool(p, "is_alive", true)
                            && JsonHelpers.GetString(p, "side") == side)
                            count++;
                parts.Add($"{SideLabels[side]}{count}子");
            }
            return string.Join(", ", parts);
        }

        /// <summary>生成详细棋局实况（供 E 类闲聊/查询回答局面、优势、胜负预测）。</summary>
        public string BuildGameInsight(JObject board)
        {
            var pieces = board?["pieces"] as JArray ?? new JArray();
            var alive = new List<JObject>();
            foreach (var token in pieces)
                if (token is JObject p && JsonHelpers.GetBool(p, "is_alive", true))
                    alive.Add(p);

            var counts = new Dictionary<string, int>();
            var lines = new List<string>();
            foreach (var side in BoardSummaryOrder)
            {
                var sidePieces = new List<JObject>();
                foreach (var p in alive)
                    if (JsonHelpers.GetString(p, "side") == side)
                        sidePieces.Add(p);
                counts[side] = sidePieces.Count;
                var label = SideLabels.TryGetValue(side, out var l) ? l : side;
                if (sidePieces.Count == 0)
                {
                    lines.Add($"- {label}：已无存活棋子（面临被全歼）");
                    continue;
                }
                var descParts = new List<string>();
                foreach (var p in sidePieces)
                {
                    var pos = p["position"] as JArray;
                    var px = pos != null && pos.Count > 0 ? pos[0].ToString() : "0";
                    var py = pos != null && pos.Count > 1 ? pos[1].ToString() : "0";
                    var name = JsonHelpers.GetString(p, "name");
                    if (string.IsNullOrEmpty(name)) name = JsonHelpers.GetString(p, "type");
                    descParts.Add($"{name}@({px},{py})");
                }
                lines.Add($"- {label}：存活 {sidePieces.Count} 子 —— {string.Join("、", descParts)}");
            }

            var nonzero = new Dictionary<string, int>();
            foreach (var kv in counts) if (kv.Value > 0) nonzero[kv.Key] = kv.Value;

            if (nonzero.Count >= 2 && AllValuesEqual(nonzero))
            {
                lines.Add("- 双方存活子数相等，胜负更多取决于棋子种类与走位");
            }
            else if (nonzero.Count > 0)
            {
                string lead = null;
                var max = int.MinValue;
                foreach (var kv in nonzero)
                    if (kv.Value > max) { max = kv.Value; lead = kv.Key; }
                lines.Add($"- 仅按存活子数粗略看，{(SideLabels.TryGetValue(lead, out var ll) ? ll : lead)}偏多（强弱需结合棋子类型/规则综合判断）");
            }

            if (board?["game_status"] is JObject status
                && JsonHelpers.GetString(status, "state") == "ended")
            {
                var winner = JsonHelpers.GetString(status, "winner");
                lines.Add($"- 对局已结束，胜者：{(SideLabels.TryGetValue(winner, out var wl) ? wl : winner)}");
            }

            var turn = board != null ? JsonHelpers.GetString(board, "current_turn", DefaultTurn) : DefaultTurn;
            lines.Add($"- 当前轮到：{(SideLabels.TryGetValue(turn, out var tl) ? tl : turn)}");
            return string.Join("\n", lines);
        }

        static bool AllValuesEqual(Dictionary<string, int> dict)
        {
            int? first = null;
            foreach (var kv in dict)
            {
                if (first == null) first = kv.Value;
                else if (first.Value != kv.Value) return false;
            }
            return true;
        }

        /// <summary>从意图的 side / target_files 推断 (棋子规则配置名, 中文标签)。</summary>
        public (string configName, string label) SideConfigFor(JObject intent)
        {
            var side = intent != null && intent["side"] != null && intent["side"].Type != JTokenType.Null
                ? intent["side"].Value<string>() : null;
            var targetFiles = intent?["target_files"] as JArray ?? new JArray();

            foreach (var entry in SideConfigMap)
            {
                var fileName = entry.configName + ".json";
                if (side == entry.configName.Replace("pieces_", "")
                    || (entry.configName == "pieces_red" && side == "red")
                    || (entry.configName == "pieces_black" && side == "black")
                    || ContainsString(targetFiles, fileName))
                {
                    return entry;
                }
            }
            return SideConfigMap[0];
        }

        /// <summary>意图解析（带日志）：构建含棋局实况的提示词并调用 LLM。</summary>
        public async Task<(JObject intent, double elapsed, string raw)> ParseIntentWithLogAsync(
            string command, JObject context, CancellationToken cancellationToken = default)
        {
            var configs = context?["configs"] as JObject ?? new JObject();
            var board = configs["board_state"] as JObject;
            var piecesSummary = GetBoardSummary(board);
            var activeRules = GetActiveCustomRules(board);

            var userPrompt = "当前游戏状态：\n"
                + $"- 当前回合：{JsonHelpers.GetString(board, "current_turn", DefaultTurn)}\n"
                + $"- 棋盘概况：{piecesSummary}\n"
                + "- 棋局实况（供回答玩家的棋局信息问题）：\n"
                + BuildGameInsight(board) + "\n"
                + $"- 已激活的自定义规则：{(activeRules.Count > 0 ? string.Join(", ", activeRules) : "无")}\n\n"
                + "回答规则：\n"
                + "- 若玩家只是在询问棋局信息（如局面如何、谁占优、预测哪方胜率高等），请按「E类·闲聊/查询」处理：\n"
                + "  feasible=true，classification=\"E\"，actions 为空数组；并在 response_to_player 中**基于上面的\"棋局实况\"如实、自然地回答**，明确告知当前局面与大致强弱倾向，不要拒绝、不要答非所问，也不要让它变成一次修改。\n"
                + "- 若玩家提出的是具体修改请求（改规则/改棋子/改界面等），则按 A/B/C/C+/D/F 分类正常处理。\n\n"
                + $"玩家输入：\"{command}\"\n\n"
                + "请分析并输出JSON。";

            var (resp, elapsed) = await CallDeepSeekAsync(
                PromptBuilder.IntentParserSystem, userPrompt, 0.3, null, cancellationToken).ConfigureAwait(false);

            return (ExtractJson(resp), elapsed, resp);
        }

        static List<string> GetActiveCustomRules(JObject board)
        {
            var result = new List<string>();
            var rules = board?["game_status"]?["custom_rules_active"] as JArray;
            if (rules != null)
                foreach (var token in rules)
                    if (token.Type == JTokenType.String) result.Add(token.Value<string>());
            return result;
        }

        // ══════════════════════════════════════════════════════════════
        // 校验
        // ══════════════════════════════════════════════════════════════

        /// <summary>棋盘校验：Schema + 棋子核心属性完整性（按 action_type 分支）。</summary>
        (bool ok, string error) ValidateBoard(JObject newBoard, JObject oldBoard, string actionType = "unknown")
        {
            var (valid, err) = SchemaValidator.ValidateBoardState(newBoard);
            if (!valid) return (false, err);

            var oldPieces = oldBoard?["pieces"] as JArray ?? new JArray();
            var newPieces = newBoard?["pieces"] as JArray ?? new JArray();

            var oldMap = new Dictionary<string, JObject>();
            foreach (var t in oldPieces) if (t is JObject p) oldMap[JsonHelpers.GetString(p, "id")] = p;
            var newMap = new Dictionary<string, JObject>();
            foreach (var t in newPieces) if (t is JObject p) newMap[JsonHelpers.GetString(p, "id")] = p;

            string[] coreAttrs = { "id", "type", "side", "name" };

            string MissingPieces(List<string> ids)
            {
                var missing = new List<string>();
                foreach (var pid in ids) if (!newMap.ContainsKey(pid)) missing.Add(pid);
                return missing.Count > 0 ? $"棋子丢失: {string.Join(", ", missing)}" : null;
            }

            string CoreTampered(List<string> ids, bool includeName)
            {
                foreach (var pid in ids)
                {
                    var op = oldMap[pid];
                    var np = newMap[pid];
                    foreach (var attr in coreAttrs)
                    {
                        if (!includeName && attr == "name") continue;
                        if (JTokenToString(op[attr]) != JTokenToString(np[attr]))
                            return $"棋子属性被篡改: {pid}的{attr}从{JTokenToString(op[attr])}变为{JTokenToString(np[attr])}";
                    }
                }
                return null;
            }

            if (actionType == "add")
            {
                var oldAliveIds = new List<string>();
                foreach (var t in oldPieces)
                    if (t is JObject p && JsonHelpers.GetBool(p, "is_alive", true))
                        oldAliveIds.Add(JsonHelpers.GetString(p, "id"));
                var missing = MissingPieces(oldAliveIds);
                if (missing != null) return (false, missing);
                var tampered = CoreTampered(oldAliveIds, includeName: true);
                if (tampered != null) return (false, tampered);

                var newCount = 0;
                foreach (var t in newPieces) if (t is JObject p && JsonHelpers.GetBool(p, "is_alive", true)) newCount++;
                if (newCount < oldAliveIds.Count)
                    return (false, $"棋子数量减少: 原{oldAliveIds.Count}个，现{newCount}个");
            }
            else if (actionType == "remove" || actionType == "move" || actionType == "rotate")
            {
                var oldIds = AllIds(oldPieces);
                var missing = MissingPieces(oldIds);
                if (missing != null) return (false, missing);
                var tampered = CoreTampered(oldIds, includeName: true);
                if (tampered != null) return (false, tampered);
            }
            else if (actionType == "modify")
            {
                var missing = MissingPieces(AllIds(oldPieces));
                if (missing != null) return (false, missing);
            }
            else if (actionType == "transform")
            {
                var oldIds = AllIds(oldPieces);
                var missing = MissingPieces(oldIds);
                if (missing != null) return (false, missing);

                var oldCount = 0;
                foreach (var t in oldPieces) if (t is JObject p && JsonHelpers.GetBool(p, "is_alive", true)) oldCount++;
                var newCount = 0;
                foreach (var t in newPieces) if (t is JObject p && JsonHelpers.GetBool(p, "is_alive", true)) newCount++;
                if (oldCount != newCount)
                    return (false, $"棋子数量变化: 原{oldCount}个，现{newCount}个");

                foreach (var pid in oldIds)
                {
                    var op = oldMap[pid];
                    var np = newMap[pid];
                    if (JTokenToString(op["id"]) != JTokenToString(np["id"]))
                        return (false, $"棋子ID被篡改: {pid}的id从{JTokenToString(op["id"])}变为{JTokenToString(np["id"])}");
                    if (JTokenToString(op["side"]) != JTokenToString(np["side"]))
                        return (false, $"棋子阵营被篡改: {pid}的side从{JTokenToString(op["side"])}变为{JTokenToString(np["side"])}");
                    if (JTokenToString(op["position"]) != JTokenToString(np["position"]))
                        return (false, $"棋子位置被改变（transform只改变类型，不改变位置）: {pid}");
                    if (JTokenToString(op["is_alive"]) != JTokenToString(np["is_alive"]))
                        return (false, $"棋子存活状态被改变: {pid}");
                }
            }
            else if (actionType == "unknown")
            {
                var oldIds = AllIds(oldPieces);
                var missing = MissingPieces(oldIds);
                if (missing != null) return (false, missing);

                foreach (var pid in oldIds)
                {
                    var op = oldMap[pid];
                    var np = newMap[pid];
                    if (JTokenToString(op["id"]) != JTokenToString(np["id"]))
                        return (false, $"棋子ID被篡改: {pid}的id从{JTokenToString(op["id"])}变为{JTokenToString(np["id"])}");
                    if (JTokenToString(op["side"]) != JTokenToString(np["side"]))
                        return (false, $"棋子阵营被篡改: {pid}的side从{JTokenToString(op["side"])}变为{JTokenToString(np["side"])}");
                }
            }

            return (true, "");
        }

        static List<string> AllIds(JArray pieces)
        {
            var ids = new List<string>();
            foreach (var t in pieces) if (t is JObject p) ids.Add(JsonHelpers.GetString(p, "id"));
            return ids;
        }

        static string JTokenToString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "None";
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>检测规则是否发生实质性变化并验证 side_overrides 结构完整性。</summary>
        (bool valid, string message) ValidateRuleChange(JObject oldRules, JObject newRules, string targetType = null)
        {
            if (newRules["side_overrides"] == null)
                return (false, "缺少 side_overrides 字段，该字段必须保留");

            var newSideOverrides = newRules["side_overrides"] as JObject;
            if (newSideOverrides == null)
                return (false, "side_overrides 必须是对象类型");

            foreach (var key in new[] { "red", "black" })
            {
                if (!newSideOverrides.ContainsKey(key))
                    return (false, $"side_overrides 缺少 {key} 字段，必须保留 red 和 black 两个子对象");
            }
            foreach (var key in new[] { "red", "black" })
            {
                if (!(newSideOverrides[key] is JObject))
                    return (false, $"side_overrides.{key} 必须是对象类型");
            }

            var oldRulesDict = oldRules["pieces"] as JObject ?? new JObject();
            var newRulesDict = newRules["pieces"] as JObject ?? new JObject();

            if (!string.IsNullOrEmpty(targetType))
            {
                var oldRule = oldRulesDict[targetType] as JObject ?? new JObject();
                var newRule = newRulesDict[targetType] as JObject ?? new JObject();

                var oldMovement = oldRule["moves"] ?? new JArray();
                var newMovement = newRule["moves"] ?? new JArray();
                var oldCustom = oldRule["custom_modifiers"] ?? new JArray();
                var newCustom = newRule["custom_modifiers"] ?? new JArray();

                if (JToken.DeepEquals(oldMovement, newMovement) && JToken.DeepEquals(oldCustom, newCustom))
                {
                    var oldOverrides = oldRules["side_overrides"] as JObject ?? new JObject();
                    var newOverrides = newRules["side_overrides"] as JObject ?? new JObject();
                    foreach (var side in new[] { "red", "black" })
                    {
                        var oldSideRules = oldOverrides[side]?[targetType] ?? new JObject();
                        var newSideRules = newOverrides[side]?[targetType] ?? new JObject();
                        if (!JToken.DeepEquals(oldSideRules, newSideRules))
                            return (true, $"阵营规则{side}.{targetType}发生变化");
                    }
                    return (false, $"规则{targetType}未发生实质性变化");
                }
                return (true, $"规则{targetType}发生变化");
            }

            if (!JToken.DeepEquals(oldRulesDict, newRulesDict))
                return (true, "规则发生变化");

            var oldOv = oldRules["side_overrides"] ?? new JObject();
            var newOv = newRules["side_overrides"] ?? new JObject();
            if (!JToken.DeepEquals(oldOv, newOv))
                return (true, "阵营规则发生变化");

            return (false, "规则未发生实质性变化");
        }

        /// <summary>调用 CodeAI 并进行 Schema 硬校验 + 规则变化检查 + 最多 2 次重试。</summary>
        async Task<(JObject newConfig, string error)> CallCodeAiWithValidationAsync(
            string systemPrompt, string userPrompt, string configName, JObject originalConfig,
            JObject logEntry, double initialTemperature = 0.1, string actionType = "unknown",
            bool ruleChangeCheck = false, string targetType = null, CancellationToken cancellationToken = default)
        {
            const int maxRetries = 2;
            var codeGenLog = logEntry["code_generation"] as JObject;
            if (codeGenLog == null) { codeGenLog = new JObject(); logEntry["code_generation"] = codeGenLog; }

            var validationLog = logEntry["validation"] as JObject;
            if (validationLog == null)
            {
                validationLog = new JObject
                {
                    ["success"] = false,
                    ["elapsed_time"] = 0.0,
                    ["errors"] = new JArray(),
                    ["warnings"] = new JArray(),
                    ["retry_count"] = 0,
                };
                logEntry["validation"] = validationLog;
            }

            var currentPrompt = userPrompt;
            var currentTemp = initialTemperature;
            var allErrors = new List<string>();
            var retryCount = 0;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                ThinkingStage = "code";

                string resp;
                double elapsed;
                try
                {
                    (resp, elapsed) = await CallDeepSeekAsync(systemPrompt, currentPrompt, currentTemp, null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (attempt == 0)
                    {
                        codeGenLog["success"] = false;
                        codeGenLog["error"] = e.Message;
                    }
                    return (null, $"AI生成失败: {e.Message}");
                }

                if (attempt == 0)
                {
                    codeGenLog["success"] = true;
                    codeGenLog["elapsed_time"] = elapsed;
                    codeGenLog["raw_output"] = resp;
                }
                else
                {
                    codeGenLog[$"retry_{attempt}_raw_output"] = resp;
                    codeGenLog[$"retry_{attempt}_elapsed_time"] = elapsed;
                }

                try { LogCodeDiffForDisplay(resp, codeGenLog, attempt); }
                catch (Exception logErr) { SetIfAbsent(codeGenLog, "log_display_error", logErr.Message); }

                JObject newConfig;
                string parseErr;
                try
                {
                    (newConfig, parseErr) = ApplyPatchOrDiff(originalConfig, resp, configName, logEntry);
                }
                catch (Exception parseExc)
                {
                    newConfig = null;
                    parseErr = $"解析过程异常: {parseExc.Message}";
                }

                if (newConfig == null)
                {
                    allErrors.Add($"输出解析失败: {parseErr}");
                    if (attempt < maxRetries)
                    {
                        retryCount++;
                        currentTemp = Math.Min(currentTemp + 0.1, 0.3);
                        currentPrompt = GenerateRetryPrompt(currentPrompt, new List<string> { $"输出解析失败: {parseErr}" });
                        continue;
                    }
                    validationLog["success"] = false;
                    validationLog["errors"] = ToJArray(allErrors);
                    return (null, $"重试{maxRetries}次后仍失败: {parseErr}");
                }

                var (schemaValid, schemaErr) = SchemaValidator.HardValidateConfig(configName, newConfig);
                if (!schemaValid)
                {
                    allErrors.Add($"Schema校验失败: {schemaErr}");
                    if (attempt < maxRetries)
                    {
                        retryCount++;
                        currentTemp = Math.Min(currentTemp + 0.1, 0.3);
                        currentPrompt = GenerateRetryPrompt(currentPrompt, new List<string> { $"Schema校验失败: {schemaErr}" });
                        continue;
                    }
                    validationLog["success"] = false;
                    validationLog["errors"] = ToJArray(allErrors);
                    validationLog["retry_count"] = retryCount;
                    return (null, $"Schema校验失败，重试{maxRetries}次后仍有错误: {schemaErr}");
                }

                if (ruleChangeCheck && ContainsString(RuleChangeConfigNames, configName))
                {
                    var (ruleChanged, changeMsg) = ValidateRuleChange(originalConfig, newConfig, targetType);
                    if (!ruleChanged)
                    {
                        allErrors.Add(changeMsg);
                        if (attempt < maxRetries)
                        {
                            retryCount++;
                            currentTemp = Math.Min(currentTemp + 0.1, 0.3);
                            currentPrompt = GenerateRetryPrompt(currentPrompt, new List<string> { changeMsg });
                            continue;
                        }
                        validationLog["success"] = false;
                        validationLog["errors"] = ToJArray(allErrors);
                        return (null, $"规则未发生实质性变化，重试{maxRetries}次后仍未修改");
                    }
                }

                validationLog["success"] = true;
                validationLog["elapsed_time"] = 0.0;
                validationLog["errors"] = ToJArray(allErrors);
                validationLog["warnings"] = new JArray();
                validationLog["retry_count"] = retryCount;
                codeGenLog["parsed_json"] = newConfig;
                return (newConfig, null);
            }

            return (null, "未知错误");
        }

        // ══════════════════════════════════════════════════════════════
        // 工具
        // ══════════════════════════════════════════════════════════════

        static JArray ToJArray(List<string> items)
        {
            var arr = new JArray();
            foreach (var item in items) arr.Add(item);
            return arr;
        }

        static bool ContainsString(JArray arr, string value)
        {
            if (arr == null) return false;
            foreach (var token in arr)
                if (token.Type == JTokenType.String && token.Value<string>() == value) return true;
            return false;
        }

        static bool ContainsString(string[] arr, string value)
        {
            foreach (var item in arr) if (item == value) return true;
            return false;
        }

        static bool ContainsAny(string haystack, IEnumerable<string> needles)
        {
            foreach (var n in needles) if (haystack.Contains(n)) return true;
            return false;
        }

        static JObject GetConfigOrEmpty(JObject configs, string name)
        {
            var token = configs?[name];
            if (token is JObject obj) return (JObject)obj.DeepClone();
            return new JObject();
        }
    }
}