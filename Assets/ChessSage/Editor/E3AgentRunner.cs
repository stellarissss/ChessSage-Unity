using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Tools.E3
{
    /// <summary>
    /// E3 真实链路校准运行器（REMAKE-IMPL-E3B）。
    ///
    /// 【单一实现、双宿主】
    /// - Unity Editor：<c>ChessSage.Editor.E3AgentRunnerMenu</c> 的 [MenuItem] / batchmode 入口调用本类；
    /// - 引擎外快速迭代：<c>tools/verify/e3</c> 控制台工程直接调用本类（同一份源码）。
    /// 两宿主共用同一 Core 程序集语义，避免「Editor 能跑、CI 不能跑」的分叉。
    ///
    /// 【真实链路】真实 <see cref="LlmClient"/>（无 MockHandler）→ <see cref="AgentEngine"/> →
    /// <see cref="ToolRouter"/> 八工具 → <see cref="SchemaValidator.HardValidateConfig"/> 硬校验。
    ///
    /// 【安全】密钥只从环境变量 / config.json 经 <see cref="AiConfig.GetApiKey"/> 读取；
    /// 本类不打印、不落盘任何密钥；输出只含指令、用量、时延与工具轨迹。
    ///
    /// 【阶段】
    ///   smoke   ：两口径 base_url 各 N 次（默认 3）
    ///   perf    ：D/A/C 各 3 次（性能基线）
    ///   effort  ：M1/M2/M3 各 5 次同一 C 档任务（--effort 指定单变体）
    ///   calib   ：六档 × N 次（默认 20）
    ///   multi   ：三改齐发 × N 次（默认 5）
    ///   inspect ：只做本地体积测量（不调用 API，除非 --call）
    /// </summary>
    public static class E3AgentRunner
    {
        static readonly object UsageLock = new object();
        static readonly List<LlmUsage> RoundUsages = new List<LlmUsage>();

        static readonly Dictionary<string, string[]> TierCommands = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["E"] = new[] { "谁现在占优？", "现在轮到谁走棋？", "这局棋目前形势如何？" },
            ["D"] = new[] { "把我的棋子都变成金色", "把棋盘背景改成墨绿色", "把棋盘尺寸改成 11x11" },
            ["B"] = new[] { "把黑车移到（3,3）", "把黑马跳到（4,5）", "把红兵向前推一格" },
            ["A"] = new[] { "让AI这一回合不能动", "让黑方接下来两回合不能走棋", "让这局棋每三回合随机走一步" },
            ["C"] = new[] { "让我的马可以横着走", "让我的车可以斜着走", "让象可以过河" },
            ["C+"] = new[] { "给我造一个能拐弯的棋子", "给我加一个会飞的棋子", "造一个能斜着走两格的棋子" },
            ["MULTI"] = new[] { "把我的马改成能横着走、让AI这一回合不能动，再把棋盘背景改成墨绿色" },
        };

        public static int Run(string[] args)
        {
            E3Options opt;
            try
            {
                opt = E3Options.Parse(args ?? Array.Empty<string>());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[e3] 参数错误: " + ex.Message);
                return 64;
            }

            var root = ResolveRepoRoot(opt.Root);
            if (string.IsNullOrEmpty(root))
            {
                Console.Error.WriteLine("[e3] 找不到仓库根（含 Assets/ChessSage/Core/Agent）");
                return 66;
            }

            AiConfig.SearchRoot = root;
            AiConfig.ReloadConfig();
            SchemaValidator.SchemaDir = Path.Combine(root, "Assets", "StreamingAssets", "Schemas");

            var configPath = opt.ConfigPath ?? Path.Combine(root, "Assets", "StreamingAssets", "Agent", "agent_remake.json");
            var outDir = opt.OutDir ?? Path.Combine(root, "Logs", "e3", "raw");
            Directory.CreateDirectory(outDir);

            if (string.Equals(opt.Stage, "inspect", StringComparison.OrdinalIgnoreCase))
                return Inspect(root, configPath, opt, outDir);

            var baseUrl = ResolveBaseUrl(opt);
            var model = AiConfig.GetModel();
            var apiKey = AiConfig.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                Console.Error.WriteLine("[e3] 未找到 API 密钥（DEEPSEEK_API_KEY / config.json 均为空）");
                return 67;
            }

            var config = LoadConfigWithOverlay(configPath, opt);
            var objectives = LoadObjectiveTypes(root);
            var configs = LoadXiangqiConfigs(root);

            var client = new LlmClient(apiKey, baseUrl, model);
            var ledgerDir = Path.Combine(root, "Logs", "e3");
            var engine = new AgentEngine(client, ledgerDir, config, opt.Seed);
            engine.ConfigValidator = SchemaValidator.HardValidateConfig;
            engine.ObjectiveTypesProvider = () => objectives;
            engine.SyncKarmaState(new AgentKarmaState
            {
                Karma = 40, KarmaMax = 120, SingleMax = 120, Detection = 0.0, Realm = "human",
            });

            // 账本回调之外再加一路「逐轮用量」采集（multicast；不改变引擎/账本行为）。
            client.TokenUsageCallbackEx += usage =>
            {
                lock (UsageLock) RoundUsages.Add(usage);
            };

            var plan = BuildPlan(opt);
            var outPath = Path.Combine(outDir, opt.Tag + ".jsonl");
            var summaryPath = Path.Combine(outDir, opt.Tag + ".summary.json");
            if (File.Exists(outPath)) File.Delete(outPath);

            Console.WriteLine($"[e3] stage={opt.Stage} tag={opt.Tag} base={LabelOf(baseUrl, opt)} model={model} " +
                              $"effort={config.Performance.EffortProfile} timeout={config.Limits.HardTimeoutS}s runs={plan.Count}");
            Console.WriteLine($"[e3] config={configPath}");
            Console.WriteLine($"[e3] out={outPath}");

            var counts = new Dictionary<string, int[]>(StringComparer.Ordinal); // tier -> [runs, ok]
            var stopwatchAll = Stopwatch.StartNew();
            var fatal = false;
            var consecutiveFailures = 0;

            for (int i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                var line = RunOne(engine, client, configs, item, i + 1, baseUrl, opt, config);
                File.AppendAllText(outPath, line.ToString(Formatting.None) + "\n");

                var ok = line.Value<bool>("success");
                if (!counts.TryGetValue(item.Tier, out var c)) counts[item.Tier] = c = new int[2];
                c[0]++;
                if (ok) c[1]++;

                Console.WriteLine(
                    $"[e3] {item.Tier,-3} #{i + 1:000} type={line.Value<string>("result_type"),-8} " +
                    $"ok={ok,-5} karma={line.Value<int>("karma"),3} T={line.Value<int>("token_eff"),6} " +
                    $"scope={line.Value<string>("scope"),-11} rounds={line.Value<int>("rounds")} " +
                    $"total={line.Value<int>("total_ms"),6}ms ttft={line.Value<int>("ttft_ms"),5}ms " +
                    $"cached2={line.Value<int>("round2_cached"),5} tools={Shorten(line.Value<string>("tool_path"), 44)}");

                if (line.Value<bool>("fatal"))
                {
                    fatal = true;
                    Console.Error.WriteLine("[e3] 致命错误（401/402/429 或 400），立即停止：" + line.Value<string>("exception"));
                    break;
                }

                if (!ok) consecutiveFailures++; else consecutiveFailures = 0;
                if (consecutiveFailures >= 5)
                {
                    fatal = true;
                    Console.Error.WriteLine("[e3] 连续 5 次失败，停止以避免盲烧额度");
                    break;
                }
            }

            stopwatchAll.Stop();
            var summary = new JObject
            {
                ["stage"] = opt.Stage,
                ["tag"] = opt.Tag,
                ["base_label"] = LabelOf(baseUrl, opt),
                ["model"] = model,
                ["effort_profile"] = config.Performance.EffortProfile,
                ["max_rounds"] = config.Limits.MaxRounds,
                ["per_round_max_tokens"] = new JObject
                {
                    ["tool"] = config.Performance.ToolRoundMaxTokens,
                    ["final"] = config.Performance.FinalRoundMaxTokens,
                },
                ["planned"] = plan.Count,
                ["executed"] = counts.Values.Sum(v => v[0]),
                ["fatal"] = fatal,
                ["elapsed_s"] = Math.Round(stopwatchAll.Elapsed.TotalSeconds, 1),
                ["tiers"] = BuildTierSummary(counts),
            };
            File.WriteAllText(summaryPath, summary.ToString(Formatting.Indented));
            Console.WriteLine($"[e3] 汇总：{summary["executed"]}/{plan.Count} 次，耗时 {summary["elapsed_s"]}s，fatal={fatal}");
            Console.WriteLine($"[e3] summary={summaryPath}");
            return fatal ? 2 : 0;
        }

        // ── 单次运行 ─────────────────────────────────────────────────

        static JObject RunOne(AgentEngine engine, LlmClient client, JObject configs,
            PlanItem item, int seq, string baseUrl, E3Options opt, AgentRemakeConfig config)
        {
            lock (UsageLock) RoundUsages.Clear();

            var line = new JObject
            {
                ["stage"] = opt.Stage,
                ["tag"] = opt.Tag,
                ["tier"] = item.Tier,
                ["seq"] = seq,
                ["run_in_tier"] = item.RunInTier,
                ["base_label"] = LabelOf(baseUrl, opt),
                ["base_url"] = baseUrl,
                ["effort"] = config.Performance.EffortProfile,
                ["command"] = item.Command,
                ["ts"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            };

            var wall = Stopwatch.StartNew();
            var httpStatus = 0;
            AgentResult result = null;

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.Limits.HardTimeoutS)))
                {
                    engine.ProcessCommandAsync(item.Command, BuildContext(configs),
                        cts.Token).GetAwaiter().GetResult();
                }
                result = engine.LastResult;
                httpStatus = 200; // 成功解析响应即 HTTP 2xx（失败在 catch 中解析状态码）
            }
            catch (Exception ex)
            {
                var status = ParseHttpStatus(ex.Message);
                httpStatus = status;
                // 只有「配额/鉴权」类错误才终结整批（401/402/429）。
                // 400 是**单次请求**的协议错误（实测 C+ #47：thinking 模式未回传 reasoning_content），
                // 引擎已内建自愈重试；此处仍把它记为一条失败样本继续跑，绝不 fatal 掉整批校准。
                line["fatal"] = status == 401 || status == 402 || status == 429;
                line["exception"] = Sanitize(ex.Message);
            }
            finally
            {
                wall.Stop();
            }

            line["http_status"] = httpStatus;
            line["wall_ms"] = (int)Math.Min(int.MaxValue, wall.ElapsedMilliseconds);
            line["success"] = false;
            line["schema_ok"] = false;
            line["tool_path"] = string.Empty;
            line["round2_cached"] = 0;
            line["karma"] = 0;
            line["token_eff"] = 0;
            line["rounds"] = 0;
            line["writes"] = 0;
            line["timed_out"] = false;
            line["cap_warned"] = false;

            if (result != null)
            {
                var modified = result.ModifiedConfigs;
                var modifiedKeys = new List<string>();
                var schemaOk = true;
                var schemaErrors = new List<string>();
                if (modified != null)
                {
                    foreach (var prop in modified.Properties())
                    {
                        modifiedKeys.Add(prop.Name);
                        var slice = prop.Value as JObject;
                        if (slice == null) continue;
                        var (ok, error) = SchemaValidator.HardValidateConfig(prop.Name, slice);
                        if (!ok)
                        {
                            schemaOk = false;
                            schemaErrors.Add(prop.Name + ": " + error);
                        }
                    }
                }

                var tools = new JArray();
                var path = new List<string>();
                foreach (var t in result.Trajectory)
                {
                    tools.Add(new JObject
                    {
                        ["round"] = t.Round,
                        ["name"] = t.ToolName,
                        ["ok"] = t.Ok,
                        ["error"] = t.ErrorCode,
                        ["args_len"] = (t.ArgumentsJson ?? string.Empty).Length,
                        ["args"] = Shorten(t.ArgumentsJson ?? string.Empty, 400),
                    });
                    path.Add(t.ToolName + (t.Ok ? "" : "!"));
                }

                // 成功口径：E = 零写入 fun；其余 = applied/partial 且至少一个切片真实写入、
                // schema 硬校验通过、未到点（到点 partial 单独记 timed_out，不计成功）。
                var hasWrites = modifiedKeys.Count > 0 && result.Writes > 0;
                var success = item.Tier == "E"
                    ? result.Type == "fun" && result.Writes == 0 && schemaOk
                    : (result.Type == "applied" || result.Type == "partial")
                      && hasWrites && schemaOk && !result.TimedOut;

                line["success"] = success;
                line["schema_ok"] = schemaOk;
                if (schemaErrors.Count > 0)
                {
                    var arr = new JArray();
                    foreach (var e in schemaErrors) arr.Add(e);
                    line["schema_errors"] = arr;
                }
                line["result_type"] = result.Type;
                line["karma"] = result.EstimatedKarmaCost;
                line["token_eff"] = (int)Math.Min(int.MaxValue, result.TokenEff);
                line["rounds"] = result.Rounds;
                line["writes"] = result.Writes;
                line["timed_out"] = result.TimedOut;
                line["cap_warned"] = result.CapWarned;
                line["error_code"] = result.ErrorCode ?? string.Empty;
                line["message"] = Shorten(result.Message ?? string.Empty, 400);
                var keysArr = new JArray();
                foreach (var k in modifiedKeys) keysArr.Add(k);
                line["modified_keys"] = keysArr;
                line["tools"] = tools;
                line["tool_path"] = string.Join(">", path.ToArray());
                // E3-C 校准审计：作用域是补丁的机械属性，逐次落盘才能复盘「同 T_eff 不同价」。
                line["scope"] = result.Scope ?? string.Empty;
                line["scope_weight"] = result.ScopeWeight;
                var scopeTags = new JArray();
                if (result.ScopeTags != null)
                    foreach (var s in result.ScopeTags) scopeTags.Add(s);
                line["scopes"] = scopeTags;
                if (result.Warnings != null && result.Warnings.Count > 0)
                {
                    var warningArray = new JArray();
                    foreach (var w in result.Warnings) warningArray.Add(w);
                    line["warnings"] = warningArray;
                }
                if (result.Karma != null)
                {
                    line["roll"] = Math.Round(result.Karma.Roll, 4);
                    line["surged"] = result.Karma.Surged;
                }
            }

            // 账本侧权威遥测（本提交记录）。
            var record = engine.Ledger.Requests.Count > 0
                ? engine.Ledger.Requests[engine.Ledger.Requests.Count - 1]
                : null;
            if (record != null)
            {
                line["prompt_tokens"] = record.PromptTokens;
                line["completion_tokens"] = record.CompletionTokens;
                line["reasoning_tokens"] = record.ReasoningTokens;
                line["cached_prompt_tokens"] = record.CachedPromptTokens;
                line["prefix_hit_rate"] = Math.Round(record.PrefixHitRate, 4);
                line["ttft_ms"] = record.TtftMs;
                line["total_ms"] = record.TotalMs;
                var rms = new JArray();
                if (record.RoundMs != null) foreach (var ms in record.RoundMs) rms.Add(ms);
                line["round_ms"] = rms;
                line["t_eff_record"] = record.TokenEff;
            }

            var roundUsage = new JArray();
            int round2Cached = 0;
            lock (UsageLock)
            {
                for (int i = 0; i < RoundUsages.Count; i++)
                {
                    var u = RoundUsages[i];
                    roundUsage.Add(new JObject
                    {
                        ["round"] = i + 1,
                        ["prompt"] = u.PromptTokens,
                        ["completion"] = u.CompletionTokens,
                        ["reasoning"] = u.ReasoningTokens,
                        ["cached"] = u.CachedPromptTokens,
                    });
                    if (i == 1) round2Cached = u.CachedPromptTokens;
                }
            }
            line["round_usage"] = roundUsage;
            line["round2_cached"] = round2Cached;

            return line;
        }

        static JObject BuildContext(JObject configs)
        {
            var allowedTools = new JArray();
            foreach (var def in AgentToolSchemas.All) allowedTools.Add(def.Name);

            return new JObject
            {
                ["configs"] = configs.DeepClone(),
                ["allowed_tools"] = allowedTools,
                ["allowed_patch_targets"] = new JArray(
                    "board", "rules", "ui_config", "pieces_red", "pieces_black", "pieces_white", "board_state"),
            };
        }

        static JObject BuildSizes(JObject configs)
        {
            var obj = new JObject();
            foreach (var prop in configs.Properties())
                obj[prop.Name] = (prop.Value as JObject)?.ToString(Formatting.None).Length ?? 0;
            return obj;
        }

        static JObject BuildTierSummary(Dictionary<string, int[]> counts)
        {
            var obj = new JObject();
            foreach (var kv in counts)
                obj[kv.Key] = new JObject { ["runs"] = kv.Value[0], ["success"] = kv.Value[1] };
            return obj;
        }

        // ── 计划 ─────────────────────────────────────────────────────

        sealed class PlanItem
        {
            public string Tier;
            public string Command;
            public int RunInTier;
        }

        static List<PlanItem> BuildPlan(E3Options opt)
        {
            var plan = new List<PlanItem>();
            switch (opt.Stage.ToLowerInvariant())
            {
                case "smoke":
                    AppendTier(plan, "C", Math.Max(1, opt.Runs), new[] { "让我的马可以横着走" });
                    break;

                case "perf":
                    AppendTier(plan, "D", 3);
                    AppendTier(plan, "A", 3);
                    AppendTier(plan, "C", 3);
                    break;

                case "effort":
                    AppendTier(plan, "C", Math.Max(1, opt.Runs), new[] { "让我的马可以横着走" });
                    break;

                case "calib":
                    foreach (var tier in SelectedTiers(opt))
                        AppendTier(plan, tier, Math.Max(1, opt.Runs));
                    break;

                case "multi":
                    AppendTier(plan, "MULTI", Math.Max(1, opt.Runs));
                    break;

                default:
                    throw new ArgumentException("未知 stage: " + opt.Stage + "（smoke/perf/effort/calib/multi/inspect）");
            }
            return plan;
        }

        static IEnumerable<string> SelectedTiers(E3Options opt)
        {
            var all = new[] { "E", "D", "B", "A", "C", "C+" };
            if (string.IsNullOrEmpty(opt.Tiers)) return all;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in opt.Tiers.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                wanted.Add(t.Trim());
            return all.Where(t => wanted.Contains(t));
        }

        static void AppendTier(List<PlanItem> plan, string tier, int runs, string[] commands = null)
        {
            var pool = commands ?? (TierCommands.TryGetValue(tier, out var c) ? c : new[] { "让我的马可以横着走" });
            for (int i = 0; i < runs; i++)
                plan.Add(new PlanItem { Tier = tier, Command = pool[i % pool.Length], RunInTier = i + 1 });
        }

        // ── 配置 / 资源 ──────────────────────────────────────────────

        static AgentRemakeConfig LoadConfigWithOverlay(string path, E3Options opt)
        {
            var root = ReadJson(path);
            var perf = root["performance"] as JObject;
            if (perf == null) root["performance"] = perf = new JObject();

            if (!string.IsNullOrEmpty(opt.Effort)) perf["effort_profile"] = opt.Effort;
            if (opt.MaxRounds.HasValue)
            {
                var limits = root["limits"] as JObject;
                if (limits == null) root["limits"] = limits = new JObject();
                limits["max_rounds"] = opt.MaxRounds.Value;
            }
            if (opt.ToolMaxTokens.HasValue || opt.FinalMaxTokens.HasValue)
            {
                var tokens = perf["per_round_max_tokens"] as JObject;
                if (tokens == null) perf["per_round_max_tokens"] = tokens = new JObject();
                if (opt.ToolMaxTokens.HasValue) tokens["tool"] = opt.ToolMaxTokens.Value;
                if (opt.FinalMaxTokens.HasValue) tokens["final"] = opt.FinalMaxTokens.Value;
            }

            return AgentRemakeConfig.FromJson(root).WithSource(path + "#overlay");
        }

        static JObject LoadXiangqiConfigs(string root)
        {
            var dir = Path.Combine(root, "Assets", "StreamingAssets", "Configs", "xiangqi");
            var cfg = new JObject();
            foreach (var name in new[] { "board_state", "board", "rules", "ui_config", "pieces_red", "pieces_black" })
            {
                var p = Path.Combine(dir, name + ".json");
                if (File.Exists(p)) cfg[name] = ReadJson(p);
            }
            return cfg;
        }

        static IReadOnlyList<string> LoadObjectiveTypes(string root)
        {
            var p = Path.Combine(root, "Assets", "StreamingAssets", "Configs", "global", "objective_types.json");
            var list = new List<string>();
            if (!File.Exists(p)) return list;
            var json = ReadJson(p);
            foreach (var prop in json.Properties()) list.Add(prop.Name);
            return list;
        }

        static JObject ReadJson(string path)
        {
            var text = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<JObject>(text, new JsonSerializerSettings
            {
                DateParseHandling = DateParseHandling.None,
            }) ?? new JObject();
        }

        // ── inspect：体积测量（可选真实调用） ────────────────────────

        static int Inspect(string root, string configPath, E3Options opt, string outDir)
        {
            var allowed = new JArray();
            foreach (var def in AgentToolSchemas.All) allowed.Add(def.Name);
            var allowedList = allowed.Select(t => t.Value<string>()).ToList();
            var system = AgentPrompts.BuildSystem(allowedList);
            var toolsJson = AgentToolSchemas.SerializeTools(AgentToolSchemas.ForAllowedTools(allowedList));
            var configs = LoadXiangqiConfigs(root);
            var user = AgentPrompts.BuildUser("让我的马可以横着走",
                BoardSummarizer.Summarize(configs["board_state"] as JObject, configs["board"] as JObject));

            var info = new JObject
            {
                ["system_chars"] = system.Length,
                ["system_utf8_bytes"] = System.Text.Encoding.UTF8.GetByteCount(system),
                ["system_est_tokens"] = AgentPrompts.EstimateTokens(system),
                ["tools_chars"] = toolsJson.Length,
                ["tools_est_tokens"] = AgentPrompts.EstimateTokens(toolsJson),
                ["user_chars"] = user.Length,
                ["user_est_tokens"] = AgentPrompts.EstimateTokens(user),
                ["tool_count"] = allowedList.Count,
                ["config_sizes"] = BuildSizes(configs),
            };

            if (opt.Call)
            {
                var client = new LlmClient(AiConfig.GetApiKey(), ResolveBaseUrl(opt), AiConfig.GetModel());
                var messages = new List<LlmMessage>
                {
                    new LlmMessage { Role = "system", Content = system },
                    new LlmMessage { Role = "user", Content = user },
                };
                var tools = JArray.Parse(toolsJson);
                var res = client.ChatToolsAsync(messages, tools, maxTokens: 1, noThink: true)
                    .GetAwaiter().GetResult();
                info["call_ok"] = res.Ok;
                info["call_error"] = res.Ok ? (JToken)null : Sanitize(res.Error);
                if (res.Ok && res.Usage != null)
                {
                    info["prompt_tokens_exact"] = res.Usage.PromptTokens;
                    info["completion_tokens"] = res.Usage.CompletionTokens;
                    info["cached_tokens"] = res.Usage.CachedPromptTokens;
                }
            }

            var path = Path.Combine(outDir, (opt.Tag ?? "inspect") + ".json");
            File.WriteAllText(path, info.ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(outDir, (opt.Tag ?? "inspect") + ".system.txt"), system);
            File.WriteAllText(Path.Combine(outDir, (opt.Tag ?? "inspect") + ".tools.json"), toolsJson);
            Console.WriteLine(info.ToString(Formatting.Indented));
            Console.WriteLine("[e3] inspect=" + path);
            return 0;
        }

        // ── 工具 ─────────────────────────────────────────────────────

        static string ResolveBaseUrl(E3Options opt)
        {
            if (!string.IsNullOrEmpty(opt.BaseUrl)) return opt.BaseUrl.TrimEnd('/');
            if (string.Equals(opt.Base, "B", StringComparison.OrdinalIgnoreCase))
                return "https://api.deepseek.com";
            if (string.Equals(opt.Base, "A", StringComparison.OrdinalIgnoreCase))
                return AiConfig.DefaultBaseUrl;
            return AiConfig.GetBaseUrl().TrimEnd('/');
        }

        static string LabelOf(string baseUrl, E3Options opt)
        {
            if (string.Equals(baseUrl, "https://api.deepseek.com", StringComparison.OrdinalIgnoreCase)) return "B";
            if (string.Equals(baseUrl, "https://api.deepseek.com/v1", StringComparison.OrdinalIgnoreCase)) return "A";
            return opt.Base ?? "config";
        }

        static string ResolveRepoRoot(string explicitRoot)
        {
            if (!string.IsNullOrEmpty(explicitRoot) &&
                Directory.Exists(Path.Combine(explicitRoot, "Assets", "ChessSage", "Core", "Agent")))
                return Path.GetFullPath(explicitRoot);

            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                if (string.IsNullOrEmpty(start)) continue;
                DirectoryInfo dir;
                try { dir = new DirectoryInfo(start); }
                catch { continue; }
                for (int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent)
                {
                    try
                    {
                        if (Directory.Exists(Path.Combine(dir.FullName, "Assets", "ChessSage", "Core", "Agent")))
                            return dir.FullName;
                    }
                    catch { break; }
                }
            }
            return null;
        }

        static int ParseHttpStatus(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            var m = Regex.Match(message, @"HTTP\s+(\d{3})");
            return m.Success && int.TryParse(m.Groups[1].Value, out var code) ? code : 0;
        }

        static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 防御：任何形如 sk-... 的串都不许进入报告（即使上游错误体回显）。
            return Regex.Replace(text, @"sk-[0-9a-zA-Z_\-]{8,}", "sk-***");
        }

        static string Shorten(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    /// <summary>命令行参数（双宿主共用；Editor 侧从环境变量拼装）。</summary>
    public sealed class E3Options
    {
        public string Stage = "smoke";
        public string Tag = "run";
        public string Base;          // A | B
        public string BaseUrl;
        public string Effort;        // M1 | M2 | M3
        public string Tiers;         // calib: E,D,B,A,C,C+
        public int Runs = 3;
        public int Seed = 20261023;
        public int? MaxRounds;
        public int? ToolMaxTokens;
        public int? FinalMaxTokens;
        public string Root;
        public string ConfigPath;
        public string OutDir;
        public bool Call;

        public static E3Options Parse(string[] args)
        {
            var o = new E3Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--stage": o.Stage = Next(args, ref i); break;
                    case "--tag": o.Tag = Next(args, ref i); break;
                    case "--base": o.Base = Next(args, ref i); break;
                    case "--base-url": o.BaseUrl = Next(args, ref i); break;
                    case "--effort": o.Effort = Next(args, ref i); break;
                    case "--tiers": o.Tiers = Next(args, ref i); break;
                    case "--runs": o.Runs = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--seed": o.Seed = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--max-rounds": o.MaxRounds = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--tool-max-tokens": o.ToolMaxTokens = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--final-max-tokens": o.FinalMaxTokens = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--root": o.Root = Next(args, ref i); break;
                    case "--config": o.ConfigPath = Next(args, ref i); break;
                    case "--out": o.OutDir = Next(args, ref i); break;
                    case "--call": o.Call = true; break;
                    default: throw new ArgumentException("未知参数: " + a);
                }
            }
            if (string.IsNullOrEmpty(o.Tag)) o.Tag = o.Stage;
            return o;
        }

        static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("参数 " + args[i] + " 缺少值");
            return args[++i];
        }
    }
}
