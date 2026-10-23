using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>思考模式（§6.3）：Default = 服务端默认（thinking 开，不发送该字段）。</summary>
    public enum LlmThinkingMode
    {
        Default = 0,
        Enabled = 1,
        Disabled = 2,
    }

    /// <summary>通用消息（system/user/assistant/tool；§6.1）。</summary>
    public sealed class LlmMessage
    {
        public string Role;
        /// <summary>可空（assistant 仅 tool_calls 时）。</summary>
        public string Content;
        /// <summary>assistant 发出的工具调用（原样回传给 API）。</summary>
        public List<LlmToolCall> ToolCalls;
        /// <summary>tool 结果回填的调用 id。</summary>
        public string ToolCallId;
        /// <summary>
        /// thinking 模式的思考内容。**不是纯遥测**：多轮工具调用时服务端要求把上一轮
        /// assistant 的 reasoning_content 原样回传，缺失会 HTTP 400（实测 E3 校准 C+ #47）。
        /// 仅在请求以 thinking 模式发出时回传（<see cref="LlmClient.SerializeMessages"/> 的开关）。
        /// </summary>
        public string ReasoningContent;
    }

    /// <summary>单次工具调用（OpenAI 标准形状；arguments 是 JSON 字符串，原样保留）。</summary>
    public sealed class LlmToolCall
    {
        public string Id;
        public string Name;
        public string ArgumentsJson;
    }

    /// <summary>工具定义（tools 数组元素；name/description/parameters）。</summary>
    public sealed class LlmToolDef
    {
        public string Name;
        public string Description;
        public JObject Parameters;
    }

    /// <summary>
    /// 工具调用请求（§6.1）。Tools 为已线格式化的 tools 数组
    /// （<see cref="AgentToolSchemas.SerializeTools"/> 的 JArray 解析结果——字节级稳定前提 R3）。
    /// </summary>
    public sealed class LlmRequest
    {
        public List<LlmMessage> Messages;
        /// <summary>空/null = 普通对话（不发送 tools/tool_choice）。</summary>
        public JArray Tools;
        public double? Temperature;
        public int? MaxTokens;
        public string Model;
        public LlmThinkingMode Thinking = LlmThinkingMode.Default;
        /// <summary>"low"/"high"/null（null = 服务端默认）。</summary>
        public string ReasoningEffort;
        public string ToolChoice = "auto";
    }

    /// <summary>可注入传输 seam（§10.1）：收 <see cref="LlmRequest"/> 还 <see cref="LlmRawResponse"/>——mock/黄金回放的地基。</summary>
    public delegate Task<LlmRawResponse> LlmTransport(LlmRequest request);

    /// <summary>
    /// LLM 原始响应（含 token 用量），供 mock 与真实传输统一返回。
    /// E1-S1 扩展：ToolCalls / FinishReason / ReasoningContent / ReasoningTokens / CachedPromptTokens / TransportError；
    /// Content/PromptTokens/CompletionTokens 旧字段保留（旧 MockHandler 消费者零影响）。
    /// </summary>
    public sealed class LlmRawResponse
    {
        public string Content;
        public string ReasoningContent;
        /// <summary>assistant 发出的工具调用（无则空列表）。</summary>
        public List<LlmToolCall> ToolCalls = new List<LlmToolCall>();
        /// <summary>"stop" | "tool_calls" | "length"（缺省 "stop"）。</summary>
        public string FinishReason = "stop";
        public int PromptTokens;
        public int CompletionTokens;
        /// <summary>completion_tokens_details.reasoning_tokens（thinking 分列；completion 子集）。</summary>
        public int ReasoningTokens;
        /// <summary>prompt_tokens_details.cached_tokens（前缀缓存命中分列；prompt 子集）。</summary>
        public int CachedPromptTokens;
        /// <summary>true = 传输层失败（模拟断网/5xx）；调用方必须得到 Ok=false 而非异常。</summary>
        public bool TransportError;
        /// <summary>
        /// mock 专用：模拟 HTTP 错误体（非空即 Ok=false）。与 <see cref="HttpStatus"/> 配合，
        /// 用于受控复现「400 reasoning_content」这类可自愈协议错误（测试/回放）。
        /// </summary>
        public string Error;
        /// <summary>mock 专用：模拟 HTTP 状态码（0 = 非 HTTP 失败）。</summary>
        public int HttpStatus;

        /// <summary>E0 <see cref="LlmUsage"/> 视图（T_eff 计量口径；thinking 全额含在 completion）。</summary>
        public LlmUsage Usage => new LlmUsage
        {
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,
            ReasoningTokens = ReasoningTokens,
            CachedPromptTokens = CachedPromptTokens,
        };
    }

    /// <summary>
    /// 单次 LLM 调用结果；网络/协议失败以 Ok=false 表达，绝不抛出未捕获异常。
    /// E1-S1 扩展：ToolCalls / FinishReason / ReasoningContent / Usage。
    /// </summary>
    public sealed class LlmResult
    {
        public bool Ok;
        public string Content;
        public string ReasoningContent;
        public List<LlmToolCall> ToolCalls = new List<LlmToolCall>();
        public string FinishReason;
        public LlmUsage Usage;
        public double ElapsedSeconds;
        public string Error;
        /// <summary>HTTP 状态码（成功 200；mock/本地失败 0）。供上层区分可自愈的 400 与致命 401/402/429。</summary>
        public int HttpStatus;
    }

    /// <summary>
    /// OpenAI 兼容 chat/completions 客户端（DeepSeek 等）。
    /// 默认 stream=false；推理开关由调用方通过 noThink 参数控制（对应 ai_config.get_no_think_params）。
    /// 双传输缝：<see cref="MockHandler"/>（旧形状，非 agent 调用方零影响）与
    /// <see cref="MockToolHandler"/>（§6.1 LlmTransport，agent/mock 回放，messages 历史可见）。
    /// </summary>
    public sealed class LlmClient
    {
        static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public string ApiKey;
        public string BaseUrl;
        public string DefaultModel;
        public double DefaultTemperature = 0.3;
        public int DefaultMaxTokens = 8192;
        public double DefaultTimeoutSeconds = 120.0;

        /// <summary>token 用量回调（promptTokens, completionTokens），对应 Python 的 _record_token_usage。
        /// 【E1 保留】旧形状原样不动——其原消费者（旧双层级编排器）已随 E2-S7 下线；保留为对外兼容缝。</summary>
        public Action<int, int> TokenUsageCallback;

        /// <summary>
        /// E1-S1 新增：全量用量回调（含 thinking / 缓存分列）。每次成功调用（含 mock）恰好触发一次，
        /// try/catch 包裹——回调失败不影响主流程。agent 侧 <see cref="TokenLedger"/> 订阅此缝。
        /// </summary>
        public Action<LlmUsage> TokenUsageCallbackEx;

        /// <summary>
        /// 自定义传输（mock，旧形状）。签名：(system, user, temperature, maxTokens, model, noThink) =&gt; 原始响应。
        /// 非空时走该路径，不发起任何网络请求。公开行为自 v1.0.1 起未变。
        /// </summary>
        public Func<string, string, double, int, string, bool, Task<LlmRawResponse>> MockHandler;

        /// <summary>
        /// E1-S1 新增：agent 传输缝（§6.1/§10.1）。收 <see cref="LlmRequest"/>（messages 历史对 mock 可见，
        /// 预编排多轮 tool_calls 响应序列的地基），还 <see cref="LlmRawResponse"/>。
        /// </summary>
        public LlmTransport MockToolHandler;

        public LlmClient(string apiKey = null, string baseUrl = null, string model = null)
        {
            ApiKey = apiKey ?? AiConfig.GetApiKey();
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? AiConfig.GetBaseUrl() : baseUrl;
            DefaultModel = string.IsNullOrEmpty(model) ? AiConfig.GetModel() : model;
        }

        /// <summary>是否已注入旧形状 mock 传输。</summary>
        public bool HasMock => MockHandler != null;

        /// <summary>是否已注入 agent 传输缝。</summary>
        public bool HasMockToolHandler => MockToolHandler != null;

        /// <summary>是否具备可用调用能力（任一 mock 或已配置密钥）。</summary>
        public bool IsAvailable => HasMock || HasMockToolHandler || !string.IsNullOrEmpty(ApiKey);

        /// <summary>发起一次 chat/completions 调用；失败时返回 Ok=false 的结构化结果。</summary>
        public async Task<LlmResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            double? temperature = null,
            int? maxTokens = null,
            string model = null,
            bool noThink = false,
            double? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            var start = DateTime.UtcNow;
            var temp = temperature ?? DefaultTemperature;
            var maxTok = maxTokens ?? DefaultMaxTokens;
            var mdl = string.IsNullOrEmpty(model) ? DefaultModel : model;

            if (MockHandler != null)
            {
                try
                {
                    var raw = await MockHandler(systemPrompt, userPrompt, temp, maxTok, mdl, noThink)
                        .ConfigureAwait(false);
                    if (raw == null)
                        return new LlmResult { Ok = false, Error = "mock 返回空响应", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                    RecordUsage(raw.Usage);
                    return new LlmResult
                    {
                        Ok = true,
                        Content = raw.Content ?? string.Empty,
                        ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds,
                    };
                }
                catch (Exception ex)
                {
                    return new LlmResult { Ok = false, Error = ex.Message, ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
            }

            if (string.IsNullOrEmpty(ApiKey))
                return new LlmResult { Ok = false, Error = "未设置API密钥", ElapsedSeconds = 0.0 };

            var payload = new JObject
            {
                ["model"] = mdl,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user", ["content"] = userPrompt },
                },
                ["temperature"] = temp,
                ["max_tokens"] = maxTok,
            };
            if (noThink)
                payload["thinking"] = new JObject { ["type"] = "disabled" };

            var url = BaseUrl.TrimEnd('/') + "/chat/completions";
            var timeout = timeoutSeconds ?? DefaultTimeoutSeconds;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
                        request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

                        using (var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                            if (!response.IsSuccessStatusCode)
                                return new LlmResult
                                {
                                    Ok = false,
                                    Error = $"HTTP {(int)response.StatusCode}: {body}",
                                    ElapsedSeconds = elapsed,
                                    HttpStatus = (int)response.StatusCode,
                                };

                            var data = JToken.Parse(body) as JObject;
                            var content = data?["choices"]?[0]?["message"]?["content"];
                            var text = content == null || content.Type == JTokenType.Null ? string.Empty : content.ToString();

                            var usage = data?["usage"] as JObject;
                            if (usage != null)
                                RecordUsage(ParseUsage(usage));

                            return new LlmResult { Ok = true, Content = text, ElapsedSeconds = elapsed };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return new LlmResult { Ok = false, Error = "请求超时或被取消", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
                catch (Exception ex)
                {
                    return new LlmResult { Ok = false, Error = ex.Message, ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
            }
        }

        /// <summary>
        /// 单条 user 消息调用（对齐 karma_assessor 的 messages 仅含 user 的请求）。
        /// 失败时返回 Ok=false 的结构化结果，绝不抛出未捕获异常。
        /// </summary>
        public async Task<LlmResult> ChatUserOnlyAsync(
            string userPrompt,
            double? temperature = null,
            int? maxTokens = null,
            string model = null,
            bool noThink = false,
            double? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            var start = DateTime.UtcNow;
            var temp = temperature ?? DefaultTemperature;
            var maxTok = maxTokens ?? DefaultMaxTokens;
            var mdl = string.IsNullOrEmpty(model) ? DefaultModel : model;

            if (MockHandler != null)
            {
                try
                {
                    var raw = await MockHandler(string.Empty, userPrompt, temp, maxTok, mdl, noThink)
                        .ConfigureAwait(false);
                    if (raw == null)
                        return new LlmResult { Ok = false, Error = "mock 返回空响应", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                    RecordUsage(raw.Usage);
                    return new LlmResult
                    {
                        Ok = true,
                        Content = raw.Content ?? string.Empty,
                        ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds,
                    };
                }
                catch (Exception ex)
                {
                    return new LlmResult { Ok = false, Error = ex.Message, ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
            }

            if (string.IsNullOrEmpty(ApiKey))
                return new LlmResult { Ok = false, Error = "未设置API密钥", ElapsedSeconds = 0.0 };

            var payload = new JObject
            {
                ["model"] = mdl,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = userPrompt },
                },
                ["temperature"] = temp,
                ["max_tokens"] = maxTok,
            };
            if (noThink)
                payload["thinking"] = new JObject { ["type"] = "disabled" };

            var url = BaseUrl.TrimEnd('/') + "/chat/completions";
            var timeout = timeoutSeconds ?? DefaultTimeoutSeconds;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
                        request.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

                        using (var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                            if (!response.IsSuccessStatusCode)
                                return new LlmResult
                                {
                                    Ok = false,
                                    Error = $"HTTP {(int)response.StatusCode}: {body}",
                                    ElapsedSeconds = elapsed,
                                    HttpStatus = (int)response.StatusCode,
                                };

                            var data = JToken.Parse(body) as JObject;
                            var content = data?["choices"]?[0]?["message"]?["content"];
                            var text = content == null || content.Type == JTokenType.Null ? string.Empty : content.ToString();

                            var usage = data?["usage"] as JObject;
                            if (usage != null)
                                RecordUsage(ParseUsage(usage));

                            return new LlmResult { Ok = true, Content = text, ElapsedSeconds = elapsed };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return new LlmResult { Ok = false, Error = "请求超时或被取消", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
                catch (Exception ex)
                {
                    return new LlmResult { Ok = false, Error = ex.Message, ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
            }
        }

        // ── E1-S1 新增：agent 出口（§6.1/§6.3） ──────────────────────

        /// <summary>
        /// 工具调用薄包装：组装 <see cref="LlmRequest"/> 转调 <see cref="SendAsync"/>。
        /// noThink:true ⇔ <see cref="LlmThinkingMode.Disabled"/>；reasoningEffort 透传（"low"/"high"）。
        /// </summary>
        public Task<LlmResult> ChatToolsAsync(
            IList<LlmMessage> messages,
            JArray tools,
            double? temperature = null,
            int? maxTokens = null,
            string model = null,
            bool noThink = false,
            string reasoningEffort = null,
            double? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            var request = new LlmRequest
            {
                Messages = messages as List<LlmMessage> ?? new List<LlmMessage>(messages ?? new List<LlmMessage>()),
                Tools = tools,
                Temperature = temperature,
                MaxTokens = maxTokens,
                Model = string.IsNullOrEmpty(model) ? null : model,
                Thinking = noThink ? LlmThinkingMode.Disabled : LlmThinkingMode.Default,
                ReasoningEffort = string.IsNullOrEmpty(reasoningEffort) ? null : reasoningEffort,
            };
            return SendAsync(request, cancellationToken);
        }

        /// <summary>
        /// 唯一真实出口（§6.2）：序列化 → POST → 防御式解析 → <see cref="RecordUsage(LlmUsage)"/> 一次 → <see cref="LlmResult"/>。
        /// mock（<see cref="MockToolHandler"/>）优先：零网络、全字段（tool_calls/finish_reason/usage）结构化返回。
        /// </summary>
        public async Task<LlmResult> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var start = DateTime.UtcNow;
            if (request == null)
                return new LlmResult { Ok = false, Error = "请求为空", ElapsedSeconds = 0.0 };
            if (request.Messages == null || request.Messages.Count == 0)
                return new LlmResult { Ok = false, Error = "messages 不得为空", ElapsedSeconds = 0.0 };

            if (MockToolHandler != null)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new LlmResult { Ok = false, Error = "请求超时或被取消", ElapsedSeconds = 0.0 };
                try
                {
                    var raw = await MockToolHandler(request).ConfigureAwait(false);
                    if (raw == null)
                        return new LlmResult { Ok = false, Error = "mock 返回空响应", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                    if (raw.TransportError)
                        return new LlmResult { Ok = false, Error = "传输错误（mock 注入）", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                    if (raw.HttpStatus >= 400 || !string.IsNullOrEmpty(raw.Error))
                        return new LlmResult
                        {
                            Ok = false,
                            Error = string.IsNullOrEmpty(raw.Error)
                                ? $"HTTP {raw.HttpStatus}: (mock)"
                                : raw.Error,
                            ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds,
                            HttpStatus = raw.HttpStatus,
                        };
                    RecordUsage(raw.Usage);
                    return new LlmResult
                    {
                        Ok = true,
                        Content = raw.Content ?? string.Empty,
                        ReasoningContent = raw.ReasoningContent,
                        ToolCalls = raw.ToolCalls ?? new List<LlmToolCall>(),
                        FinishReason = string.IsNullOrEmpty(raw.FinishReason) ? "stop" : raw.FinishReason,
                        Usage = raw.Usage,
                        ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds,
                    };
                }
                catch (OperationCanceledException)
                {
                    return new LlmResult { Ok = false, Error = "请求超时或被取消", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
                catch (Exception ex)
                {
                    return new LlmResult { Ok = false, Error = ex.Message, ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
            }

            if (string.IsNullOrEmpty(ApiKey))
                return new LlmResult { Ok = false, Error = "未设置API密钥", ElapsedSeconds = 0.0 };

            var payloadText = BuildPayloadJson(request, string.IsNullOrEmpty(request.Model) ? DefaultModel : request.Model);
            var url = BaseUrl.TrimEnd('/') + "/chat/completions";

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));
                try
                {
                    using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
                        httpRequest.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");

                        using (var response = await Http.SendAsync(httpRequest, cts.Token).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                            if (!response.IsSuccessStatusCode)
                                return new LlmResult
                                {
                                    Ok = false,
                                    Error = $"HTTP {(int)response.StatusCode}: {body}",
                                    ElapsedSeconds = elapsed,
                                    HttpStatus = (int)response.StatusCode,
                                };

                            var data = JToken.Parse(body) as JObject;
                            var choice = data?["choices"]?[0] as JObject;
                            var message = choice?["message"] as JObject;
                            var contentToken = message?["content"];
                            var text = contentToken == null || contentToken.Type == JTokenType.Null
                                ? string.Empty
                                : contentToken.ToString();
                            var reasoningToken = message?["reasoning_content"];
                            var reasoning = reasoningToken == null || reasoningToken.Type == JTokenType.Null
                                ? null
                                : reasoningToken.ToString();

                            var toolCalls = new List<LlmToolCall>();
                            var callsToken = message?["tool_calls"] as JArray;
                            if (callsToken != null)
                                foreach (var t in callsToken)
                                {
                                    var call = t as JObject;
                                    var fn = call?["function"] as JObject;
                                    if (fn == null) continue;
                                    toolCalls.Add(new LlmToolCall
                                    {
                                        Id = call.Value<string>("id") ?? string.Empty,
                                        Name = fn.Value<string>("name") ?? string.Empty,
                                        // arguments 是 JSON 字符串——原样保留，解析失败走循环层自愈（§5.3）
                                        ArgumentsJson = fn["arguments"]?.Type == JTokenType.String
                                            ? fn.Value<string>("arguments")
                                            : fn["arguments"]?.ToString(Newtonsoft.Json.Formatting.None),
                                    });
                                }

                            var finish = choice?["finish_reason"];
                            var finishReason = finish == null || finish.Type == JTokenType.Null
                                ? "stop"
                                : finish.ToString();

                            var usageToken = data?["usage"] as JObject;
                            var usage = usageToken != null ? ParseUsage(usageToken) : new LlmUsage();
                            if (usageToken != null)
                                RecordUsage(usage);

                            return new LlmResult
                            {
                                Ok = true,
                                Content = text,
                                ReasoningContent = reasoning,
                                ToolCalls = toolCalls,
                                FinishReason = finishReason,
                                Usage = usage,
                                ElapsedSeconds = elapsed,
                            };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return new LlmResult { Ok = false, Error = "请求超时或被取消", ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
                catch (Exception ex)
                {
                    return new LlmResult { Ok = false, Error = ex.Message, ElapsedSeconds = (DateTime.UtcNow - start).TotalSeconds };
                }
            }
        }

        /// <summary>
        /// 请求体线格式（§6.3 已核验口径；静态可单测）。
        /// 键序固定（model/messages/tools/tool_choice/temperature/max_tokens/thinking/reasoning_effort）——
        /// Newtonsoft JObject 保插入序，字节级确定。
        /// </summary>
        public static string BuildPayloadJson(LlmRequest request, string model)
        {
            var payload = new JObject
            {
                ["model"] = model,
                // thinking 模式必须回传 assistant.reasoning_content（多轮工具调用硬要求）。
                ["messages"] = SerializeMessages(request.Messages,
                    request.Thinking != LlmThinkingMode.Disabled),
            };
            if (request.Tools != null && request.Tools.Count > 0)
            {
                payload["tools"] = request.Tools;
                if (!string.IsNullOrEmpty(request.ToolChoice))
                    payload["tool_choice"] = request.ToolChoice;
            }
            payload["temperature"] = request.Temperature ?? 0.3;
            payload["max_tokens"] = request.MaxTokens ?? 8192;
            if (request.Thinking == LlmThinkingMode.Disabled)
                payload["thinking"] = new JObject { ["type"] = "disabled" };
            if (!string.IsNullOrEmpty(request.ReasoningEffort))
                payload["reasoning_effort"] = request.ReasoningEffort;
            return payload.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// messages 线格式（assistant.tool_calls 原样序列化；tool 消息 = {role,tool_call_id,content}）。
        /// 确定性：键序固定 + 空内容写 null——同输入同字节（前缀缓存前提，§13.2 R1-R7）。
        ///
        /// <paramref name="includeReasoningContent"/>：thinking 模式多轮工具调用要求回传
        /// assistant.reasoning_content（缺字段 ⇒ HTTP 400）；非 thinking 请求不得带该字段。
        /// 默认 false —— 黄金夹具的 request_digest 与旧口径逐字节一致。
        /// </summary>
        public static JArray SerializeMessages(IList<LlmMessage> messages, bool includeReasoningContent = false)
        {
            var array = new JArray();
            if (messages == null) return array;
            foreach (var m in messages)
            {
                if (m == null) continue;
                var role = m.Role ?? string.Empty;
                if (role == "assistant")
                {
                    var obj = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = m.Content == null ? (JToken)null : JValue.CreateString(m.Content),
                    };
                    if (includeReasoningContent && !string.IsNullOrEmpty(m.ReasoningContent))
                        obj["reasoning_content"] = m.ReasoningContent;
                    if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        var calls = new JArray();
                        foreach (var c in m.ToolCalls)
                        {
                            if (c == null) continue;
                            calls.Add(new JObject
                            {
                                ["id"] = c.Id ?? string.Empty,
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = c.Name ?? string.Empty,
                                    ["arguments"] = c.ArgumentsJson ?? "{}",
                                },
                            });
                        }
                        obj["tool_calls"] = calls;
                    }
                    array.Add(obj);
                }
                else if (role == "tool")
                {
                    array.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId ?? string.Empty,
                        ["content"] = m.Content ?? string.Empty,
                    });
                }
                else
                {
                    array.Add(new JObject
                    {
                        ["role"] = role,
                        ["content"] = m.Content ?? string.Empty,
                    });
                }
            }
            return array;
        }

        /// <summary>usage 防御式解析（缺字段不炸；§6.3）：reasoning/cached 缺省 0。</summary>
        static LlmUsage ParseUsage(JObject usage)
        {
            var reasoning = usage["completion_tokens_details"] as JObject;
            var cached = usage["prompt_tokens_details"] as JObject;
            return new LlmUsage
            {
                PromptTokens = JsonHelpers.GetInt(usage, "prompt_tokens"),
                CompletionTokens = JsonHelpers.GetInt(usage, "completion_tokens"),
                ReasoningTokens = reasoning != null ? JsonHelpers.GetInt(reasoning, "reasoning_tokens") : 0,
                CachedPromptTokens = cached != null ? JsonHelpers.GetInt(cached, "cached_tokens") : 0,
            };
        }

        void RecordUsage(LlmUsage usage)
        {
            if (usage == null) return;
            // 旧形状回调先行（兼容旧调用方），全量回调随后——两者并存，互不影响。
            try { TokenUsageCallback?.Invoke(usage.PromptTokens, usage.CompletionTokens); }
            catch { /* 统计回调失败不影响主流程 */ }
            try { TokenUsageCallbackEx?.Invoke(usage); }
            catch { /* 同上 */ }
        }
    }
}
