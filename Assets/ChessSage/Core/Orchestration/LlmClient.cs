using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>LLM 原始响应（含 token 用量），供 mock 与真实传输统一返回。</summary>
    public sealed class LlmRawResponse
    {
        public string Content;
        public int PromptTokens;
        public int CompletionTokens;
    }

    /// <summary>单次 LLM 调用结果；网络/协议失败以 Ok=false 表达，绝不抛出未捕获异常。</summary>
    public sealed class LlmResult
    {
        public bool Ok;
        public string Content;
        public double ElapsedSeconds;
        public string Error;
    }

    /// <summary>
    /// OpenAI 兼容 chat/completions 客户端（DeepSeek 等）。
    /// 默认 stream=false；推理开关由调用方通过 noThink 参数控制（对应 ai_config.get_no_think_params）。
    /// 支持 <see cref="MockHandler"/> 注入，在无 API Key / 无网络时用于自测（返回结构化结果而非抛异常）。
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

        /// <summary>token 用量回调（promptTokens, completionTokens），对应 Python 的 _record_token_usage。</summary>
        public Action<int, int> TokenUsageCallback;

        /// <summary>
        /// 自定义传输（mock）。签名：(system, user, temperature, maxTokens, model, noThink) =&gt; 原始响应。
        /// 非空时走该路径，不发起任何网络请求。
        /// </summary>
        public Func<string, string, double, int, string, bool, Task<LlmRawResponse>> MockHandler;

        public LlmClient(string apiKey = null, string baseUrl = null, string model = null)
        {
            ApiKey = apiKey ?? AiConfig.GetApiKey();
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? AiConfig.GetBaseUrl() : baseUrl;
            DefaultModel = string.IsNullOrEmpty(model) ? AiConfig.GetModel() : model;
        }

        /// <summary>是否已注入 mock 传输。</summary>
        public bool HasMock => MockHandler != null;

        /// <summary>是否具备可用调用能力（mock 或已配置密钥）。</summary>
        public bool IsAvailable => HasMock || !string.IsNullOrEmpty(ApiKey);

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
                    RecordUsage(raw.PromptTokens, raw.CompletionTokens);
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
                                return new LlmResult { Ok = false, Error = $"HTTP {(int)response.StatusCode}: {body}", ElapsedSeconds = elapsed };

                            var data = JToken.Parse(body) as JObject;
                            var content = data?["choices"]?[0]?["message"]?["content"];
                            var text = content == null || content.Type == JTokenType.Null ? string.Empty : content.ToString();

                            var usage = data?["usage"] as JObject;
                            if (usage != null)
                                RecordUsage(JsonHelpers.GetInt(usage, "prompt_tokens"), JsonHelpers.GetInt(usage, "completion_tokens"));

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

        void RecordUsage(int promptTokens, int completionTokens)
        {
            try { TokenUsageCallback?.Invoke(promptTokens, completionTokens); }
            catch { /* 统计回调失败不影响主流程 */ }
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
                    RecordUsage(raw.PromptTokens, raw.CompletionTokens);
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
                                return new LlmResult { Ok = false, Error = $"HTTP {(int)response.StatusCode}: {body}", ElapsedSeconds = elapsed };

                            var data = JToken.Parse(body) as JObject;
                            var content = data?["choices"]?[0]?["message"]?["content"];
                            var text = content == null || content.Type == JTokenType.Null ? string.Empty : content.ToString();

                            var usage = data?["usage"] as JObject;
                            if (usage != null)
                                RecordUsage(JsonHelpers.GetInt(usage, "prompt_tokens"), JsonHelpers.GetInt(usage, "completion_tokens"));

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
    }
}