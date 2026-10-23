using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// Agent 主循环（E1-S3；替代旧双层级编排器的大脑；旧实现已随 E2-S7 删除）。
    ///
    /// 【循环】tool-calling 循环（§5.1）：system+user 前缀一次组装 → 每轮 ChatToolsAsync →
    /// usage 入账（TokenLedger 单一漏斗）+ KarmaFormula 投射 → tool_calls 派发（ToolRouter 五步，
    /// 错误结构化回喂自愈）/ 文字收尾。
    ///
    /// 【收尾策略 A（裁决 #4 + §13.6）】轮次耗尽 / token 硬停 / 20s 到点 / 连续工具错误 ≥3 /
    /// 单工具重复 &gt;3：有写入 ⇒ partial 预览（按 f(T_stop) 计价）；无写入 ⇒ 失败卡（0 点）。
    ///
    /// 【顶格警示不拦截（裁决 #3）】投射触顶只置 <see cref="AgentProgress.CapWarning"/>，循环继续；
    /// Confirm 口径 clamp 到 singleMax 放行（<see cref="KarmaFormula.Compute"/> 内部 clamp）。
    ///
    /// 【时间纪律】循环内零 wall-clock 依赖——20s 经 <see cref="CancellationToken"/> 贯通每轮
    /// （组合根负责 CancelAfter），引擎只做取消感知与收尾。
    /// </summary>
    public sealed class AgentEngine
    {
        /// <summary>离线/不可用时的错误文案（与旧引擎一致，ConvertResult 链路不变）。</summary>
        public const string OfflineMessage = "未设置API密钥，请先在设置中输入DeepSeek API Key";

        public LlmClient Client { get; }
        public string BaseDir { get; }
        public AgentRemakeConfig Config { get; }
        public KarmaFormula Formula { get; }
        /// <summary>长生命周期账本（遥测环 + token_stats.json 持久化；T_eff 按提交分列）。</summary>
        public TokenLedger Ledger { get; }

        /// <summary>
        /// 棋种 id（组合根按变体传入，与旧编排器的 GameType 同形）。
        /// 【注意】刻意不进 system prompt：变体差异经 <c>BoardSummarizer</c> 的棋子名摘要体现，
        /// 而 system 段字节级恒定是前缀缓存的硬前提（§13.2 R1/R2）。
        /// </summary>
        public string GameType { get; set; }

        /// <summary>最近一次提交的完整结果对象（UX 推演摘要与测试断言的接缝）。</summary>
        public AgentResult LastResult { get; private set; }

        /// <summary>配置硬校验注入（E2 接 SchemaValidator.HardValidateConfig；null = 跳过）。</summary>
        public Func<string, JObject, (bool ok, string error)> ConfigValidator;

        /// <summary>关卡目标类型表注入（E2 接 objective_types.json；null = 空表，全部拒绝）。</summary>
        public Func<IReadOnlyList<string>> ObjectiveTypesProvider;

        /// <summary>
        /// 延迟遥测时间源（§13.7 / §10.1 时钟缝）：生产恒为 <see cref="StopwatchClock"/>（单调），
        /// 测试注入虚拟时钟后 ttft_ms / round_ms / total_ms 完全确定、零 wall-clock。
        /// </summary>
        public IMonotonicClock Clock { get; set; } = new StopwatchClock();

        readonly int _sessionSeed;
        AgentKarmaState _karma = new AgentKarmaState { SingleMax = 120, KarmaMax = 120 };
        int _requestSeq;

        public AgentEngine(LlmClient client = null, string baseDir = null, AgentRemakeConfig config = null,
            int sessionSeed = 0)
        {
            Client = client ?? new LlmClient();
            BaseDir = baseDir;
            _sessionSeed = sessionSeed;
            Config = config ?? AgentConfigLoader.Load(ResolveConfigPath(baseDir));
            Formula = Config.CreateFormula();
            Ledger = TokenLedger.Load(baseDir);
            // 单一计量漏斗：每次成功调用（含 mock）恰好一次入账（ADR-4）。
            Client.TokenUsageCallbackEx = usage => Ledger.Add(usage);
        }

        /// <summary>
        /// 同步业力真源（§8.3：E2 由 CheatController.SyncAssessor 调用，两行适配的落点）。
        /// 便捷重载保留 Karma/KarmaMax/SingleMax/Detection/realm 五参（其余字段默认）。
        /// </summary>
        public void SyncKarmaState(int karma, int karmaMax, int karmaSingleMax, double detection, string realm)
            => SyncKarmaState(new AgentKarmaState
            {
                Karma = karma,
                KarmaMax = karmaMax,
                SingleMax = karmaSingleMax,
                Detection = detection,
                Realm = realm,
            });

        public void SyncKarmaState(AgentKarmaState state)
        {
            _karma = state ?? new AgentKarmaState();
            if (_karma.SingleMax <= 0) _karma.SingleMax = 120;
            if (_karma.KarmaMax <= 0) _karma.KarmaMax = 120;
        }

        /// <summary>
        /// 提交自然语言指令（契约与旧编排器的 ProcessCommandAsync 同构：
        /// 返回 <c>CheatController.ConvertResult</c> 可直接消费的 JObject；完整遥测见 <see cref="LastResult"/>）。
        /// 传输失败（LlmResult.Ok=false 且非取消）不在循环内重试——向上抛，由 CheatController 既有 catch
        /// 计入 ConsecutiveFailures（§5.3，与旧行为一致）。
        /// </summary>
        public async Task<JObject> ProcessCommandAsync(
            string command, JObject context, CancellationToken cancellationToken = default,
            IProgress<AgentProgress> progress = null)
        {
            if (!Client.IsAvailable)
                return FailOffline();

            var configs = context?["configs"] as JObject ?? new JObject();
            var sandbox = new SandboxTransaction(configs);
            var allowed = ReadAllowedTools(context);
            var allowedTargets = ReadAllowedPatchTargets(context);
            var router = new ToolRouter(sandbox, new AgentToolContext
            {
                AllowedTools = allowed,
                AllowedPatchTargets = allowedTargets,
                KarmaStateProvider = () => _karma,
                ConfigValidator = ConfigValidator,
                ObjectiveTypesProvider = ObjectiveTypesProvider,
            });

            var trajectory = new List<AgentTrajectoryEntry>();
            var warnings = new List<string>();
            var messages = new List<LlmMessage>
            {
                new LlmMessage { Role = "system", Content = AgentPrompts.BuildSystem(allowed) },
                new LlmMessage
                {
                    Role = "user",
                    Content = AgentPrompts.BuildUser(command ?? string.Empty,
                        BoardSummarizer.Summarize(configs["board_state"] as JObject, configs["board"] as JObject)),
                },
            };
            var toolsJson = JArray.Parse(AgentToolSchemas.SerializeTools(
                AgentToolSchemas.ForAllowedTools(allowed)));

            var limits = Config.Limits;
            var perf = Config.Performance;
            // 延迟遥测（§13.7）：提交起点 + 每轮耗时（仅计**已完成**轮；被取消的那轮不入账）。
            var submitStartMs = Clock.ElapsedMs;
            var roundDurations = new List<int>();
            // 循环内实时 T_eff（账本侧权威值以 EndRequest 重写为准；两者必须一致，测试断言）。
            long tokenEff = 0;
            long promptEff = 0, completionEff = 0;   // 进度计数器分列（设计 §1.7：思索/改数）

            // 遥测记录（账本侧权威重写 token 分列，防错值污染校准回归）。
            var record = Ledger.BeginRequest(CommandHash(command));
            var rng = new DeterministicRng(_sessionSeed, ++_requestSeq);
            int rounds = 0;
            int consecutiveErrors = 0;
            string lastToolKey = null;
            int sameToolCount = 0;
            bool capWarned = false;
            bool timedOut = false;
            bool truncationRetried = false;
            // E3-C 修复（HTTP 400 reasoning_content）：一旦 thinking 被降级，本提交内**不再回升**。
            // 思考/非思考混链是非法的——非思考轮产生的 assistant 消息没有 reasoning_content，
            // 下一轮若重新开 thinking，服务端会以 400 拒绝整次提交（实测 calib C+ #47）。
            bool thinkingDisabled = false;
            string stopReason = null;
            string finalText = null;
            string commitPartialNote = null;
            // 最近一次工具失败的结构化错误（失败卡翻译的输入；成功调用不清零——失败即事实）。
            string lastToolErrorCode = null;
            string lastToolErrorHint = null;

            void Report(int round, string phase, bool growing, string lastTool = null, bool lastOk = true)
            {
                progress?.Report(new AgentProgress
                {
                    Round = round,
                    TokensSoFar = tokenEff,
                    PromptTokens = promptEff,
                    CompletionTokens = completionEff,
                    ProjectedKarma = Formula.ProjectDisplay((int)tokenEff,
                        Formula.ResolveScopeWeight(sandbox.ChangedScopes)),
                    Growing = growing,
                    CurrentPhase = phase,
                    CapWarning = capWarned,
                    TimedOut = timedOut,
                    ChunkTokens = 0, // P1 流式预留（P0 非流式恒 0；面板只透传不消费）
                    LastToolName = lastTool,
                    LastToolOk = lastOk,
                });
            }

            AgentResult result = null;
            try
            {
                for (int round = 1; round <= limits.MaxRounds; round++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // 20s 到点（组合根取消）：当前轮不再发起，按已完成轮收尾（§13.6 策略 A）。
                        timedOut = true;
                        stopReason = "TIMEOUT";
                        break;
                    }

                    Report(round, "thinking", growing: true);

                    var isFinalRound = round == limits.MaxRounds;
                    var effort = AgentPerformanceConfig.EffortForRound(
                        perf.EffortProfile, round, limits.MaxRounds);
                    var maxTokens = isFinalRound ? perf.FinalRoundMaxTokens : perf.ToolRoundMaxTokens;

                    // 单轮 = 至多两次传输尝试（第二次仅在「thinking 协议 400」时发生，且强制无思考）。
                    LlmResult resp = null;
                    int attempt = 0;
                    bool useNoThink = false;
                    int roundElapsedMs = 0;
                    while (true)
                    {
                        Report(round, "thinking", growing: true);
                        useNoThink = thinkingDisabled;
                        var roundStartMs = Clock.ElapsedMs;
                        resp = await Client.ChatToolsAsync(messages, toolsJson,
                            maxTokens: maxTokens, reasoningEffort: useNoThink ? null : effort,
                            noThink: useNoThink,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        var elapsedMs = Clock.ElapsedMs - roundStartMs;
                        if (elapsedMs < 0) elapsedMs = 0; // 单调时钟防御（虚拟时钟实现可能回拨）
                        roundElapsedMs = (int)Math.Min(int.MaxValue, elapsedMs);

                        if (resp.Ok) break;

                        if (cancellationToken.IsCancellationRequested)
                        {
                            timedOut = true;
                            stopReason = "TIMEOUT";
                            resp = null;
                            break;
                        }

                        // HTTP 400「reasoning_content must be passed back」是可自愈的协议错误：
                        // 降级为无思考重试本轮（thinking→none 网关允许；反向 none→high 才 400）。
                        // 只重试一次，仍失败则按传输失败上抛（该提交失败，不拖垮整批）。
                        if (attempt == 0 && IsThinkingProtocolRejection(resp))
                        {
                            attempt++;
                            thinkingDisabled = true;
                            warnings.Add("HTTP 400（thinking 模式 reasoning_content 缺失）：本轮降级无思考重试");
                            continue;
                        }
                        // 传输/协议失败：不重试，向上抛（§5.3）。
                        throw new InvalidOperationException(resp.Error ?? "LLM 调用失败");
                    }
                    if (resp == null)
                        break; // 20s 到点（stopReason 已置）

                    rounds = round; // 只计实际完成 LLM 调用并入账的轮次（20s 到点未发起的不计）。
                    roundDurations.Add((int)Math.Min(int.MaxValue, roundElapsedMs));
                    // 投射顶格只警示不拦截（裁决 #3）。
                    tokenEff += resp.Usage?.TotalTokens ?? 0;
                    promptEff += resp.Usage?.PromptTokens ?? 0;
                    completionEff += resp.Usage?.CompletionTokens ?? 0;
                    var projected = Formula.ProjectDisplay((int)tokenEff,
                        Formula.ResolveScopeWeight(sandbox.ChangedScopes));
                    if (projected >= _karma.SingleMax)
                        capWarned = true;
                    Report(round, "tool", growing: true);

                    // 防御：thinking 轮若上报了 reasoning_tokens 却没回 reasoning_content，
                    // 后续再以 thinking 发送会缺字段（下一轮必 400）——立即降级，保提交不断链。
                    if (!useNoThink && (resp.Usage?.ReasoningTokens ?? 0) > 0 && string.IsNullOrEmpty(resp.ReasoningContent))
                    {
                        thinkingDisabled = true;
                        warnings.Add("thinking 模式未返回 reasoning_content，后续轮降级无思考（防 400 断链）");
                    }

                    // token 硬停（成本保险丝；延迟预算先于它绑定）。
                    if (tokenEff > limits.TokenHardStop)
                    {
                        stopReason = "TOKEN_BUDGET";
                        break;
                    }

                    if (string.Equals(resp.FinishReason, "length", StringComparison.Ordinal))
                    {
                        warnings.Add($"第 {round.ToString(CultureInfo.InvariantCulture)} 轮输出被截断（finish_reason=length）");
                        // 思考吃满预算（content 空且无工具调用）⇒ 后续轮关闭 thinking 重试一次。
                        // 网关允许 thinking→none 降级（实测 200）；反向 none→high 会 400，故降级是单向的。
                        if (!truncationRetried && round < limits.MaxRounds
                            && resp.ToolCalls.Count == 0 && string.IsNullOrEmpty(resp.Content))
                        {
                            truncationRetried = true;
                            thinkingDisabled = true;
                            warnings.Add("thinking 吃满 max_tokens，后续轮改用无思考重试");
                            Report(round, "tool", growing: true);
                            continue;
                        }
                    }

                    if (resp.ToolCalls.Count == 0)
                    {
                        // 模型选择文字收尾。
                        finalText = resp.Content ?? string.Empty;
                        stopReason = "TEXT";
                        break;
                    }

                    messages.Add(new LlmMessage
                    {
                        Role = "assistant",
                        Content = string.IsNullOrEmpty(resp.Content) ? null : resp.Content,
                        ToolCalls = resp.ToolCalls,
                        // E3-C：thinking 模式要求多轮工具调用把 reasoning_content 原样回传（否则 400）。
                        ReasoningContent = resp.ReasoningContent,
                    });

                    foreach (var call in resp.ToolCalls)
                    {
                        var toolResult = router.Dispatch(call); // 永不抛异常
                        var ok = toolResult.Value<bool>("ok");
                        if (!ok)
                        {
                            lastToolErrorCode = toolResult.Value<string>("errorCode");
                            lastToolErrorHint = toolResult.Value<string>("hint");
                        }
                        trajectory.Add(new AgentTrajectoryEntry
                        {
                            Round = round,
                            ToolName = call.Name ?? string.Empty,
                            ArgumentsJson = call.ArgumentsJson ?? string.Empty,
                            ResultJson = toolResult.ToString(Newtonsoft.Json.Formatting.None),
                            Ok = ok,
                            ErrorCode = ok ? null : toolResult.Value<string>("errorCode"),
                        });

                        // 重复熔断口径：同名 **且同参数** 才算重复（E3-S2 修复）。
                        // 修复前只按工具名计数——并行读 pieces_red/pieces_black 会被误判为死循环。
                        var callKey = (call.Name ?? string.Empty) + "|" + (call.ArgumentsJson ?? string.Empty);
                        if (callKey == lastToolKey) sameToolCount++;
                        else { lastToolKey = callKey; sameToolCount = 1; }

                        // 阅后即焚（§5.6）：读工具结果入列即压缩；错误全量回喂（可操作）。
                        messages.Add(new LlmMessage
                        {
                            Role = "tool",
                            ToolCallId = call.Id ?? string.Empty,
                            Content = Receipt(call.Name, call.ArgumentsJson, toolResult),
                        });

                        if (ok) consecutiveErrors = 0;
                        else consecutiveErrors++;
                        // 轨迹实时透传（E2-S4：面板按工具逐条渲染「天道行话」+ token 增量）。
                        Report(round, "tool", true, call.Name, ok);
                    }

                    if (sameToolCount > limits.RepeatToolCap)
                    {
                        stopReason = "REPEAT_LOOP";
                        break;
                    }
                    if (consecutiveErrors >= limits.MaxToolErrors)
                    {
                        stopReason = "TOOL_ERRORS";
                        break;
                    }
                    if (router.CommitRequested)
                    {
                        finalText = router.CommitSummary;
                        commitPartialNote = router.CommitPartialNote;
                        stopReason = "COMMIT";
                        break;
                    }
                }

                if (stopReason == null)
                    stopReason = "ROUNDS_EXHAUSTED";

                // 收尾在账本落账**之前**完成：业力结算与延迟遥测属于同一条提交记录（§13.7）。
                result = WrapUp(sandbox, record, rounds, stopReason, finalText, commitPartialNote,
                    trajectory, warnings, timedOut, capWarned, rng, tokenEff,
                    lastToolErrorCode, lastToolErrorHint);
            }
            finally
            {
                // 遥测落账（传输失败抛出也如实记录——失败也是真实消耗）。
                record.Rounds = rounds;
                record.Writes = sandbox.Writes;
                record.TimedOut = timedOut;
                record.CapWarned = capWarned;
                record.EffortProfile = perf.EffortProfile;
                record.Tools = DistinctTools(trajectory);
                // E3-C 校准审计：作用域是机械事实，无论成败都如实落账（失败卡权重按中性记）。
                record.Scopes = new List<string>(sandbox.ChangedScopes).ToArray();
                if (record.ScopeWeight <= 0.0) record.ScopeWeight = ScopeWeightsConfig.NeutralWeight;
                // 延迟四元组（§13.7）：rounds 与 round_ms 一一对应，total_ms 为客户端口径（含本地开销）。
                record.RoundMs = roundDurations.ToArray();
                record.TtftMs = roundDurations.Count > 0 ? roundDurations[0] : 0;
                record.TtftProxy = true; // P0 非流式：TTFT 取首轮总时长（§13.5 降级口径，遥测显式标注）
                record.TotalMs = Math.Max(0, Clock.ElapsedMs - submitStartMs);
                Ledger.EndRequest(record);
                Ledger.Save(BaseDir);
            }

            LastResult = result;
            Report(rounds, "wrap-up", growing: false);
            return result.ToResultJson();
        }

        // ── 收尾（策略 A） ───────────────────────────────────────────

        AgentResult WrapUp(SandboxTransaction sandbox, AgentRequestRecord record, int rounds,
            string stopReason, string finalText, string partialNote,
            List<AgentTrajectoryEntry> trajectory, List<string> warnings,
            bool timedOut, bool capWarned, IRandom rng, long tokenEff,
            string lastToolErrorCode = null, string lastToolErrorHint = null)
        {
            var modified = sandbox.BuildModifiedConfigs();
            // tokenEff 由循环侧传入（账本落账在后：EndRequest 会把同一数值权威重写进 record，
            // AgentSmokeProbe 断言两者逐值相等，防「循环内累计」与「账本累计」分叉）。

            if (modified.HasValues)
            {
                var scopes = sandbox.ChangedScopes;
                var karma = Formula.Compute((int)tokenEff, sandbox.Writes, success: true,
                    BuildKarmaContext(scopes), rng);
                record.Karma = karma.Karma;
                record.Roll = karma.Roll;
                record.Surged = karma.Surged;
                record.Scope = karma.DominantScope;
                record.ScopeWeight = karma.ScopeWeight;

                var clean = stopReason == "COMMIT" || stopReason == "TEXT";
                var message = clean ? (finalText ?? string.Empty) : StopMessage(stopReason, finalText);
                var result = new AgentResult
                {
                    Type = clean && string.IsNullOrEmpty(partialNote) ? "applied" : "partial",
                    Message = message,
                    ModifiedConfigs = modified,
                    EstimatedKarmaCost = karma.Karma,
                    PartialNote = partialNote ?? string.Empty,
                    TokenEff = tokenEff,
                    Rounds = rounds,
                    Writes = sandbox.Writes,
                    Trajectory = trajectory,
                    Karma = karma,
                    Scope = karma.DominantScope,
                    ScopeWeight = karma.ScopeWeight,
                    ScopeTags = scopes,
                    TimedOut = timedOut,
                    CapWarned = capWarned,
                    Warnings = warnings,
                };
                if (!clean && string.IsNullOrEmpty(partialNote))
                    result.PartialNote = StopMessage(stopReason, null);
                if (!clean)
                {
                    // 半成品也要带可诊断的错误码（UI/遥测；失败卡文案由 CheatErrorTranslator 决定）。
                    result.ErrorCode = stopReason;
                    result.Hint = lastToolErrorHint;
                }
                return result;
            }

            if (!string.IsNullOrEmpty(finalText))
                return AgentResult.Fun(finalText, tokenEff, rounds, trajectory, warnings);
            // 零写入失败：错误码取「到点 > 最近工具错误 > 停止原因」的优先序（翻译分支依赖它）。
            var code = timedOut ? "TIMEOUT"
                : !string.IsNullOrEmpty(lastToolErrorCode) ? lastToolErrorCode
                : stopReason;
            var failure = AgentResult.Failure("天道默然", StopMessage(stopReason, null),
                tokenEff, rounds, timedOut, trajectory, warnings, code, lastToolErrorHint);
            failure.CapWarned = capWarned;
            failure.Writes = sandbox.Writes;
            record.Karma = 0;
            return failure;
        }

        KarmaContext BuildKarmaContext(IReadOnlyList<string> scopes)
        {
            // E1 边界：道级平坦加数（畜生道 high_rank_piece +20）的条件判定归 E2/设计侧，
            // 此处 Flat=0 并预留 KarmaContext.Flat 字段（ seam 见回传）。
            return new KarmaContext
            {
                Realm = _karma.Realm ?? string.Empty,
                SingleMax = _karma.SingleMax,
                Flat = 0,
                EfficiencyFraud = _karma.EfficiencyFraud,
                HellHungryDiscount = _karma.HellHungryDiscount,
                HeavenAsuraDiscount = _karma.HeavenAsuraDiscount,
                ScopeTags = scopes,
            };
        }

        /// <summary>
        /// HTTP 400「The `reasoning_content` in the thinking mode must be passed back to the API」
        /// 的可自愈判据：只认 400 + 错误体含 reasoning_content，避免把真正的参数错误重试成双倍消耗。
        /// </summary>
        static bool IsThinkingProtocolRejection(LlmResult resp)
        {
            if (resp == null || resp.HttpStatus != 400 || string.IsNullOrEmpty(resp.Error))
                return false;
            return resp.Error.IndexOf("reasoning_content", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string StopMessage(string stopReason, string finalText)
        {
            switch (stopReason)
            {
                case "TIMEOUT": return "天道推演到点（20s），已为你保留可行变更";
                case "TOKEN_BUDGET": return "推演预算已尽，已为你保留可行变更";
                case "TOOL_ERRORS": return "天道屡屡受挫（连续工具错误），已为你保留可行变更";
                case "REPEAT_LOOP": return "天道陷入重复推演（已熔断），已为你保留可行变更";
                case "ROUNDS_EXHAUSTED": return "推演轮次已尽，已为你保留可行变更";
                case "TEXT": return finalText ?? string.Empty;
                default: return "推演中断，已为你保留可行变更";
            }
        }

        static JObject FailOffline()
        {
            var result = AgentResult.Failure("天道未联", OfflineMessage, 0, 0);
            return result.ToResultJson();
        }

        // ── 内部工具 ─────────────────────────────────────────────────

        /// <summary>
        /// 读工具结果压缩（§5.6「阅后即焚」的**可执行**口径）：
        /// 原始结果入列前压成「紧凑但保数据」的 JSON——保留模型决策所需的
        /// 棋子 id/type/side/position 与配置切片值，丢弃 _metadata/name/custom_properties 等装饰字段。
        ///
        /// 【缺陷修复 E3-S2】修复前回执是「[已压缩] …结果已阅」，模型从未见过配置内容，
        /// 只能反复猜 JSON Pointer（实测 PATH_NOT_FOUND/REPEAT_LOOP）。工具调用循环里
        /// 「模型看不到结果」等于没有读工具——压缩必须以不丢决策信息为前提。
        /// 失败结果仍全量回喂（错误文案必须可操作：合法值/范围/前缀）。
        ///
        /// 确定性：回执是 (tool, args, result) 的纯函数——无时间戳、无随机（可回放）。
        /// </summary>
        static string Receipt(string toolName, string argsJson, JObject result)
        {
            var ok = result.Value<bool>("ok");
            if (!ok)
                return result.ToString(Newtonsoft.Json.Formatting.None);

            switch (toolName)
            {
                case "get_config_slice":
                    return CompactSlice(result);
                case "get_board_state":
                    return CompactBoard(result);
                case "get_karma_state":
                    return result.ToString(Newtonsoft.Json.Formatting.None);
                default:
                    return result.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        /// <summary>配置切片压缩：保留 target/path/value（值上限 3,000 字符）。</summary>
        static string CompactSlice(JObject result)
        {
            var value = result["value"];
            var text = value == null ? "null" : value.ToString(Newtonsoft.Json.Formatting.None);
            if (text.Length > 3000)
                text = text.Substring(0, 3000) + "…[截断，用 path 取子树]";
            return "{\"ok\":true,\"target\":" + JsonConvert.ToString(result.Value<string>("target") ?? string.Empty)
                + ",\"path\":" + JsonConvert.ToString(result.Value<string>("path") ?? string.Empty)
                + ",\"value\":" + text
                + ",\"truncated\":" + (result.Value<bool>("truncated") ? "true" : "false") + "}";
        }

        /// <summary>棋盘压缩：只保留 id/type/side/position + 回合/机制/状态/尺寸/近手。</summary>
        static string CompactBoard(JObject result)
        {
            var compact = new JObject { ["ok"] = true };
            var state = result["board_state"] as JObject;
            if (state != null)
            {
                var bs = new JObject();
                foreach (var key in new[] { "current_turn", "mechanisms", "game_status", "board", "recent_moves" })
                {
                    var token = state[key];
                    if (token != null && token.Type != JTokenType.Null)
                        bs[key] = token.DeepClone();
                }
                var pieces = state["pieces"] as JArray;
                if (pieces != null)
                {
                    var slim = new JArray();
                    foreach (var token in pieces)
                    {
                        if (!(token is JObject p)) continue;
                        slim.Add(new JObject
                        {
                            ["id"] = p["id"]?.DeepClone(),
                            ["type"] = p["type"]?.DeepClone(),
                            ["side"] = p["side"]?.DeepClone(),
                            ["position"] = p["position"]?.DeepClone(),
                        });
                    }
                    bs["pieces"] = slim;
                }
                compact["board_state"] = bs;
            }
            compact["truncated"] = result.Value<bool>("truncated");
            var text = compact.ToString(Newtonsoft.Json.Formatting.None);
            if (text.Length > 3000)
            {
                // 极端大盘：截断并显式提示收窄查询（不静默丢数据）。
                text = text.Substring(0, 3000) + "…[截断，用 side/include 收窄]";
            }
            return text;
        }

        static string[] ReadAllowedTools(JObject context)
        {
            var token = context?["allowed_tools"] as JArray;
            if (token == null || token.Count == 0)
            {
                // 未提供门控集合 ⇒ 全量注册表（E2 前 Samsara 未接线的默认形态）。
                var all = new List<string>();
                foreach (var def in AgentToolSchemas.All) all.Add(def.Name);
                return all.ToArray();
            }
            var names = new List<string>();
            foreach (var t in token)
                if (t.Type == JTokenType.String) names.Add(t.Value<string>());
            return names.ToArray();
        }

        /// <summary>
        /// 读取 apply_config_patch 的作用域白名单（ADR-6 第三条：t1b 解锁 ui_config / board）。
        /// 缺键/null ⇒ 不限制（E1 兼容形态 + 黄金夹具）；空数组 ⇒ 全禁（有意与「缺键」区分）。
        /// </summary>
        static string[] ReadAllowedPatchTargets(JObject context)
        {
            var token = context?["allowed_patch_targets"] as JArray;
            if (token == null) return null;
            var names = new List<string>();
            foreach (var t in token)
                if (t.Type == JTokenType.String) names.Add(t.Value<string>());
            return names.ToArray();
        }

        static string[] DistinctTools(List<AgentTrajectoryEntry> trajectory)
        {
            var seen = new List<string>();
            foreach (var e in trajectory)
                if (!seen.Contains(e.ToolName)) seen.Add(e.ToolName);
            return seen.ToArray();
        }

        /// <summary>指令文本 SHA-256 前 12 位（不存原文，隐私；TokenLedger.CommandHash 契约）。</summary>
        static string CommandHash(string command)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(command ?? string.Empty));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>配置路径解析：baseDir 直下优先，其次 baseDir/Agent/（Bootstrap 两种传法都容错）。</summary>
        static string ResolveConfigPath(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return null;
            var direct = Path.Combine(baseDir, AgentConfigLoader.ConfigFileName);
            if (File.Exists(direct)) return direct;
            var nested = Path.Combine(baseDir, "Agent", AgentConfigLoader.ConfigFileName);
            if (File.Exists(nested)) return nested;
            return direct; // 让 loader 记「显式候选缺失」告警（兜底不等于静默）
        }
    }
}
