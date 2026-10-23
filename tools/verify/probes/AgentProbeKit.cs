using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using ChessSage.Core.Cheat;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Orchestration;
using ChessSage.Core.Samsara;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>
    /// E3-S4 六个 agent 探针的共享基座（**非探针**：类名不以 Probe 结尾，
    /// <see cref="Program"/> 的反射发现不会误收，因而无需注册）。
    ///
    /// 纪律（与 QA §4.4.1 通则一致）：
    ///   · 零网络：一律走 <see cref="LlmClient.MockToolHandler"/>（LlmTransport 缝）。
    ///   · 零 wall-clock：时间经 <see cref="VirtualClock"/> 推进，脚本不 sleep。
    ///   · mock 脚本耗尽即抛（禁静默错位，QA §2.2-A）。
    ///   · 期望值独立推导：<see cref="DesignContract"/> 用设计侧 §1.4 的字面量锚点重算 f，
    ///     **不调用 <see cref="KarmaFormula"/>**（防「拿实现验实现」）。
    /// </summary>
    public static class AgentProbeKit
    {
        public const int DefaultSeed = 20260101;

        /// <summary>棋盘/切片黄金夹具（与 <c>AgentTestRig.GoldenConfigs</c> 同构；L1 不编译 Tests，故此处自带）。</summary>
        public static JObject GoldenConfigs() => JObject.Parse(@"{
              ""board_state"": {
                ""pieces"": [
                  {""id"": ""r_chariot_1"", ""type"": ""chariot"", ""name"": ""车"", ""side"": ""red"", ""position"": [0, 9], ""is_alive"": true},
                  {""id"": ""b_horse_1"", ""type"": ""horse"", ""name"": ""马"", ""side"": ""black"", ""position"": [1, 0], ""is_alive"": true}
                ],
                ""current_turn"": ""red"",
                ""move_history"": [],
                ""game_status"": {""state"": ""playing""},
                ""mechanisms"": {""skip_turns"": [], ""ai_control"": []}
              },
              ""board"": {""width"": 9, ""height"": 10},
              ""rules"": {""ai_difficulty"": {""personality"": ""calm""}},
              ""ui_config"": {""theme"": ""dark""},
              ""pieces_red"": {""pieces"": {""chariot"": {""label"": ""车"", ""moves"": []},
                                            ""horse"": {""label"": ""馬"", ""moves"": [{""kind"": ""jump"", ""to"": [1, 2], ""block"": [[0, 1]], ""land"": ""any"", ""sym"": ""rotate4_mirror""}]}}}
            }");

        /// <summary>全工具名（取自注册表本身，避免探针里再抄一份名单）。</summary>
        public static JArray AllTools()
        {
            var array = new JArray();
            foreach (var def in AgentToolSchemas.All) array.Add(def.Name);
            return array;
        }

        public static JObject Context(JArray allowedTools = null, JObject configs = null) => new JObject
        {
            ["configs"] = configs ?? GoldenConfigs(),
            ["allowed_tools"] = allowedTools ?? AllTools(),
        };

        public static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>带 schema 的工程引擎（校验器接真实 <see cref="SchemaValidator"/>，而非总是放行的 fake）。</summary>
        public static AgentEngine NewEngine(ScriptedTransport transport, int seed = DefaultSeed,
            AgentRemakeConfig config = null, Action<AgentEngine> configure = null)
        {
            var client = new LlmClient(apiKey: "probe-mock-key", baseUrl: "https://probe.invalid/v1",
                model: AiConfig.DefaultModelName);
            client.MockToolHandler = transport.Handler;

            var engine = new AgentEngine(client, null, config ?? AgentRemakeConfig.Default, seed)
            {
                // 延迟遥测走虚拟时钟：断言确定、零 wall-clock（§10.1 时钟缝）
                Clock = transport.Clock,
            };
            engine.ConfigValidator = (name, cfg) => RunValidator(name, cfg);
            engine.ObjectiveTypesProvider = () => new[] { "checkmate", "capture_all", "survive_turns", "reach_position" };
            engine.SyncKarmaState(new AgentKarmaState
            {
                Karma = 50, KarmaMax = 120, SingleMax = 120, Detection = 0.0, Realm = "human",
            });
            configure?.Invoke(engine);
            return engine;
        }

        /// <summary>
        /// 硬校验注入（真实 Schema）。<c>objective</c> 与 <c>ui_config</c> 的宽松处理沿用工具层语义：
        /// 只有 Schema 目录里确有对应文件才校验；缺文件时报错而不是静默放行（与生产一致）。
        /// </summary>
        static (bool ok, string error) RunValidator(string name, JObject cfg)
        {
            var schemas = Path.Combine(ProbeEnv.RepoRoot, "Assets", "StreamingAssets", "Schemas");
            if (Directory.Exists(schemas)) SchemaValidator.SchemaDir = schemas;
            return SchemaValidator.HardValidateConfig(name, cfg);
        }

        public static AgentResult Run(AgentEngine engine, string command, CancellationToken? token = null,
            IProgress<AgentProgress> progress = null, JObject context = null)
        {
            var task = engine.ProcessCommandAsync(command, context ?? Context(),
                token ?? CancellationToken.None, progress);
            task.GetAwaiter().GetResult();
            return engine.LastResult;
        }

        // ────────────────────────────────────────────────────────────────
        // 设计侧 §1.4 合同的**独立**镜像（探针侧字面量，永不调用实现）
        // ────────────────────────────────────────────────────────────────

        public static class DesignContract
        {
            /// <summary>
            /// 镜像版本标签：设计侧 §1.4 v2 —— E3-C 增补「作用域深度权重」（锚点表本身未变）。
            /// 改锚点或权重必须先改本标签并走 §3.6 治理。
            /// </summary>
            public const string Version = "design-§1.4-v2(e3c-scope)";

            /// <summary>六档锚点：[token, karma] 字面量（顺序与设计表一致 E/D/B/A/C/C+）。</summary>
            public static readonly int[][] Anchors =
            {
                new[] { 2500, 1 },    // E  问讯/纯对话
                new[] { 8000, 16 },   // D  微调（界面/尺寸）
                new[] { 12000, 32 },  // B  移子
                new[] { 15000, 48 },  // A  机制
                new[] { 16000, 55 },  // C  改规
                new[] { 21000, 91 },  // C+ 铸子
            };

            public static readonly string[] Ids = { "E", "D", "B", "A", "C", "C+" };
            public static readonly int[] RangeMin = { 1, 13, 26, 38, 44, 73 };
            public static readonly int[] RangeMax = { 5, 21, 42, 62, 72, 120 };

            /// <summary>
            /// 作用域深度权重字面量（E3-C 合同；探针侧独立镜像，不读实现）：
            /// ui_config 1.0 / board_state 2.9 / pieces 1.85 / rules 3.35 / objectives 3.35。
            /// </summary>
            public static readonly string[] ScopeIds = { "ui_config", "board_state", "pieces", "rules", "objectives" };
            public static readonly double[] ScopeWeights = { 1.0, 2.9, 1.85, 3.35, 3.35 };

            public static double ScopeWeightOf(string scope)
            {
                for (int i = 0; i < ScopeIds.Length; i++)
                    if (string.Equals(ScopeIds[i], scope, StringComparison.Ordinal))
                        return ScopeWeights[i];
                return 1.0;
            }

            /// <summary>平地价区上界（T &lt; floor ⇒ f = 1）。</summary>
            public const int FloorToken = 2500;

            /// <summary>末段斜率（设计侧 §1.5：触顶 120 落在 ≈25K token）。</summary>
            public const double ExtendSlope = 0.0072;

            /// <summary>分段线性凸函数的独立实现（字面量系数，与 KarmaFormula 无共享代码）。</summary>
            public static double F(int tokenEff)
            {
                if (tokenEff < 0) throw new ArgumentOutOfRangeException(nameof(tokenEff));
                if (tokenEff < FloorToken) return 1.0;
                if (tokenEff <= Anchors[0][0]) return Anchors[0][1];
                for (int i = 1; i < Anchors.Length; i++)
                {
                    if (tokenEff <= Anchors[i][0])
                    {
                        var a = Anchors[i - 1];
                        var b = Anchors[i];
                        return a[1] + (double)(b[1] - a[1]) * (tokenEff - a[0]) / (b[0] - a[0]);
                    }
                }
                var last = Anchors[Anchors.Length - 1];
                return last[1] + (tokenEff - last[0]) * ExtendSlope;
            }

            public static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

            public static int Clamp(int value, int lo, int hi) => value < lo ? lo : (value > hi ? hi : value);

            /// <summary>
            /// 期望业力（独立推导）：
            /// clamp(round(f(T) × roll × scopeWeight) + flat, 1, singleMax)。
            /// scopeWeight 默认 1.0 ⇒ 旧口径逐值不变。
            /// </summary>
            public static int ExpectedKarma(int tokenEff, double roll, int singleMax = 120, int flat = 0,
                double scopeWeight = 1.0)
                => Clamp(Round(F(tokenEff) * roll * scopeWeight) + flat, 1, singleMax);
        }
    }

    /// <summary>固定随机源（roll 端点按压用）：<c>NextDouble()</c> 恒返回构造值。</summary>
    public sealed class FixedRandom : IRandom
    {
        readonly double _value;
        public int Calls;

        public FixedRandom(double value) { _value = value; }

        public double NextDouble()
        {
            Calls++;
            return _value;
        }

        /// <summary>让 σ = 1.0 的取值：σ = 0.8 + v × (1.3 − 0.8) ⇒ v = 0.4（且 0.4 ≥ 0.3 不触发激增）。</summary>
        public static FixedRandom UnitRoll() => new FixedRandom(0.4);
    }

    /// <summary>
    /// 虚拟时钟：只被显式推进，绝不读系统时间（QA §4.3 零 wall-clock 纪律）。
    /// 同时实现 <see cref="IMonotonicClock"/>，可直接注入 <see cref="AgentEngine.Clock"/>，
    /// 使 ttft_ms / round_ms / total_ms 完全可断言。
    /// </summary>
    public sealed class VirtualClock : IMonotonicClock
    {
        long _ms;
        public long Ms => _ms;
        /// <summary>单调时钟接口（引擎遥测用）：与 <see cref="Ms"/> 同源。</summary>
        public long ElapsedMs => _ms;
        public DateTime Origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void Advance(long ms)
        {
            if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms));
            _ms += ms;
        }

        public DateTime UtcNow => Origin.AddMilliseconds(_ms);
    }

    /// <summary>一次被捕获的请求（mock 侧快照；引擎会继续改写 messages，故必须当场序列化）。</summary>
    public sealed class CapturedRequest
    {
        public int Round;
        public string MessagesJson;
        public string ToolsJson;
        public int MessageCount;
        public int? MaxTokens;
        public string ReasoningEffort;
        /// <summary>messages 序列化 SHA-256（前缀稳定性/两跑一致性断言用）。</summary>
        public string PrefixHash;
        /// <summary>system 段单独哈希（R1/R2：system 必须恒定）。</summary>
        public string SystemHash;
        public string SystemContent;
    }

    /// <summary>
    /// 队列式 mock 传输（QA §2.2-A）：严格按调用次序出牌，耗尽即抛。
    /// 每轮可挂 <see cref="BeforeRound"/> 钩子（虚拟时钟推进 / 取消令牌）。
    /// </summary>
    public sealed class ScriptedTransport
    {
        readonly List<JObject> _rounds = new List<JObject>();

        public readonly List<CapturedRequest> Requests = new List<CapturedRequest>();
        public readonly VirtualClock Clock = new VirtualClock();

        /// <summary>第 N 轮（1 起）进入前调用；可推进虚拟时钟或取消令牌。</summary>
        public Action<int> BeforeRound;

        /// <summary>每轮虚拟耗时（ms）；0 = 不推进（默认）。</summary>
        public long VirtualRoundMs;

        public int RoundCount => _rounds.Count;

        public ScriptedTransport Tool(string name, string argumentsJson, int prompt, int completion,
            int reasoning = 0, int cached = 0, string callId = null)
        {
            var calls = new JArray
            {
                new JObject
                {
                    ["id"] = callId ?? ("call_" + (_rounds.Count + 1)),
                    ["name"] = name,
                    ["arguments"] = argumentsJson,
                },
            };
            return Calls(calls, prompt, completion, reasoning, cached);
        }

        public ScriptedTransport Calls(JArray toolCalls, int prompt, int completion, int reasoning = 0, int cached = 0)
            => Add(new JObject
            {
                ["content"] = null,
                ["tool_calls"] = toolCalls,
                ["finish_reason"] = "tool_calls",
                ["usage"] = Usage(prompt, completion, reasoning, cached),
            });

        public ScriptedTransport Text(string content, int prompt, int completion, int reasoning = 0, int cached = 0)
            => Add(new JObject
            {
                ["content"] = content,
                ["finish_reason"] = "stop",
                ["usage"] = Usage(prompt, completion, reasoning, cached),
            });

        public ScriptedTransport Truncated(string content, int prompt, int completion)
            => Add(new JObject
            {
                ["content"] = content,
                ["finish_reason"] = "length",
                ["usage"] = Usage(prompt, completion, 0, 0),
            });

        public ScriptedTransport Add(JObject round)
        {
            _rounds.Add(round);
            return this;
        }

        static JObject Usage(int prompt, int completion, int reasoning, int cached) => new JObject
        {
            ["prompt_tokens"] = prompt,
            ["completion_tokens"] = completion,
            ["reasoning_tokens"] = reasoning,
            ["cached_prompt_tokens"] = cached,
        };

        public LlmTransport Handler => request => Task.FromResult(Handle(request));

        LlmRawResponse Handle(LlmRequest request)
        {
            if (VirtualRoundMs > 0) Clock.Advance(VirtualRoundMs);

            var messages = request?.Messages ?? new List<LlmMessage>();
            var messagesJson = LlmClient.SerializeMessages(messages).ToString(Formatting.None);
            var captured = new CapturedRequest
            {
                Round = Requests.Count + 1,
                MessagesJson = messagesJson,
                ToolsJson = request?.Tools != null ? request.Tools.ToString(Formatting.None) : string.Empty,
                MessageCount = messages.Count,
                MaxTokens = request?.MaxTokens,
                ReasoningEffort = request?.ReasoningEffort,
                PrefixHash = AgentProbeKit.Sha256(messagesJson),
                SystemContent = messages.Count > 0 ? messages[0].Content : null,
            };
            captured.SystemHash = AgentProbeKit.Sha256(captured.SystemContent);
            Requests.Add(captured);

            BeforeRound?.Invoke(captured.Round);

            if (captured.Round > _rounds.Count)
                throw new InvalidOperationException(
                    $"mock 脚本已耗尽：引擎请求了第 {captured.Round} 轮，脚本只有 {_rounds.Count} 轮（禁静默错位）");

            return ToRaw(_rounds[captured.Round - 1]);
        }

        public static LlmRawResponse ToRaw(JObject round)
        {
            var raw = new LlmRawResponse
            {
                Content = round.Value<string>("content"),
                FinishReason = round.Value<string>("finish_reason") ?? "stop",
            };
            var usage = round["usage"] as JObject;
            if (usage != null)
            {
                raw.PromptTokens = usage.Value<int?>("prompt_tokens") ?? 0;
                raw.CompletionTokens = usage.Value<int?>("completion_tokens") ?? 0;
                raw.ReasoningTokens = usage.Value<int?>("reasoning_tokens") ?? 0;
                raw.CachedPromptTokens = usage.Value<int?>("cached_prompt_tokens") ?? 0;
            }
            var calls = round["tool_calls"] as JArray;
            if (calls != null)
                foreach (var t in calls)
                {
                    var o = (JObject)t;
                    raw.ToolCalls.Add(new LlmToolCall
                    {
                        Id = o.Value<string>("id"),
                        Name = o.Value<string>("name"),
                        ArgumentsJson = o.Value<string>("arguments"),
                    });
                }
            return raw;
        }
    }

    /// <summary>
    /// CheatController 端到端夹具（真实 <see cref="GameSession"/> + 隔离 <see cref="SamsaraEngine"/>）：
    /// 用于「Confirm 后对局真实改写」与「顶格终值 120 可确认」这两条只有走完两段式契约才能验的断言。
    /// </summary>
    public sealed class CheatHarness : IDisposable
    {
        readonly string _tempDir;

        public GameSession Session { get; }
        public SamsaraEngine Samsara { get; }
        public AgentEngine Engine { get; }
        public CheatController Controller { get; }

        public CheatHarness(ScriptedTransport transport, string variant = "xiangqi", int seed = AgentProbeKit.DefaultSeed)
        {
            var loader = new ConfigLoader(ProbeEnv.ConfigsRoot);
            var config = loader.LoadVariant(variant);
            Session = new GameSession(config, new MechanismEngine(config.Rules));

            _tempDir = Path.Combine(Path.GetTempPath(), "probe_samsara_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            var save = Path.Combine(_tempDir, "state.json");
            File.WriteAllText(save, new JObject { ["version"] = 4 }.ToString(Formatting.None));
            Samsara = new SamsaraEngine(Path.Combine(ProbeEnv.ConfigsRoot, "global"), save);

            Engine = AgentProbeKit.NewEngine(transport, seed);
            Controller = new CheatController(Session, Samsara, Engine);
        }

        /// <summary>预置业力（避免 0 业力时「扣费无变化」把断言变空转）。</summary>
        public void SeedKarma(int karma) => Samsara.IncreaseKarma(karma);

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
            catch { /* 临时目录清理失败不影响判定 */ }
        }
    }

    /// <summary>断言辅助（ProbeEnv 之上的语义化封装；失败一律抛异常 ⇒ 探针 FAIL）。</summary>
    public static class AgentAssert
    {
        public static void True(bool condition, string message) => ProbeEnv.Check(condition, message);

        public static void Equal(object expected, object actual, string label) => ProbeEnv.Equal(expected, actual, label);

        public static void Sequence(IList<string> expected, IList<string> actual, string label)
        {
            if (expected.Count != actual.Count)
                throw new Exception($"{label}: 数量不符 期望 {expected.Count} 实际 {actual.Count} [{string.Join(",", expected)}] vs [{string.Join(",", actual)}]");
            for (int i = 0; i < expected.Count; i++)
                if (expected[i] != actual[i])
                    throw new Exception($"{label}: 第 {i} 项不符 期望 {expected[i]} 实际 {actual[i]}");
        }

        public static void InRange(int value, int min, int max, string label)
        {
            if (value < min || value > max)
                throw new Exception($"{label}: {value} 不在 [{min},{max}]");
        }

        public static void InRange(double value, double min, double max, string label)
        {
            if (value < min || value > max)
                throw new Exception($"{label}: {value:0.000000} 不在 [{min},{max}]");
        }

        public static void DeepEquals(JToken expected, JToken actual, string label)
        {
            if (!JToken.DeepEquals(expected, actual))
                throw new Exception($"{label}: 语义不等\n期望 {expected?.ToString(Formatting.None)}\n实际 {actual?.ToString(Formatting.None)}");
        }

        public static List<string> ToolNames(AgentResult result)
        {
            var list = new List<string>();
            foreach (var entry in result.Trajectory) list.Add(entry.ToolName);
            return list;
        }
    }
}
