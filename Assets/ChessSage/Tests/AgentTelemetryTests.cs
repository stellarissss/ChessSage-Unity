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
    /// E3-S4 缺陷修复的**受控复现用例**：AgentEngine 延迟遥测（§13.7 ttft_ms / round_ms / total_ms）
    /// 与时钟注入缝（§10.1）。
    ///
    /// 【缺陷复现】修复前 <c>ProcessCommandAsync</c> 只写 rounds/tools/karma 等字段，
    /// ttft_ms / round_ms / total_ms 恒为 0——遥测文件里三个延迟字段是死的，
    /// 性能回归（P50 total_ms、P90 ttft_ms）与 MAN 观察项都失去数据源。
    ///
    /// 【纪律】全部用**虚拟时钟**（手动推进），零 wall-clock、零网络：
    /// 断言的是「引擎把每轮耗时正确记账」这一契约，而不是机器跑多快。
    /// </summary>
    [TestFixture]
    public sealed class AgentTelemetryTests
    {
        const int Seed = 20260101;

        /// <summary>虚拟单调时钟：只被测试显式推进。</summary>
        sealed class TestClock : IMonotonicClock
        {
            public long ElapsedMs { get; set; }
            public void Advance(long ms) => ElapsedMs += ms;
        }

        static LlmRawResponse ToolRound(string name, string args, int prompt, int completion)
            => new LlmRawResponse
            {
                Content = null,
                FinishReason = "tool_calls",
                PromptTokens = prompt,
                CompletionTokens = completion,
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall { Id = "call_" + name, Name = name, ArgumentsJson = args },
                },
            };

        static LlmRawResponse TextRound(string content, int prompt, int completion)
            => new LlmRawResponse
            {
                Content = content,
                FinishReason = "stop",
                PromptTokens = prompt,
                CompletionTokens = completion,
            };

        static AgentEngine NewEngine(LlmTransport transport, TestClock clock)
        {
            var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "deepseek-flash");
            client.MockToolHandler = transport;
            var engine = new AgentEngine(client, config: null, sessionSeed: Seed) { Clock = clock };
            engine.ConfigValidator = (name, cfg) => (true, (string)null);
            engine.SyncKarmaState(new AgentKarmaState
            {
                Karma = 50, KarmaMax = 120, SingleMax = 120, Detection = 0.0, Realm = "human",
            });
            return engine;
        }

        static JObject Context() => new JObject
        {
            ["configs"] = JObject.Parse(@"{
              ""board_state"": {
                ""pieces"": [{""id"": ""r_chariot_1"", ""type"": ""chariot"", ""name"": ""车"", ""side"": ""red"", ""position"": [0, 9], ""is_alive"": true}],
                ""current_turn"": ""red"", ""move_history"": [], ""game_status"": {""state"": ""playing""}
              },
              ""board"": {""width"": 9, ""height"": 10},
              ""pieces_red"": {""pieces"": {""horse"": {""label"": ""馬"", ""moves"": []}}}
            }"),
        };

        // ── ① 每轮耗时逐项入账 ────────────────────────────────────────

        [Test]
        public void RoundMs_Ttft_TotalMs_TrackPerRoundLatency()
        {
            TestEnv.RunSync(async () =>
            {
                var clock = new TestClock();
                var index = 0;
                var rounds = new[]
                {
                    ToolRound("get_board_state", "{}", 1200, 300),
                    ToolRound("apply_config_patch",
                        "{\"operations\":[{\"target\":\"pieces_red\",\"patch\":[{\"op\":\"replace\",\"path\":\"/pieces/horse/label\",\"value\":\"马\"}]}]}",
                        1800, 400),
                    TextRound("改好了。", 900, 120),
                };

                var engine = NewEngine(req =>
                {
                    clock.Advance(500); // 每轮虚拟 500ms（模拟一次真实 HTTP 往返）
                    return Task.FromResult(rounds[Math.Min(index++, rounds.Length - 1)]);
                }, clock);

                await engine.ProcessCommandAsync("改个名", Context(), CancellationToken.None);

                var record = engine.Ledger.Requests[engine.Ledger.Requests.Count - 1];
                CollectionAssert.AreEqual(new[] { 500, 500, 500 }, record.RoundMs, "每轮耗时应逐项入账");
                Assert.AreEqual(500, record.TtftMs, "TTFT 取首轮时长（P0 代理口径）");
                Assert.IsTrue(record.TtftProxy, "P0 非流式必须标注 ttft_proxy");
                Assert.AreEqual(1500, record.TotalMs, "total_ms = 提交起点到收尾（含本地开销）");
                Assert.AreEqual(3, record.Rounds, "rounds 与 round_ms 长度必须一致");
                Assert.AreEqual(record.RoundMs.Length, record.Rounds);
            });
        }

        // ── ② 被取消的那一轮不计入遥测 ────────────────────────────────

        [Test]
        public void CancelledRound_IsExcludedFromRoundMsAndRounds()
        {
            TestEnv.RunSync(async () =>
            {
                var clock = new TestClock();
                var cts = new CancellationTokenSource();
                var engine = NewEngine(req =>
                {
                    clock.Advance(6000);              // 虚拟 6s/轮
                    if (clock.ElapsedMs > 20000)      // 第 4 轮越过 20s 硬顶 ⇒ 组合根取消
                    {
                        cts.Cancel();
                        throw new OperationCanceledException("虚拟时钟到点（20s）");
                    }
                    return Task.FromResult(ToolRound("get_board_state", "{}", 1200, 300));
                }, clock);

                await engine.ProcessCommandAsync("多读几遍", Context(), cts.Token);

                var result = engine.LastResult;
                var record = engine.Ledger.Requests[engine.Ledger.Requests.Count - 1];

                Assert.IsTrue(result.TimedOut, "20s 到点应置 TimedOut");
                Assert.AreEqual(3, result.Rounds, "只计已完成的三轮");
                Assert.AreEqual(3, record.RoundMs.Length, "被取消的轮不得出现在 round_ms（否则口径造假）");
                Assert.AreEqual(3, record.Rounds);
                // total_ms 是「提交总耗时」，含被取消那一轮已花的 6s（24000）；
                // round_ms 只收**已完成**轮（18000）——两者口径不同，故意分别断言。
                Assert.AreEqual(24000, record.TotalMs, "total_ms 覆盖整次提交（含到点那一轮已消耗的时间）");
                Assert.AreEqual(18000, Sum(record.RoundMs), "Σ round_ms 只含已完成轮");
            });
        }

        static int Sum(int[] values)
        {
            var total = 0;
            foreach (var v in values) total += v;
            return total;
        }

        // ── ③ 落盘形状：五字段 + 代理标注 ────────────────────────────

        [Test]
        public void Telemetry_Persisted_ContainsDelayFields()
        {
            TestEnv.RunSync(async () =>
            {
                var clock = new TestClock();
                var engine = NewEngine(req =>
                {
                    clock.Advance(700);
                    return Task.FromResult(TextRound("好的。", 500, 50));
                }, clock);

                await engine.ProcessCommandAsync("随便聊聊", Context(), CancellationToken.None);

                var requests = (JArray)engine.Ledger.ToJson()["requests"];
                Assert.AreEqual(1, requests.Count);
                var req = (JObject)requests[0];

                foreach (var key in new[] { "ttft_ms", "round_ms", "total_ms", "prefix_hit_rate", "rounds", "ttft_proxy" })
                    Assert.IsTrue(req.ContainsKey(key), $"§13.7 遥测字段缺失：{key}");

                Assert.AreEqual(700, req.Value<long>("ttft_ms"));
                Assert.AreEqual(1, ((JArray)req["round_ms"]).Count);
                Assert.AreEqual(700, req.Value<long>("total_ms"));
                Assert.IsTrue(req.Value<bool>("ttft_proxy"));
            });
        }

        // ── ④ 默认时钟：单调、零系统时间依赖 ──────────────────────────

        [Test]
        public void DefaultClock_IsMonotonicStopwatch()
        {
            var engine = new AgentEngine(new LlmClient("k"), null);
            Assert.IsInstanceOf<StopwatchClock>(engine.Clock, "生产时钟必须是 Stopwatch（不受系统时间调整影响）");
            var first = engine.Clock.ElapsedMs;
            var second = engine.Clock.ElapsedMs;
            Assert.GreaterOrEqual(second, first, "单调不减");
        }
    }
}
