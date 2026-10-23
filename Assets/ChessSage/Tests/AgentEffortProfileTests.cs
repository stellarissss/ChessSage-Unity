using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E3-S2 effort 矩阵的引擎侧契约：M1/M2/M3 的逐轮 reasoning_effort 必须真实下发。
    ///
    /// 【缺陷背景】修复前 <see cref="AgentEngine"/> 只识别 M2；M1/M3 一律发送 null（服务端默认
    /// = thinking 常开），实验矩阵会静默失真。本组用例把「档位 → 每轮 effort」钉成可回归的合同。
    /// </summary>
    [TestFixture]
    public sealed class AgentEffortProfileTests
    {
        const int Seed = 20260101;

        static JObject SampleConfigs() => JObject.Parse(@"{
              ""board_state"": {
                ""pieces"": [{""id"": ""r_chariot_1"", ""type"": ""chariot"", ""name"": ""车"", ""side"": ""red"", ""position"": [0, 9], ""is_alive"": true}],
                ""current_turn"": ""red"", ""move_history"": [], ""game_status"": {""state"": ""playing""}
              },
              ""board"": {""width"": 9, ""height"": 10}
            }");

        static LlmRawResponse ToolRound(string name, string args, int id)
            => new LlmRawResponse
            {
                Content = null,
                FinishReason = "tool_calls",
                PromptTokens = 100,
                CompletionTokens = 10,
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall { Id = "call_" + id, Name = name, ArgumentsJson = args },
                },
            };

        static AgentRemakeConfig Config(string profile)
            => AgentRemakeConfig.FromJson(JObject.Parse(
                "{\"performance\":{\"effort_profile\":\"" + profile + "\"}}"));

        // ── 档位 → 逐轮 effort（纯函数合同） ─────────────────────────

        [Test]
        public void EffortForRound_M1_AlwaysLow()
        {
            for (int r = 1; r <= 4; r++)
                Assert.AreEqual("low", AgentPerformanceConfig.EffortForRound("M1", r, 4), $"M1 第 {r} 轮");
        }

        [Test]
        public void EffortForRound_M2_ToolLowFinalHigh()
        {
            Assert.AreEqual("low", AgentPerformanceConfig.EffortForRound("M2", 1, 4));
            Assert.AreEqual("low", AgentPerformanceConfig.EffortForRound("M2", 3, 4));
            Assert.AreEqual("high", AgentPerformanceConfig.EffortForRound("M2", 4, 4), "M2 终轮必须 high");
        }

        [Test]
        public void EffortForRound_M3_ToolNoneFinalHigh()
        {
            Assert.AreEqual("none", AgentPerformanceConfig.EffortForRound("M3", 1, 4));
            Assert.AreEqual("none", AgentPerformanceConfig.EffortForRound("M3", 3, 4));
            Assert.AreEqual("high", AgentPerformanceConfig.EffortForRound("M3", 4, 4), "M3 终轮必须 high");
        }

        [Test]
        public void EffortForRound_Unknown_DefaultsToM2()
        {
            Assert.AreEqual("low", AgentPerformanceConfig.EffortForRound("M9", 1, 4));
            Assert.AreEqual("high", AgentPerformanceConfig.EffortForRound("M9", 4, 4));
        }

        // ── 端到端：请求体里的 reasoning_effort 逐轮可观测 ────────────

        [TestCase("M1", new[] { "low", "low", "low", "low" })]
        [TestCase("M2", new[] { "low", "low", "low", "high" })]
        [TestCase("M3", new[] { "none", "none", "none", "high" })]
        public void Engine_SendsProfileEffortPerRound(string profile, string[] expected)
        {
            TestEnv.RunSync(async () =>
            {
                var seen = new List<string>();
                var index = 0;
                var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "deepseek-flash");
                client.MockToolHandler = req =>
                {
                    seen.Add(req.ReasoningEffort);
                    index++;
                    return Task.FromResult(ToolRound("get_board_state", "{}", index));
                };

                var engine = new AgentEngine(client, config: Config(profile), sessionSeed: Seed);
                engine.ConfigValidator = (name, cfg) => (true, (string)null);
                engine.ObjectiveTypesProvider = () => new[] { "checkmate" };
                engine.SyncKarmaState(new AgentKarmaState
                {
                    Karma = 50, KarmaMax = 120, SingleMax = 120, Detection = 0, Realm = "human",
                });

                var context = new JObject { ["configs"] = SampleConfigs() };
                await engine.ProcessCommandAsync("读取局势", context, CancellationToken.None);

                // 4 轮均发起（第 4 轮为终轮；重复工具在第 4 轮后熔断，不影响观测）。
                Assert.AreEqual(4, seen.Count, "应完成 4 次 LLM 调用");
                CollectionAssert.AreEqual(expected, seen, $"{profile} 逐轮 reasoning_effort");
            });
        }

        [Test]
        public void Config_ParsesM3_WithoutWarnings()
        {
            var c = Config("M3");
            Assert.AreEqual("M3", c.Performance.EffortProfile);
            Assert.That(c.Warnings, Is.Empty, "M3 是合法档位，不得回退/告警");
        }
    }
}
