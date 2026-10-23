using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    // ────────────────────────────────────────────────────────────────
    // 黄金轨迹夹具模型（工程侧 §10.2 格式）
    //   Fixtures/agent/<case>.json =
    //     { id, version, note, command, allowed_tools, seed,
    //       rounds: [ { request_digest?, response: { content?, tool_calls?, finish_reason, usage } } ],
    //       cancel_before_round?,                       // S10：第 N 轮前 20s 到点
    //       expected: { tool_sequence, result_type, token_eff, rounds, writes,
    //                   cap_warned, timed_out, modified_keys,
    //                   karma_exact?, karma_min, karma_max, trajectory_error_codes? } }
    //   request_digest = 该轮请求 messages 序列化 SHA-256 前 16 位（"*" = 跳过断言）；
    //   response.usage = { prompt_tokens, completion_tokens, reasoning_tokens?, cached_prompt_tokens? }。
    // ────────────────────────────────────────────────────────────────

    public sealed class AgentGoldenToolCall
    {
        public string id;
        public string name;
        public string arguments;
    }

    public sealed class AgentGoldenUsage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int reasoning_tokens;
        public int cached_prompt_tokens;
    }

    public sealed class AgentGoldenResponse
    {
        public string content;
        public List<AgentGoldenToolCall> tool_calls;
        public string finish_reason;
        public AgentGoldenUsage usage;
    }

    public sealed class AgentGoldenRound
    {
        public string request_digest;
        public AgentGoldenResponse response;
    }

    public sealed class AgentGoldenExpected
    {
        public List<string> tool_sequence;
        public string result_type;
        public int token_eff;
        public int rounds;
        public int writes;
        public bool cap_warned;
        public bool timed_out;
        public List<string> modified_keys;
        public int? karma_exact;
        public int karma_min;
        public int karma_max;
        /// <summary>E3-C：本次提交的主导作用域（null = 不断言；"" = 断言无作用域）。</summary>
        public string scope;
        /// <summary>按序断言轨迹中的失败错误码（只列失败条目；null = 不断言）。</summary>
        public List<string> trajectory_error_codes;
    }

    public sealed class AgentGoldenCase
    {
        public string id;
        public int version;
        public string note;
        public string command;
        public List<string> allowed_tools;
        public int seed;
        public int cancel_before_round;
        public List<AgentGoldenRound> rounds;
        public AgentGoldenExpected expected;

        public static AgentGoldenCase FromJson(JObject json)
        {
            var c = new AgentGoldenCase
            {
                id = json.Value<string>("id"),
                version = json.Value<int?>("version") ?? 1,
                note = json.Value<string>("note"),
                command = json.Value<string>("command"),
                seed = json.Value<int?>("seed") ?? 0,
                cancel_before_round = json.Value<int?>("cancel_before_round") ?? 0,
                rounds = new List<AgentGoldenRound>(),
                expected = new AgentGoldenExpected(),
            };
            var allowed = json["allowed_tools"] as JArray;
            if (allowed != null)
            {
                c.allowed_tools = new List<string>();
                foreach (var t in allowed) c.allowed_tools.Add(t.Value<string>());
            }
            foreach (var r in (JArray)json["rounds"])
            {
                var ro = (JObject)r;
                var resp = (JObject)ro["response"];
                var usage = resp["usage"] as JObject;
                var round = new AgentGoldenRound
                {
                    request_digest = ro.Value<string>("request_digest"),
                    response = new AgentGoldenResponse
                    {
                        content = resp.Value<string>("content"),
                        finish_reason = resp.Value<string>("finish_reason") ?? "stop",
                        usage = new AgentGoldenUsage
                        {
                            prompt_tokens = usage?.Value<int?>("prompt_tokens") ?? 0,
                            completion_tokens = usage?.Value<int?>("completion_tokens") ?? 0,
                            reasoning_tokens = usage?.Value<int?>("reasoning_tokens") ?? 0,
                            cached_prompt_tokens = usage?.Value<int?>("cached_prompt_tokens") ?? 0,
                        },
                    },
                };
                var calls = resp["tool_calls"] as JArray;
                if (calls != null)
                {
                    round.response.tool_calls = new List<AgentGoldenToolCall>();
                    foreach (var t in calls)
                    {
                        var to = (JObject)t;
                        round.response.tool_calls.Add(new AgentGoldenToolCall
                        {
                            id = to.Value<string>("id"),
                            name = to.Value<string>("name"),
                            arguments = to.Value<string>("arguments"),
                        });
                    }
                }
                c.rounds.Add(round);
            }
            var e = (JObject)json["expected"];
            c.expected = new AgentGoldenExpected
            {
                tool_sequence = ((JArray)e["tool_sequence"])?.Select(t => t.Value<string>()).ToList(),
                result_type = e.Value<string>("result_type"),
                token_eff = e.Value<int?>("token_eff") ?? 0,
                rounds = e.Value<int?>("rounds") ?? 0,
                writes = e.Value<int?>("writes") ?? 0,
                cap_warned = e.Value<bool?>("cap_warned") ?? false,
                timed_out = e.Value<bool?>("timed_out") ?? false,
                modified_keys = e["modified_keys"] is JArray mk ? mk.Select(t => t.Value<string>()).ToList() : null,
                karma_exact = e.Value<int?>("karma_exact"),
                karma_min = e.Value<int?>("karma_min") ?? 0,
                karma_max = e.Value<int?>("karma_max") ?? 0,
                scope = e["scope"]?.Type == JTokenType.String ? e.Value<string>("scope") : null,
                trajectory_error_codes = e["trajectory_error_codes"] is JArray te
                    ? te.Select(t => t.Value<string>()).ToList()
                    : null,
            };
            return c;
        }
    }

    /// <summary>
    /// 黄金轨迹夹具加载 / 回放 / 断言（E1-S7；QA §2.2/§2.3 契约落地）。
    /// 回放全程零网络、零 wall-clock：多轮响应按序供给，20s 到点经 CancellationToken 模拟。
    /// </summary>
    public static class AgentGoldenFixtures
    {
        public const string FixtureDir = "agent";

        public static AgentGoldenCase Load(string name)
            => AgentGoldenCase.FromJson(TestEnv.LoadJson(Path.Combine(TestEnv.FixturesDir, FixtureDir, name + ".json")));

        public static IEnumerable<string> AllNames() => new[]
        {
            "trj_e1_01_normal_flow",
            "trj_e1_02_cap_pass_through",
            "trj_e1_03_timeout_partial",
            "trj_e1_04_hallucination_heal",
            "trj_e1_05_zero_write_fun",
            "trj_e1_06_rounds_exhausted_partial",
        };

        /// <summary>回放一条夹具：返回 (CheatController 兼容 JObject, AgentResult 遥测)。</summary>
        public static async Task<(JObject result, AgentResult agent)> Replay(AgentGoldenCase c)
        {
            var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "deepseek-flash");
            var cts = new CancellationTokenSource();
            var index = 0;

            client.MockToolHandler = req =>
            {
                var roundNo = index + 1;
                if (c.cancel_before_round > 0 && roundNo >= c.cancel_before_round)
                {
                    cts.Cancel();
                    throw new OperationCanceledException($"20s 到点（夹具 cancel_before_round={c.cancel_before_round}）");
                }
                if (index >= c.rounds.Count)
                    throw new InvalidOperationException($"夹具 {c.id} 只有 {c.rounds.Count} 轮，引擎却请求了第 {roundNo} 轮（编排错误）");

                var round = c.rounds[index];
                index++;
                if (!string.IsNullOrEmpty(round.request_digest) && round.request_digest != "*")
                {
                    var digest = Digest(req.Messages);
                    if (Environment.GetEnvironmentVariable("CHESSSAGE_DUMP_DIGESTS") == "1")
                    {
                        var dir = Path.Combine(Directory.GetParent(TestEnv.AssetsRoot).FullName, "Logs", "e3");
                        Directory.CreateDirectory(dir);
                        File.AppendAllText(Path.Combine(dir, "fixture-digests.txt"),
                            c.id + "|" + roundNo + "|" + digest + "\n");
                    }
                    else
                    {
                        Assert.AreEqual(round.request_digest, digest,
                            $"夹具 {c.id} 第 {roundNo} 轮请求前缀漂移（request_digest 不匹配）");
                    }
                }
                return Task.FromResult(ToRaw(round.response));
            };

            var engine = new AgentEngine(client, config: AgentRemakeConfig.Default, sessionSeed: c.seed);
            engine.ConfigValidator = (name, cfg) => (true, (string)null);
            engine.ObjectiveTypesProvider = () => new[] { "checkmate", "capture_all", "survive_turns", "reach_position" };
            engine.SyncKarmaState(new AgentKarmaState
            {
                Karma = 50, KarmaMax = 120, SingleMax = 120, Detection = 0.0, Realm = "human",
            });

            var context = new JObject
            {
                ["configs"] = GoldenConfigs(),
                ["allowed_tools"] = new JArray(c.allowed_tools ?? AgentGoldenFixturesAllTools()),
            };
            var result = await engine.ProcessCommandAsync(c.command, context, cts.Token);
            return (result, engine.LastResult);
        }

        public static void AssertExpected(AgentGoldenCase c, JObject result, AgentResult agent)
        {
            var e = c.expected;
            StringAssert.AreEqualIgnoringCase(e.result_type, result.Value<string>("type"),
                $"{c.id} 结果类型");
            Assert.AreEqual(e.token_eff, agent.TokenEff, $"{c.id} T_eff");
            Assert.AreEqual(e.rounds, agent.Rounds, $"{c.id} 轮次");
            Assert.AreEqual(e.writes, agent.Writes, $"{c.id} 写入数");
            Assert.AreEqual(e.cap_warned, agent.CapWarned, $"{c.id} CapWarning");
            Assert.AreEqual(e.timed_out, agent.TimedOut, $"{c.id} TimedOut");

            var actualTools = agent.Trajectory.Select(t => t.ToolName).ToList();
            CollectionAssert.AreEqual(e.tool_sequence, actualTools, $"{c.id} 工具序列");

            if (e.modified_keys != null)
            {
                var modified = result["modified_configs"] as JObject;
                if (e.modified_keys.Count == 0)
                    Assert.IsTrue(modified == null || !modified.HasValues, $"{c.id} 不应有 modified_configs");
                else
                {
                    Assert.IsNotNull(modified, $"{c.id} 应有 modified_configs");
                    CollectionAssert.AreEqual(e.modified_keys, modified.Properties().Select(p => p.Name).ToList(),
                        $"{c.id} 变更切片键");
                }
            }

            if (e.karma_exact.HasValue)
                Assert.AreEqual(e.karma_exact.Value, result.Value<int>("estimated_karma_cost"),
                    $"{c.id} 业力精确值（固定种子可复现）");
            Assert.GreaterOrEqual(result.Value<int>("estimated_karma_cost"), e.karma_min, $"{c.id} 业力下界");
            Assert.LessOrEqual(result.Value<int>("estimated_karma_cost"), e.karma_max, $"{c.id} 业力上界");

            // E3-C：作用域是机械事实，黄金镜像同时锁住「价格从哪个作用域来」。
            if (e.scope != null)
                Assert.AreEqual(e.scope, agent.Scope ?? string.Empty, $"{c.id} 主导作用域");

            if (e.trajectory_error_codes != null)
            {
                var codes = agent.Trajectory.Where(t => !t.Ok).Select(t => t.ErrorCode).ToList();
                CollectionAssert.AreEqual(e.trajectory_error_codes, codes, $"{c.id} 失败错误码序列");
            }
        }

        static List<string> AgentGoldenFixturesAllTools()
        {
            var all = new List<string>();
            foreach (var def in AgentToolSchemas.All) all.Add(def.Name);
            return all;
        }

        static LlmRawResponse ToRaw(AgentGoldenResponse r)
        {
            var raw = new LlmRawResponse
            {
                Content = r.content,
                FinishReason = r.finish_reason ?? "stop",
                PromptTokens = r.usage?.prompt_tokens ?? 0,
                CompletionTokens = r.usage?.completion_tokens ?? 0,
                ReasoningTokens = r.usage?.reasoning_tokens ?? 0,
                CachedPromptTokens = r.usage?.cached_prompt_tokens ?? 0,
            };
            if (r.tool_calls != null)
                foreach (var t in r.tool_calls)
                    raw.ToolCalls.Add(new LlmToolCall { Id = t.id, Name = t.name, ArgumentsJson = t.arguments });
            return raw;
        }

        /// <summary>请求 messages 的 SHA-256 前 16 位（前缀漂移检测）。</summary>
        public static string Digest(List<LlmMessage> messages)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    LlmClient.SerializeMessages(messages).ToString(Formatting.None)));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>夹具共用初始配置（与 AgentEngineTests.SampleConfigs 同构）。</summary>
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
              ""pieces_red"": {""pieces"": {""chariot"": {""label"": ""车"", ""moves"": []}}}
            }");
    }

    /// <summary>
    /// E1-S7：黄金轨迹回放门（mock 全绿的最终形态）。
    /// 每条夹具 = 有序响应序列 + 期望断言；回放零网络零 wall-clock。
    /// </summary>
    [TestFixture]
    public sealed class AgentGoldenTrajectoryTests
    {
        [Test]
        public void FixtureSet_CoversRequiredScenarios()
        {
            var names = AgentGoldenFixtures.AllNames();
            CollectionAssert.Contains(names, "trj_e1_01_normal_flow", "必须覆盖正常流");
            CollectionAssert.Contains(names, "trj_e1_02_cap_pass_through", "必须覆盖顶格放行");
            CollectionAssert.Contains(names, "trj_e1_03_timeout_partial", "必须覆盖 20s 收尾半成品");
            Assert.GreaterOrEqual(names.Count(), 3);
        }

        [TestCaseSource(typeof(AgentGoldenFixtures), nameof(AgentGoldenFixtures.AllNames))]
        public void Replay_GoldenCase_MatchesExpected(string name)
        {
            TestEnv.RunSync(async () =>
            {
            var c = AgentGoldenFixtures.Load(name);
            var (result, agent) = await AgentGoldenFixtures.Replay(c);
            AgentGoldenFixtures.AssertExpected(c, result, agent);
            });
        }
    }
}
