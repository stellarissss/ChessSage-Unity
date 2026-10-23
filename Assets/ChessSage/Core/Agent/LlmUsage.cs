namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 单轮 LLM 用量（E0-S1 纯数据类型；E1-S1 起由 <c>LlmClient.TokenUsageCallback</c> 产出）。
    ///
    /// 【计量口径】T_eff = Σ(prompt_tokens + completion_tokens)，completion 含 thinking
    /// （DeepSeek 计费口径全额计入）；<see cref="ReasoningTokens"/> 与
    /// <see cref="CachedPromptTokens"/> 是分列遥测（子集关系），绝不另计入 T_eff。
    /// </summary>
    public sealed class LlmUsage
    {
        /// <summary>prompt_tokens（名义值；缓存命中部分见 <see cref="CachedPromptTokens"/>）。</summary>
        public int PromptTokens;
        /// <summary>completion_tokens（含 thinking token）。</summary>
        public int CompletionTokens;
        /// <summary>completion_tokens_details.reasoning_tokens（thinking 分列，completion 子集）。</summary>
        public int ReasoningTokens;
        /// <summary>prompt_tokens_details.cached_tokens（前缀缓存命中分列，prompt 子集）。</summary>
        public int CachedPromptTokens;

        /// <summary>本轮计入 T_eff 的 token 数。</summary>
        public int TotalTokens => PromptTokens + CompletionTokens;
    }
}
