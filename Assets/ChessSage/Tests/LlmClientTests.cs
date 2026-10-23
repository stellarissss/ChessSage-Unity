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
    /// E1-S1：LlmClient 扩展（tools/tool_calls/tool 角色消息/thinking 计量/MockToolHandler）。
    /// 期望值独立推导：请求/解析口径 = 工程侧 §6.3 已核验的 DeepSeek 现行标准（2026-10-22）。
    /// 旧 MockHandler / ChatAsync / ChatUserOnlyAsync / TokenUsageCallback 行为原样保留（非 agent 调用方零影响）。
    /// </summary>
    [TestFixture]
    public sealed class LlmClientTests
    {
        static LlmClient NewMockClient(LlmTransport transport)
        {
            var client = new LlmClient(apiKey: "test-key", baseUrl: "https://mock.invalid/v1", model: "deepseek-flash");
            client.MockToolHandler = transport;
            return client;
        }

        static LlmMessage Sys(string text) => new LlmMessage { Role = "system", Content = text };
        static LlmMessage User(string text) => new LlmMessage { Role = "user", Content = text };

        static LlmRawResponse ToolCallResp(string id, string name, string args,
            int prompt = 100, int completion = 40, int thinking = 15, int cached = 0, string finish = "tool_calls")
            => new LlmRawResponse
            {
                Content = null,
                FinishReason = finish,
                PromptTokens = prompt,
                CompletionTokens = completion,
                ReasoningTokens = thinking,
                CachedPromptTokens = cached,
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = id, Name = name, ArgumentsJson = args } },
            };

        // ── 1. ChatToolsAsync 基本形状 ──────────────────────────────

        [Test]
        public void SendAsync_MockToolHandler_ReturnsToolCallsAndUsage()
        {
            TestEnv.RunSync(async () =>
            {
            var client = NewMockClient(req => Task.FromResult(
                ToolCallResp("call_1", "get_board_state", "{}")));

            var tools = JArray.Parse(AgentToolSchemas.SerializeTools(AgentToolSchemas.All));
            var result = await client.SendAsync(new LlmRequest
            {
                Messages = new List<LlmMessage> { Sys("s"), User("u") },
                Tools = tools,
            }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Ok, "mock 传输应成功");
            Assert.AreEqual("tool_calls", result.FinishReason);
            Assert.AreEqual(1, result.ToolCalls.Count);
            Assert.AreEqual("call_1", result.ToolCalls[0].Id);
            Assert.AreEqual("get_board_state", result.ToolCalls[0].Name);
            Assert.AreEqual("{}", result.ToolCalls[0].ArgumentsJson);
            Assert.AreEqual(140, result.Usage.TotalTokens, "T_eff 口径 = prompt + completion");
            Assert.AreEqual(15, result.Usage.ReasoningTokens);
            });
        }

        [Test]
        public void SendAsync_MockToolHandler_SeesFullMessageHistory()
        {
            TestEnv.RunSync(async () =>
            {
            LlmRequest seen = null;
            var client = NewMockClient(req => { seen = req; return Task.FromResult(ToolCallResp("c", "t", "{}")); });

            var messages = new List<LlmMessage>
            {
                Sys("system"),
                User("command"),
                new LlmMessage { Role = "assistant", Content = null, ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "c0", Name = "get_board_state", ArgumentsJson = "{}" } } },
                new LlmMessage { Role = "tool", ToolCallId = "c0", Content = "{\"ok\":true}" },
            };
            await client.SendAsync(new LlmRequest { Messages = messages }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(seen, "mock 必须收到请求");
            Assert.AreEqual(4, seen.Messages.Count, "messages 历史对 mock 可见（预编排多轮的地基）");
            Assert.AreEqual("tool", seen.Messages[3].Role);
            Assert.AreEqual("c0", seen.Messages[3].ToolCallId);
            Assert.AreEqual(1, seen.Messages[2].ToolCalls.Count);
            });
        }

        [Test]
        public void SendAsync_NoTools_PlainChatStillWorks()
        {
            TestEnv.RunSync(async () =>
            {
            var client = NewMockClient(req =>
            {
                Assert.IsNull(req.Tools, "无 tools 时请求不携带工具数组");
                return Task.FromResult(new LlmRawResponse { Content = "答案", FinishReason = "stop", PromptTokens = 10, CompletionTokens = 5 });
            });

            var result = await client.SendAsync(new LlmRequest
            {
                Messages = new List<LlmMessage> { Sys("s"), User("u") },
            }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("答案", result.Content);
            Assert.AreEqual("stop", result.FinishReason);
            Assert.AreEqual(0, result.ToolCalls.Count);
            });
        }

        // ── 2. TransportError（模拟断网/5xx，Ok=false 不抛） ─────────

        [Test]
        public void SendAsync_TransportError_ReturnsOkFalse_DoesNotThrow()
        {
            TestEnv.RunSync(async () =>
            {
            var client = NewMockClient(req => Task.FromResult(new LlmRawResponse
            {
                TransportError = true,
                PromptTokens = 0,
                CompletionTokens = 0,
            }));

            var result = await client.SendAsync(new LlmRequest
            {
                Messages = new List<LlmMessage> { Sys("s"), User("u") },
            }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Ok, "传输错误必须结构化返回 Ok=false，绝不抛异常");
            Assert.IsFalse(string.IsNullOrEmpty(result.Error));
            });
        }

        // ── 3. 用量回调：每轮必达 + 扩参含 thinking ──────────────────

        [Test]
        public void TokenUsageCallbackEx_FiresOncePerCall_WithThinking()
        {
            TestEnv.RunSync(async () =>
            {
            var usages = new List<LlmUsage>();
            var client = NewMockClient(req => Task.FromResult(
                ToolCallResp("c1", "get_board_state", "{}", prompt: 120, completion: 60, thinking: 25, cached: 40)));
            client.TokenUsageCallbackEx = u => usages.Add(u);

            var tools = JArray.Parse(AgentToolSchemas.SerializeTools(AgentToolSchemas.All));
            await client.SendAsync(new LlmRequest { Messages = new List<LlmMessage> { Sys("s"), User("u") }, Tools = tools }, CancellationToken.None).ConfigureAwait(false);
            await client.SendAsync(new LlmRequest { Messages = new List<LlmMessage> { Sys("s"), User("u2") }, Tools = tools }, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(2, usages.Count, "每次成功调用（含 mock）恰好回调一次");
            Assert.AreEqual(120, usages[0].PromptTokens);
            Assert.AreEqual(60, usages[0].CompletionTokens);
            Assert.AreEqual(25, usages[0].ReasoningTokens, "扩参含 thinking（DeepSeek 计费口径全额计入 completion）");
            Assert.AreEqual(40, usages[0].CachedPromptTokens);
            });
        }

        [Test]
        public void TokenUsageCallback_LegacyShape_StillFires_FromNewPath()
        {
            TestEnv.RunSync(async () =>
            {
            int legacyPrompt = -1, legacyCompletion = -1;
            var client = NewMockClient(req => Task.FromResult(
                ToolCallResp("c1", "get_board_state", "{}", prompt: 77, completion: 33, thinking: 12)));
            client.TokenUsageCallback = (p, c) => { legacyPrompt = p; legacyCompletion = c; };

            await client.SendAsync(new LlmRequest { Messages = new List<LlmMessage> { Sys("s"), User("u") } }, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(77, legacyPrompt, "旧 Action<int,int> 回调形状原样保留（OrchestratorEngine 消费者不受影响）");
            Assert.AreEqual(33, legacyCompletion);
            });
        }

        // ── 4. 旧 MockHandler / ChatAsync 公开行为原样保留 ───────────

        [Test]
        public void ChatAsync_LegacyMockHandler_UnchangedBehavior()
        {
            TestEnv.RunSync(async () =>
            {
            var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "m");
            client.MockHandler = (system, user, temp, maxTok, mdl, noThink) =>
            {
                Assert.AreEqual("sys", system);
                Assert.AreEqual("usr", user);
                Assert.AreEqual(0.3, temp);
                Assert.AreEqual(8192, maxTok);
                Assert.AreEqual("m", mdl);
                Assert.IsFalse(noThink);
                return Task.FromResult(new LlmRawResponse { Content = "legacy", PromptTokens = 11, CompletionTokens = 22 });
            };

            int lp = -1, lc = -1;
            client.TokenUsageCallback = (p, c) => { lp = p; lc = c; };

            var result = await client.ChatAsync("sys", "usr").ConfigureAwait(false);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("legacy", result.Content);
            Assert.AreEqual(11, lp);
            Assert.AreEqual(22, lc);
            });
        }

        [Test]
        public void ChatUserOnlyAsync_LegacyMockHandler_UnchangedBehavior()
        {
            TestEnv.RunSync(async () =>
            {
            var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "m");
            string seenSystem = null;
            client.MockHandler = (system, user, temp, maxTok, mdl, noThink) =>
            {
                seenSystem = system;
                return Task.FromResult(new LlmRawResponse { Content = "user-only", PromptTokens = 1, CompletionTokens = 2 });
            };

            var result = await client.ChatUserOnlyAsync("hello").ConfigureAwait(false);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("user-only", result.Content);
            Assert.AreEqual(string.Empty, seenSystem, "ChatUserOnlyAsync 的 system 恒为空串（对齐旧行为）");
            });
        }

        [Test]
        public void HasMock_And_IsAvailable_Semantics_Preserved_And_Extended()
        {
            var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "m");
            Assert.IsFalse(client.HasMock);
            Assert.IsTrue(client.IsAvailable, "有 key 即可用");

            client.MockHandler = (s, u, t, m, mdl, nt) => Task.FromResult(new LlmRawResponse());
            Assert.IsTrue(client.HasMock);
            Assert.IsTrue(client.IsAvailable);

            var noKey = new LlmClient(apiKey: string.Empty, baseUrl: "https://mock.invalid/v1", model: "m");
            Assert.IsFalse(noKey.IsAvailable, "无 key 无 mock ⇒ 不可用（AGT-09 离线判据）");

            var mockOnly = new LlmClient(apiKey: null, baseUrl: "https://mock.invalid/v1", model: "m");
            mockOnly.MockToolHandler = req => Task.FromResult(new LlmRawResponse { Content = "x" });
            Assert.IsTrue(mockOnly.IsAvailable, "MockToolHandler 注入即视为可用传输");
        }

        // ── 5. 请求体线格式（§6.3 已核验口径，可单测断言） ───────────

        [Test]
        public void BuildPayload_ToolCallsStandardOpenAIShape()
        {
            var request = new LlmRequest
            {
                Messages = new List<LlmMessage>
                {
                    Sys("sys"),
                    User("cmd"),
                    new LlmMessage
                    {
                        Role = "assistant", Content = null,
                        ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "call_1", Name = "move_piece", ArgumentsJson = "{\"action\":\"move\"}" } },
                    },
                    new LlmMessage { Role = "tool", ToolCallId = "call_1", Content = "{\"ok\":true}" },
                },
                Tools = JArray.Parse("[{\"type\":\"function\",\"function\":{\"name\":\"move_piece\",\"description\":\"d\",\"parameters\":{\"type\":\"object\"}}}]"),
                Temperature = 0.2,
                MaxTokens = 2048,
                ReasoningEffort = "low",
            };

            var payload = JObject.Parse(LlmClient.BuildPayloadJson(request, "deepseek-flash"));

            Assert.AreEqual("deepseek-flash", payload.Value<string>("model"));
            Assert.AreEqual(0.2, payload.Value<double>("temperature"));
            Assert.AreEqual(2048, payload.Value<int>("max_tokens"));
            Assert.AreEqual("low", payload.Value<string>("reasoning_effort"));
            Assert.AreEqual("auto", payload.Value<string>("tool_choice"));
            Assert.IsFalse(payload["thinking"] != null, "Default 思考模式不发送 thinking 字段");

            var msgs = (JArray)payload["messages"];
            Assert.AreEqual(4, msgs.Count);
            var assistant = (JObject)msgs[2];
            Assert.AreEqual("assistant", assistant.Value<string>("role"));
            var tcs = (JArray)assistant["tool_calls"];
            Assert.AreEqual(1, tcs.Count);
            var fn = (JObject)((JObject)tcs[0])["function"];
            Assert.AreEqual("move_piece", fn.Value<string>("name"));
            Assert.AreEqual("{\"action\":\"move\"}", fn.Value<string>("arguments"), "arguments 是 JSON 字符串，原样透传");
            var toolMsg = (JObject)msgs[3];
            Assert.AreEqual("tool", toolMsg.Value<string>("role"));
            Assert.AreEqual("call_1", toolMsg.Value<string>("tool_call_id"));
        }

        [Test]
        public void BuildPayload_NoThinkMode_SendsThinkingDisabled()
        {
            var request = new LlmRequest
            {
                Messages = new List<LlmMessage> { Sys("s"), User("u") },
                Thinking = LlmThinkingMode.Disabled,
            };

            var payload = JObject.Parse(LlmClient.BuildPayloadJson(request, "m"));
            Assert.AreEqual("disabled", payload["thinking"].Value<string>("type"), "noThink:true ⇔ thinking.type=disabled");
        }

        // ── 7. ChatToolsAsync 便捷入口（Story 规格点名，转 SendAsync） ──

        [Test]
        public void ChatToolsAsync_DelegatesToSendAsync_WithToolsAndEffort()
        {
            TestEnv.RunSync(async () =>
            {
            LlmRequest seen = null;
            var client = NewMockClient(req => { seen = req; return Task.FromResult(ToolCallResp("c1", "get_board_state", "{}")); });
            var tools = JArray.Parse(AgentToolSchemas.SerializeTools(AgentToolSchemas.All));

            var result = await client.ChatToolsAsync(
                new List<LlmMessage> { Sys("s"), User("u") }, tools, reasoningEffort: "low").ConfigureAwait(false);

            Assert.IsTrue(result.Ok);
            Assert.IsNotNull(seen, "ChatToolsAsync 必须转 SendAsync（mock 收到请求）");
            Assert.AreEqual(2, seen.Messages.Count);
            Assert.IsNotNull(seen.Tools, "tools 透传");
            Assert.AreEqual("low", seen.ReasoningEffort);
            Assert.AreEqual("auto", seen.ToolChoice);
            Assert.AreEqual(1, result.ToolCalls.Count);
            Assert.AreEqual("get_board_state", result.ToolCalls[0].Name);
            });
        }

        // ── 6. 取消贯通（20s 硬超时由组合根传入，客户端只负责透传） ──

        [Test]
        public void SendAsync_CancelledToken_ReturnsOkFalse()
        {
            TestEnv.RunSync(async () =>
            {
            var client = NewMockClient(req => throw new OperationCanceledException("cancelled"));
            var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await client.SendAsync(new LlmRequest
            {
                Messages = new List<LlmMessage> { Sys("s"), User("u") },
            }, cts.Token).ConfigureAwait(false);

            Assert.IsFalse(result.Ok, "取消必须结构化返回，不向上抛");
            });
        }

        // ── 8. thinking 模式 reasoning_content 回传（E3-C 400 修复） ──

        [Test]
        public void SerializeMessages_ReasoningContent_OnlyWhenRequested()
        {
            var messages = new List<LlmMessage>
            {
                Sys("s"),
                User("u"),
                new LlmMessage
                {
                    Role = "assistant",
                    Content = null,
                    ReasoningContent = "先看棋盘",
                    ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "c1", Name = "get_board_state", ArgumentsJson = "{}" } },
                },
            };

            var without = (JObject)LlmClient.SerializeMessages(messages)[2];
            Assert.IsFalse(without.ContainsKey("reasoning_content"),
                "默认口径不带 reasoning_content（黄金夹具 request_digest 逐字节不变）");

            var with = (JObject)LlmClient.SerializeMessages(messages, includeReasoningContent: true)[2];
            Assert.AreEqual("先看棋盘", with.Value<string>("reasoning_content"));
            Assert.AreEqual("assistant", with.Value<string>("role"));
            Assert.IsNotNull(with["tool_calls"], "tool_calls 与 reasoning_content 必须同时在场");
        }

        [Test]
        public void BuildPayload_ThinkingMode_EchoesReasoningContent_NoThinkOmitsIt()
        {
            var messages = new List<LlmMessage>
            {
                Sys("s"),
                User("u"),
                new LlmMessage
                {
                    Role = "assistant",
                    Content = null,
                    ReasoningContent = "想一想",
                    ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "c1", Name = "get_board_state", ArgumentsJson = "{}" } },
                },
            };

            var thinkingPayload = JObject.Parse(LlmClient.BuildPayloadJson(
                new LlmRequest { Messages = messages }, "deepseek-flash"));
            Assert.AreEqual("想一想",
                ((JObject)thinkingPayload["messages"][2]).Value<string>("reasoning_content"),
                "thinking 模式必须回传 reasoning_content（多轮工具调用的硬要求）");

            var noThinkPayload = JObject.Parse(LlmClient.BuildPayloadJson(
                new LlmRequest { Messages = messages, Thinking = LlmThinkingMode.Disabled }, "deepseek-flash"));
            Assert.IsFalse(((JObject)noThinkPayload["messages"][2]).ContainsKey("reasoning_content"),
                "非 thinking 请求不得携带 reasoning_content（避免非法字段）");
        }

        [Test]
        public void SendAsync_MockHttpError_SurfacesStatusForSelfHealing()
        {
            TestEnv.RunSync(async () =>
            {
            var client = NewMockClient(req => Task.FromResult(new LlmRawResponse
            {
                HttpStatus = 400,
                Error = "HTTP 400: {\"error\":{\"message\":\"The `reasoning_content` in the thinking mode must be passed back to the API.\"}}",
            }));

            var result = await client.SendAsync(new LlmRequest
            {
                Messages = new List<LlmMessage> { Sys("s"), User("u") },
            }).ConfigureAwait(false);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(400, result.HttpStatus, "HttpStatus 必须透出，引擎据此区分可自愈 400 与致命 401/402/429");
            StringAssert.Contains("reasoning_content", result.Error);
            });
        }
    }
}
