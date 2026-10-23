using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E1-S3：AgentEngine 主循环（关键路径）。
    /// 期望值独立推导：场景对齐 QA §2.5 矩阵引擎侧可覆盖部分——
    /// S1 正常流 / S2 schema 重试 / S4 轮次上限 / S5 token 早停 / S7 幻觉 /
    /// S9 顶格放行 / S10 20s 收尾 / S11 前缀稳定性 / S12 闲聊。
    /// 全部经 MockToolHandler 回放，零网络、零 wall-clock（时间经 CancellationToken）。
    /// </summary>
    [TestFixture]
    public sealed class AgentEngineTests
    {
        const int Seed = 20260101;

        static JObject SampleConfigs()
        {
            return JObject.Parse(@"{
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

        static LlmRawResponse ToolRound(string name, string args, int prompt, int completion,
            int thinking = 0, int cached = 0, string id = "call_1")
            => new LlmRawResponse
            {
                Content = null,
                FinishReason = "tool_calls",
                PromptTokens = prompt,
                CompletionTokens = completion,
                ReasoningTokens = thinking,
                CachedPromptTokens = cached,
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = id, Name = name, ArgumentsJson = args } },
            };

        static LlmRawResponse TextRound(string content, int prompt, int completion)
            => new LlmRawResponse
            {
                Content = content,
                FinishReason = "stop",
                PromptTokens = prompt,
                CompletionTokens = completion,
            };

        static AgentEngine NewEngine(LlmTransport transport, AgentRemakeConfig config = null,
            int sessionSeed = Seed)
        {
            var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "deepseek-flash");
            client.MockToolHandler = transport;
            var engine = new AgentEngine(client, config: config, sessionSeed: sessionSeed);
            engine.ConfigValidator = (name, cfg) => (true, (string)null);
            engine.ObjectiveTypesProvider = () => new[] { "checkmate", "capture_all" };
            engine.SyncKarmaState(new AgentKarmaState
            {
                Karma = 50, KarmaMax = 120, SingleMax = 120, Detection = 0.0, Realm = "human",
            });
            return engine;
        }

        static JObject Context() => new JObject { ["configs"] = SampleConfigs() };

        // ── S1 正常流：读 → 写 → commit ──────────────────────────────

        [Test]
        public void NormalFlow_ReadPatchCommit_AppliedWithKarma()
        {
            TestEnv.RunSync(async () =>
            {
            var rounds = new[]
            {
                ToolRound("get_board_state", "{\"include\":[\"pieces\"]}", 1200, 300),
                ToolRound("apply_config_patch",
                    "{\"operations\":[{\"target\":\"board\",\"patch\":[{\"op\":\"replace\",\"path\":\"/width\",\"value\":8}]}]}",
                    1800, 400, thinking: 120),
                ToolRound("commit_preview", "{\"summary\":\"已把棋盘加宽到 8 列\"}", 2000, 150),
            };
            var i = 0;
            var engine = NewEngine(req => Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]));

            var result = await engine.ProcessCommandAsync("棋盘加宽", Context(), CancellationToken.None);

            Assert.AreEqual("applied", result.Value<string>("type"));
            Assert.IsTrue(result.Value<bool>("success"));
            Assert.AreEqual("已把棋盘加宽到 8 列", result.Value<string>("message"));
            Assert.AreEqual(8, (int)result["modified_configs"]["board"]["width"]);
            Assert.IsTrue(result.Value<int>("estimated_karma_cost") > 0, "有写入必须计价");
            Assert.IsFalse(result.Value<bool>("karma_blocked"), "裁决 #3：karma_blocked 恒 false");
            Assert.AreEqual(3, engine.LastResult.Rounds);
            Assert.AreEqual(3, engine.LastResult.Trajectory.Count, "轨迹记录每次工具调用");
            Assert.AreEqual("get_board_state", engine.LastResult.Trajectory[0].ToolName);
            Assert.IsTrue(engine.LastResult.Karma.Karma > 0);
            });
        }

        // ── S12 闲聊：零写入 ⇒ fun 恒 1 ──────────────────────────────

        [Test]
        public void ZeroWriteChat_ReturnsFun_KarmaConstant1()
        {
            TestEnv.RunSync(async () =>
            {
            var engine = NewEngine(req => Task.FromResult(TextRound("今日天象不错，宜动棋。", 1500, 80)));

            var result = await engine.ProcessCommandAsync("今天天气如何", Context(), CancellationToken.None);

            Assert.AreEqual("fun", result.Value<string>("type"));
            Assert.AreEqual(1, result.Value<int>("estimated_karma_cost"), "零写入等价旧 E 类固定价");
            Assert.IsTrue(engine.LastResult.Karma.ZeroWrite);
            });
        }

        // ── S7 幻觉工具名：结构化错误回喂自愈 ─────────────────────────

        [Test]
        public void HallucinatedTool_StructuredErrorFedBack_SelfHeals()
        {
            TestEnv.RunSync(async () =>
            {
            var rounds = new[]
            {
                ToolRound("conquer_world", "{}", 1000, 100),
                ToolRound("get_board_state", "{}", 1300, 200),
                ToolRound("commit_preview", "{\"summary\":\"了解了一下局势\"}", 1600, 120),
            };
            var i = 0;
            var engine = NewEngine(req => Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]));

            var result = await engine.ProcessCommandAsync("看看局势", Context(), CancellationToken.None);

            Assert.AreEqual("fun", result.Value<string>("type"), "只读 + commit 无写入 ⇒ 文字收尾等价闲聊");
            Assert.AreEqual("UNKNOWN_TOOL", engine.LastResult.Trajectory[0].ErrorCode);
            Assert.IsFalse(engine.LastResult.Trajectory[0].Ok, "幻觉调用记录在轨迹且标记失败");
            });
        }

        // ── S2 参数损坏：BAD_ARGS_JSON 回喂自愈 ──────────────────────

        [Test]
        public void CorruptArguments_FedBack_SelfHeals()
        {
            TestEnv.RunSync(async () =>
            {
            var rounds = new[]
            {
                ToolRound("move_piece", "{这不是合法JSON", 1000, 100),
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}", 1400, 200),
                ToolRound("commit_preview", "{\"summary\":\"已移车\"}", 1800, 120),
            };
            var i = 0;
            var engine = NewEngine(req => Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]));

            var result = await engine.ProcessCommandAsync("把车移到 (4,8)", Context(), CancellationToken.None);

            Assert.AreEqual("applied", result.Value<string>("type"));
            Assert.AreEqual("BAD_ARGS_JSON", engine.LastResult.Trajectory[0].ErrorCode);
            Assert.IsTrue(engine.LastResult.Trajectory[1].Ok, "第二轮修正成功");
            });
        }

        // ── S4 轮次上限：有写入 ⇒ partial 按 f(T_stop) 计价 ───────────

        [Test]
        public void RoundsExhausted_WithWrites_PartialPricedAtStop()
        {
            TestEnv.RunSync(async () =>
            {
            // 每轮都读棋盘、从不 commit ⇒ 第 4 轮后耗尽。
            var engine = NewEngine(req => Task.FromResult(
                ToolRound("get_board_state", "{}", 1200, 300)));
            // 预置一次真实写入：第一轮读、第二轮写、之后继续读。
            var script = new List<LlmRawResponse>
            {
                ToolRound("get_board_state", "{}", 1200, 300),
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}", 1500, 300),
                ToolRound("get_board_state", "{}", 1900, 300),
                ToolRound("get_board_state", "{}", 2300, 300),
            };
            var i = 0;
            engine = NewEngine(req => Task.FromResult(script[Math.Min(i++, script.Count - 1)]));

            var result = await engine.ProcessCommandAsync("多读几遍再改", Context(), CancellationToken.None);

            Assert.AreEqual("partial", result.Value<string>("type"), "轮次耗尽 + 有写入 ⇒ partial（收尾策略 A）");
            Assert.IsNotNull(result["modified_configs"], "partial 携带已实现变更");
            Assert.AreEqual(4, engine.LastResult.Rounds);
            Assert.Greater(engine.LastResult.EstimatedKarmaCost, 0, "按 f(T_stop) 计价，不豁免");
            });
        }

        [Test]
        public void RoundsExhausted_NoWrites_FailureKarmaZero()
        {
            TestEnv.RunSync(async () =>
            {
            var engine = NewEngine(req => Task.FromResult(ToolRound("get_board_state", "{}", 1200, 300)));

            var result = await engine.ProcessCommandAsync("一直读不写", Context(), CancellationToken.None);

            Assert.AreEqual("error", result.Value<string>("type"));
            Assert.AreEqual(0, result.Value<int>("estimated_karma_cost"), "失败卡不收费");
            Assert.IsTrue(engine.LastResult.Karma.Failed);
            });
        }

        // ── S5 token 硬停：即刻停 + TokenEff 如实记录 ─────────────────

        [Test]
        public void TokenHardStop_WithWrites_Partial_TokenEffHonest()
        {
            TestEnv.RunSync(async () =>
            {
            var config = AgentRemakeConfig.FromJson(JObject.Parse(
                @"{""limits"": {""max_rounds"": 8, ""token_hard_stop"": 5000}}"));
            var script = new List<LlmRawResponse>
            {
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}", 4000, 800),
                ToolRound("get_board_state", "{}", 4200, 900), // 累计 9900 > 5000 ⇒ 硬停
            };
            var i = 0;
            var engine = NewEngine(req => Task.FromResult(script[Math.Min(i++, script.Count - 1)]), config);

            var result = await engine.ProcessCommandAsync("移子", Context(), CancellationToken.None);

            Assert.AreEqual("partial", result.Value<string>("type"));
            Assert.AreEqual(9900, engine.LastResult.TokenEff, "TokenEff 如实记录（不截断造假）");
            Assert.AreEqual(2, engine.LastResult.Rounds, "硬停发生在第 2 轮 usage 入账后");
            });
        }

        // ── S9 顶格放行：CapWarning 只警示不拦截 ──────────────────────

        [Test]
        public void CapWarning_WarnsButDoesNotBlock_KarmaClampedAtConfirm()
        {
            TestEnv.RunSync(async () =>
            {
            var script = new List<LlmRawResponse>
            {
                ToolRound("apply_config_patch",
                    "{\"operations\":[{\"target\":\"board\",\"patch\":[{\"op\":\"replace\",\"path\":\"/width\",\"value\":8}]}]}",
                    20000, 4000),
                ToolRound("commit_preview", "{\"summary\":\"大手笔改完了\"}", 26000, 2000),
            };
            var i = 0;
            var engine = NewEngine(req => Task.FromResult(script[Math.Min(i++, script.Count - 1)]));

            var result = await engine.ProcessCommandAsync("大改", Context(), CancellationToken.None);

            Assert.AreEqual("applied", result.Value<string>("type"), "触顶不拦截：循环继续把活干完");
            Assert.IsTrue(engine.LastResult.CapWarned, "CapWarning 置位（UI 转红）");
            Assert.AreEqual(120, result.Value<int>("estimated_karma_cost"), "Confirm 口径 clamp 到 singleMax 放行");
            });
        }

        // ── S10 20s 到点收尾（经 CancellationToken，无 wall-clock） ────

        [Test]
        public void Timeout_WithWrites_PartialPreview()
        {
            TestEnv.RunSync(async () =>
            {
            var cts = new CancellationTokenSource();
            var script = new List<LlmRawResponse>
            {
                ToolRound("get_board_state", "{}", 1200, 300),
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}", 1500, 300),
                null, // 第三轮：20s 到点（组合根取消）
            };
            var i = 0;
            var engine = NewEngine(req =>
            {
                var r = script[Math.Min(i++, script.Count - 1)];
                if (r == null) { cts.Cancel(); throw new OperationCanceledException("20s 到点"); }
                return Task.FromResult(r);
            });

            var result = await engine.ProcessCommandAsync("移子", Context(), cts.Token);

            Assert.AreEqual("partial", result.Value<string>("type"), "20s 到点 + 有写入 ⇒ partial（收尾策略 A）");
            Assert.IsTrue(engine.LastResult.TimedOut);
            Assert.IsNotNull(result["modified_configs"]);
            });
        }

        [Test]
        public void Timeout_NoWrites_FailureCard()
        {
            TestEnv.RunSync(async () =>
            {
            var cts = new CancellationTokenSource();
            var engine = NewEngine(req =>
            {
                cts.Cancel();
                throw new OperationCanceledException("20s 到点");
            });

            var result = await engine.ProcessCommandAsync("移子", Context(), cts.Token);

            Assert.AreEqual("error", result.Value<string>("type"));
            Assert.AreEqual(0, result.Value<int>("estimated_karma_cost"), "无写入失败卡不收费");
            Assert.IsTrue(engine.LastResult.TimedOut);
            });
        }

        // ── 连续工具错误 ≥3 中止 ──────────────────────────────────────

        [Test]
        public void ConsecutiveToolErrors3_Aborts_WithWritesPartial()
        {
            TestEnv.RunSync(async () =>
            {
            var script = new List<LlmRawResponse>
            {
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}", 1000, 200),
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"ghost\",\"to\":[0,0]}", 1400, 200),
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"ghost\",\"to\":[0,0]}", 1800, 200),
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"ghost\",\"to\":[0,0]}", 2200, 200),
            };
            var i = 0;
            var engine = NewEngine(req => Task.FromResult(script[Math.Min(i++, script.Count - 1)]));

            var result = await engine.ProcessCommandAsync("移子", Context(), CancellationToken.None);

            Assert.AreEqual("partial", result.Value<string>("type"), "连续错误 ≥3 + 有写入 ⇒ partial");
            Assert.AreEqual(4, engine.LastResult.Rounds, "第 4 轮（第 3 次连续错误）后中止");
            });
        }

        [Test]
        public void TruncatedThinking_RetriesWithoutThinking()
        {
            TestEnv.RunSync(async () =>
            {
            var seen = new List<LlmThinkingMode>();
            var rounds = new[]
            {
                new LlmRawResponse
                {
                    Content = null, FinishReason = "length",
                    PromptTokens = 1700, CompletionTokens = 2048, ReasoningTokens = 2048,
                },
                ToolRound("get_board_state", "{}", 1200, 200),
                ToolRound("commit_preview", "{\"summary\":\"ok\"}", 1000, 100),
            };
            var i = 0;
            var engine = NewEngine(req =>
            {
                seen.Add(req.Thinking);
                return Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]);
            });

            var result = await engine.ProcessCommandAsync("x", Context(), CancellationToken.None);

            Assert.AreEqual(LlmThinkingMode.Default, seen[0], "首轮按 M2 low（thinking 开）");
            Assert.AreEqual(LlmThinkingMode.Disabled, seen[1], "thinking 吃满预算后应关闭 thinking 重试");
            Assert.AreEqual(LlmThinkingMode.Disabled, seen[2],
                "降级必须是单向的（E3-C）：非思考轮的 assistant 消息没有 reasoning_content，" +
                "再回升 thinking 会被服务端以 400 拒绝整次提交");
            Assert.IsTrue(engine.LastResult.Warnings.Exists(w => w.Contains("无思考重试")),
                "重试必须留遥测警告");
            Assert.AreEqual("fun", result.Value<string>("type"), "重试后正常走到零写入收尾");
            });
        }

        // ── E3-C 缺陷修复：thinking 协议 400 的可自愈路径 ──────────────

        [Test]
        public void ReasoningContent_IsEchoedOnSubsequentRequests()
        {
            TestEnv.RunSync(async () =>
            {
            List<LlmMessage> secondRoundMessages = null;
            var rounds = new[]
            {
                new LlmRawResponse
                {
                    FinishReason = "tool_calls",
                    PromptTokens = 1000, CompletionTokens = 200, ReasoningTokens = 150,
                    ReasoningContent = "先读棋盘，再决定改哪一格。",
                    ToolCalls = new List<LlmToolCall>
                    {
                        new LlmToolCall { Id = "c1", Name = "get_board_state", ArgumentsJson = "{}" },
                    },
                },
                ToolRound("commit_preview", "{\"summary\":\"看完了\"}", 1200, 100),
            };
            var i = 0;
            var engine = NewEngine(req =>
            {
                if (i == 1) secondRoundMessages = new List<LlmMessage>(req.Messages);
                return Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]);
            });

            await engine.ProcessCommandAsync("看看局势", Context(), CancellationToken.None);

            var assistant = secondRoundMessages.Find(m => m.Role == "assistant");
            Assert.IsNotNull(assistant, "第二轮必须带上第一轮的 assistant 消息");
            Assert.AreEqual("先读棋盘，再决定改哪一格。", assistant.ReasoningContent,
                "reasoning_content 必须随 assistant 消息回传（否则 thinking 模式 400）");
            StringAssert.Contains("reasoning_content", LlmClient.SerializeMessages(secondRoundMessages, true)
                .ToString(Newtonsoft.Json.Formatting.None), "thinking 请求的线格式应包含 reasoning_content");
            });
        }

        [Test]
        public void Http400ReasoningContent_RetriesOnceWithoutThinking_SubmissionSurvives()
        {
            TestEnv.RunSync(async () =>
            {
            var seen = new List<LlmThinkingMode>();
            var rounds = new[]
            {
                // 第 1 次尝试：模拟真实 400（E3 校准 C+ #47 的原始错误体）。
                new LlmRawResponse
                {
                    HttpStatus = 400,
                    Error = "HTTP 400: {\"error\":{\"message\":\"The `reasoning_content` in the thinking mode must be passed back to the API.\",\"type\":\"invalid_request_error\"}}",
                },
                ToolRound("get_board_state", "{}", 1200, 200),
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}", 1500, 300),
                ToolRound("commit_preview", "{\"summary\":\"移好了\"}", 1600, 100),
            };
            var i = 0;
            var engine = NewEngine(req =>
            {
                seen.Add(req.Thinking);
                return Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]);
            });

            var result = await engine.ProcessCommandAsync("把车移到 (4,8)", Context(), CancellationToken.None);

            Assert.AreEqual("applied", result.Value<string>("type"), "400 应被自愈，不得终结整次提交");
            Assert.AreEqual(LlmThinkingMode.Default, seen[0], "首次仍按生产配置开 thinking");
            Assert.AreEqual(LlmThinkingMode.Disabled, seen[1], "400 后本轮立即降级无思考重试");
            Assert.AreEqual(LlmThinkingMode.Disabled, seen[2], "降级单向：后续轮不得回升");
            Assert.IsTrue(engine.LastResult.Warnings.Exists(w => w.Contains("reasoning_content")),
                "自愈必须留遥测警告（含 reasoning_content 关键词，便于校准审计）");
            Assert.AreEqual(1, engine.LastResult.Writes);
            });
        }

        [Test]
        public void Http400_UnrelatedMessage_IsNotRetried_ThrowsOnce()
        {
            var calls = 0;
            var engine = NewEngine(req =>
            {
                calls++;
                return Task.FromResult(new LlmRawResponse
                {
                    HttpStatus = 400,
                    Error = "HTTP 400: {\"error\":{\"message\":\"invalid max_tokens\"}}",
                });
            });

            Assert.Throws<InvalidOperationException>(() =>
                engine.ProcessCommandAsync("x", Context(), CancellationToken.None).GetAwaiter().GetResult());
            Assert.AreEqual(1, calls, "非 reasoning_content 的 400 不得盲目重试（双倍烧额度）");
        }

        [Test]
        public void ReasoningTokensWithoutContent_DegradesNextRound()
        {
            TestEnv.RunSync(async () =>
            {
            var seen = new List<LlmThinkingMode>();
            var rounds = new[]
            {
                // thinking 上报了 reasoning_tokens，却没回 reasoning_content —— 真实链路的危险形态。
                ToolRound("get_board_state", "{}", 1200, 200, thinking: 180),
                ToolRound("commit_preview", "{\"summary\":\"ok\"}", 1000, 100, thinking: 0),
            };
            var i = 0;
            var engine = NewEngine(req =>
            {
                seen.Add(req.Thinking);
                return Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]);
            });

            await engine.ProcessCommandAsync("x", Context(), CancellationToken.None);

            Assert.AreEqual(LlmThinkingMode.Disabled, seen[1], "缺 reasoning_content 时必须预防性降级");
            Assert.IsTrue(engine.LastResult.Warnings.Exists(w => w.Contains("reasoning_content")));
            });
        }

        // ── 单工具重复 >3 熔断 ────────────────────────────────────────

        [Test]
        public void SameToolRepeatedOver3_CircuitBreaks()
        {
            TestEnv.RunSync(async () =>
            {
            var engine = NewEngine(req => Task.FromResult(
                ToolRound("get_board_state", "{}", 1200, 200)));

            var result = await engine.ProcessCommandAsync("死循环读", Context(), CancellationToken.None);

            Assert.AreEqual("error", result.Value<string>("type"), "死循环熔断 + 无写入 ⇒ 失败卡");
            Assert.GreaterOrEqual(engine.LastResult.Rounds, 4, "同一工具连续第 4 次调用后熔断");
            });
        }

        [Test]
        public void SameTool_DifferentArgs_IsNotARepeatLoop()
        {
            TestEnv.RunSync(async () =>
            {
            // E3-S2 修复：并行读不同切片（pieces_red/pieces_black）不是死循环。
            var rounds = new[]
            {
                ToolRound("get_config_slice", "{\"target\":\"board\"}", 100, 10),
                ToolRound("get_config_slice", "{\"target\":\"rules\"}", 100, 10),
                ToolRound("get_config_slice", "{\"target\":\"ui_config\"}", 100, 10),
                ToolRound("get_config_slice", "{\"target\":\"pieces_red\"}", 100, 10),
                ToolRound("get_config_slice", "{\"target\":\"pieces_black\"}", 100, 10),
                ToolRound("commit_preview", "{\"summary\":\"读完了\"}", 100, 10),
            };
            var i = 0;
            var config = AgentRemakeConfig.FromJson(JObject.Parse(
                "{\"limits\":{\"max_rounds\":6,\"repeat_tool_cap\":3}}"));
            var engine = NewEngine(req => Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]), config);

            var result = await engine.ProcessCommandAsync("并行读多个切片", Context(), CancellationToken.None);

            Assert.AreEqual(6, engine.LastResult.Rounds, "5 次不同参数读 + commit 应全部执行");
            Assert.AreNotEqual("REPEAT_LOOP", engine.LastResult.ErrorCode, "不同参数不得触发重复熔断");
            Assert.AreEqual("fun", result.Value<string>("type"), "零写入收尾 = 闲聊");
            });
        }

        // ── 传输失败：不重试，向上抛（§5.3） ──────────────────────────

        [Test]
        public void TransportError_Throws_NotRetriedInLoop()
        {
            var calls = 0;
            var engine = NewEngine(req =>
            {
                calls++;
                return Task.FromResult(new LlmRawResponse { TransportError = true });
            });

            // Unity 2022 内置 NUnit 无 Assert.ThrowsAsync（dotnet 侧有）——用同步等待保持双跑口径一致。
            Assert.Throws<InvalidOperationException>(() =>
                engine.ProcessCommandAsync("x", Context(), CancellationToken.None).GetAwaiter().GetResult());
            Assert.AreEqual(1, calls, "传输失败不在循环内重试（直接向上抛给 CheatController 计入 ConsecutiveFailures）");
        }

        // ── S? 离线：无 mock 无 key ⇒ 零 LLM 调用 ─────────────────────

        [Test]
        public void Offline_ZeroLlmCalls_ErrorResult()
        {
            TestEnv.RunSync(async () =>
            {
            var client = new LlmClient(apiKey: string.Empty, baseUrl: "https://mock.invalid/v1", model: "m");
            var engine = new AgentEngine(client, sessionSeed: Seed);

            var result = await engine.ProcessCommandAsync("x", Context(), CancellationToken.None);

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("error", result.Value<string>("type"));
            });
        }

        // ── S11 前缀稳定性 ───────────────────────────────────────────

        [Test]
        public void PrefixStability_TwoRequests_SameSystemAndUser()
        {
            TestEnv.RunSync(async () =>
            {
            LlmRequest first = null, second = null;
            var engine = NewEngine(req =>
            {
                if (first == null) first = req;
                else second = req;
                return Task.FromResult(TextRound("闲聊", 1000, 50));
            });

            await engine.ProcessCommandAsync("同一句话", Context(), CancellationToken.None);
            await engine.ProcessCommandAsync("同一句话", Context(), CancellationToken.None);

            Assert.AreEqual(Sha(first.Messages), Sha(second.Messages), "两次提交的 system+user 前缀必须字节级相等");
            });
        }

        [Test]
        public void PrefixStability_IncrementalAppend_NoRewrite()
        {
            TestEnv.RunSync(async () =>
            {
            var seenLengths = new List<int>();
            var messagesSeen = new List<List<LlmMessage>>();
            var script = new List<LlmRawResponse>
            {
                ToolRound("get_board_state", "{}", 1200, 300),
                ToolRound("commit_preview", "{\"summary\":\"完\"}", 1600, 120),
            };
            var i = 0;
            var engine = NewEngine(req =>
            {
                seenLengths.Add(req.Messages.Count);
                messagesSeen.Add(new List<LlmMessage>(req.Messages));
                return Task.FromResult(script[Math.Min(i++, script.Count - 1)]);
            });

            await engine.ProcessCommandAsync("看看", Context(), CancellationToken.None);

            Assert.AreEqual(2, seenLengths.Count);
            Assert.AreEqual(2, seenLengths[0], "首轮：system + user");
            Assert.Greater(seenLengths[1], seenLengths[0], "后续轮只追加（append-only）");
            // 前 n 条消息逐字节相等（入列后不得改写——R5/R6）。
            for (int k = 0; k < seenLengths[0]; k++)
                Assert.AreEqual(Serialize(messagesSeen[0][k]), Serialize(messagesSeen[1][k]), $"第 {k} 条消息被改写");
            });
        }

        // ── 读结果压缩：保留决策数据、丢弃装饰字段 ──────────────────

        [Test]
        public void ToolResults_CompactReceipt_KeepsEssentialData()
        {
            TestEnv.RunSync(async () =>
            {
            List<LlmMessage> lastMessages = null;
            var script = new List<LlmRawResponse>
            {
                ToolRound("get_board_state", "{}", 1200, 300),
                ToolRound("commit_preview", "{\"summary\":\"看完了\"}", 1600, 120),
            };
            var i = 0;
            var engine = NewEngine(req =>
            {
                lastMessages = new List<LlmMessage>(req.Messages);
                return Task.FromResult(script[Math.Min(i++, script.Count - 1)]);
            });

            await engine.ProcessCommandAsync("看看棋盘", Context(), CancellationToken.None);

            var toolMsg = lastMessages.Find(m => m.Role == "tool");
            Assert.IsNotNull(toolMsg);
            // 修复前是「[已压缩] 结果已阅」——模型看不到数据，只能猜路径。
            StringAssert.Contains("r_chariot_1", toolMsg.Content, "压缩回执必须保留棋子 id（模型决策所需）");
            StringAssert.Contains("\"position\"", toolMsg.Content, "压缩回执必须保留坐标");
            StringAssert.DoesNotContain("custom_properties", toolMsg.Content, "装饰字段应丢弃");
            Assert.Less(toolMsg.Content.Length, 1500, "回执应显著小于原始结果");
            });
        }

        // ── 结果对象接缝：TokenEff/Rounds/Trajectory/KarmaResult ──────

        [Test]
        public void ResultExposes_TelemetrySeams()
        {
            TestEnv.RunSync(async () =>
            {
            var script = new List<LlmRawResponse>
            {
                ToolRound("get_board_state", "{}", 1000, 300, thinking: 100),
                ToolRound("commit_preview", "{\"summary\":\"完\"}", 1400, 150, thinking: 50),
            };
            var i = 0;
            var engine = NewEngine(req => Task.FromResult(script[Math.Min(i++, script.Count - 1)]));

            await engine.ProcessCommandAsync("x", Context(), CancellationToken.None);

            var r = engine.LastResult;
            Assert.AreEqual(2850, r.TokenEff, "T_eff = Σ(prompt+completion)，thinking 全额含在 completion");
            Assert.AreEqual(2, r.Rounds);
            Assert.AreEqual(2, r.Trajectory.Count);
            Assert.IsNotNull(r.Karma);
            Assert.AreEqual(1, r.Trajectory[0].Round, "Round 记录发生轮");
            Assert.AreEqual(2, r.Trajectory[1].Round);
            });
        }

        static string Serialize(LlmMessage m) => LlmClient.SerializeMessages(new List<LlmMessage> { m })[0].ToString(Newtonsoft.Json.Formatting.None);

        static string Sha(List<LlmMessage> messages)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    LlmClient.SerializeMessages(messages).ToString(Newtonsoft.Json.Formatting.None)));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
