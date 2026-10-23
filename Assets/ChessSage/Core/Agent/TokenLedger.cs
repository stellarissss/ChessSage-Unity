using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>日统计桶（既有 token_stats.json daily_stats 条目）。</summary>
    public sealed class DailyBucket
    {
        public long PromptTokens;
        public long CompletionTokens;
        public long Calls;
        public long TokenEff => PromptTokens + CompletionTokens;
    }

    /// <summary>
    /// 单次提交的遥测记录（工程侧 §13.7 全字段；环形保留最近 500 条）。
    /// token 四件套与派生字段（t_eff / prefix_hit_rate）由 <see cref="TokenLedger"/>  authoritative 填写。
    /// </summary>
    public sealed class AgentRequestRecord
    {
        public string Ts;                     // 提交时刻（本地 ISO；仅遥测，不进 prompt——R1）
        public string CommandHash;            // 指令文本 SHA-256 前 12 位（不存原文，隐私）
        public int Rounds;
        public long TtftMs;                   // 首轮首字延迟（P1 流式实测；P0 为首轮总时长代理口径）
        /// <summary>P0 非流式：ttft_ms 是「首轮总时长」代理值（§13.5 降级口径，遥测显式标注）。</summary>
        public bool TtftProxy;
        public int[] RoundMs;
        public long TotalMs;
        public int PromptTokens;
        public int CompletionTokens;
        public int ReasoningTokens;
        public int CachedPromptTokens;
        public double PrefixHitRate;          // Σcached/Σprompt（本提交口径）
        public int TokenEff;                  // 本提交 T_eff
        public int Karma;
        public double Roll;
        public bool Surged;
        public int Writes;
        /// <summary>主导作用域（E3-C 校准审计；无写入为 null）。</summary>
        public string Scope;
        /// <summary>实际计入的作用域深度权重（无作用域 1.0）。</summary>
        public double ScopeWeight = 1.0;
        /// <summary>触及的全部成功写入作用域（按发生序去重）。</summary>
        public string[] Scopes;
        public string[] Tools;
        public string EffortProfile;
        public bool TimedOut;
        public bool CapWarned;
    }

    /// <summary>
    /// token 账本（E0-S1）：T_eff 累计 + 日桶 + 持久化 + requests 遥测环。
    ///
    /// 【兼容底线】持久化路径与字段与既有（已下线的旧编排器）实现完全一致
    /// （{BaseDir}/configs/token_stats.json，total_prompt_tokens/total_completion_tokens/
    /// total_calls/daily_stats/token_price_per_1k），仅在末尾追加 requests 数组；
    /// 旧文件（无 requests）无损加载。E1 迁移时由本类接管 <c>PersistTokenStats</c>。
    ///
    /// 【时钟注入】日桶与 ts 经 <c>Func&lt;DateTime&gt;</c> 取时间，测试可钉死；
    /// 【线程安全】Add/Begin/End 与读写统计均加锁（回调可能跨线程延续）。
    /// </summary>
    public sealed class TokenLedger
    {
        /// <summary>requests 环形缓冲容量（§13.7）。</summary>
        public const int RequestRingCapacity = 500;

        readonly Func<DateTime> _now;
        readonly object _lock = new object();
        readonly SortedDictionary<string, DailyBucket> _daily = new SortedDictionary<string, DailyBucket>(StringComparer.Ordinal);
        readonly List<AgentRequestRecord> _requests = new List<AgentRequestRecord>();
        PendingRequest _pending;

        long _totalPrompt;
        long _totalCompletion;
        long _totalReasoning;
        long _totalCachedPrompt;
        long _totalCalls;

        /// <summary>每千 token 单价（美元；既有口径 0.0015）。</summary>
        public double PricePer1K = 0.0015;

        public TokenLedger(Func<DateTime> now = null)
        {
            _now = now ?? (() => DateTime.Now);
        }

        // ── 累计（每轮 usage 恰好一次入账） ──

        /// <summary>T_eff = Σ(prompt + completion)（thinking 全额含在 completion）。</summary>
        public long TokenEff { get { lock (_lock) return _totalPrompt + _totalCompletion; } }

        public long TotalPromptTokens { get { lock (_lock) return _totalPrompt; } }
        public long TotalCompletionTokens { get { lock (_lock) return _totalCompletion; } }
        public long TotalCalls { get { lock (_lock) return _totalCalls; } }
        public long TotalReasoningTokens { get { lock (_lock) return _totalReasoning; } }
        public long TotalCachedPromptTokens { get { lock (_lock) return _totalCachedPrompt; } }

        /// <summary>日桶数量。</summary>
        public int DailyCount { get { lock (_lock) return _daily.Count; } }

        /// <summary>遥测记录视图（最旧在前；容量 500）。</summary>
        public IReadOnlyList<AgentRequestRecord> Requests { get { lock (_lock) return _requests; } }

        /// <summary>累计一轮用量；若有进行中的请求，同步累入该请求的分列。</summary>
        public void Add(LlmUsage usage)
        {
            if (usage == null) throw new ArgumentNullException(nameof(usage));
            if (usage.PromptTokens < 0)
                throw new ArgumentOutOfRangeException(nameof(usage), "prompt_tokens 不得为负");
            if (usage.CompletionTokens < 0)
                throw new ArgumentOutOfRangeException(nameof(usage), "completion_tokens 不得为负");
            if (usage.ReasoningTokens < 0)
                throw new ArgumentOutOfRangeException(nameof(usage), "reasoning_tokens 不得为负");
            if (usage.CachedPromptTokens < 0)
                throw new ArgumentOutOfRangeException(nameof(usage), "cached_prompt_tokens 不得为负");

            lock (_lock)
            {
                _totalPrompt += usage.PromptTokens;
                _totalCompletion += usage.CompletionTokens;
                _totalReasoning += usage.ReasoningTokens;
                _totalCachedPrompt += usage.CachedPromptTokens;
                _totalCalls++;

                var key = DateKey(_now());
                if (!_daily.TryGetValue(key, out var day))
                    _daily[key] = day = new DailyBucket();
                day.PromptTokens += usage.PromptTokens;
                day.CompletionTokens += usage.CompletionTokens;
                day.Calls++;

                if (_pending != null)
                {
                    // 同一时刻只允许一个进行中请求（BeginRequest 保证），累入其账本侧累加器。
                    _pending.Prompt += usage.PromptTokens;
                    _pending.Completion += usage.CompletionTokens;
                    _pending.Reasoning += usage.ReasoningTokens;
                    _pending.Cached += usage.CachedPromptTokens;
                }
            }
        }

        /// <summary>取某日桶快照（不存在则返回全零桶）。</summary>
        public DailyBucket GetDaily(int year, int month, int day)
        {
            var key = new DateTime(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (_lock)
            {
                if (_daily.TryGetValue(key, out var b))
                    return new DailyBucket
                    {
                        PromptTokens = b.PromptTokens,
                        CompletionTokens = b.CompletionTokens,
                        Calls = b.Calls,
                    };
                return new DailyBucket();
            }
        }

        // ── requests 遥测环 ──

        /// <summary>开始一次提交记录（ts/command_hash 落账；token 分列由后续 Add 累入账本侧累加器）。</summary>
        public AgentRequestRecord BeginRequest(string commandHash)
        {
            if (string.IsNullOrEmpty(commandHash))
                throw new ArgumentException("command_hash 不得为空（指令文本 SHA-256 前 12 位）", nameof(commandHash));
            lock (_lock)
            {
                if (_pending != null)
                    throw new InvalidOperationException("上一个提交尚未 EndRequest（不支持并发请求）");
                var r = new AgentRequestRecord
                {
                    Ts = _now().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    CommandHash = commandHash,
                };
                _pending = new PendingRequest { Record = r };
                return r;
            }
        }

        /// <summary>
        /// 结束一次提交：token 四件套与派生字段以账本侧累加器权威重写
        /// （t_eff = 本提交 Σ(prompt+completion)；prefix_hit_rate = cached/prompt）——
        /// 调用方预填一律被覆盖，防止错值污染校准回归；随后压入环形缓冲（超过 500 挤出最旧）。
        /// </summary>
        public void EndRequest(AgentRequestRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            lock (_lock)
            {
                if (_pending == null || !ReferenceEquals(_pending.Record, record))
                    throw new ArgumentException("该记录不是本账本 BeginRequest 产生的（或已 EndRequest）", nameof(record));

                record.PromptTokens = _pending.Prompt;
                record.CompletionTokens = _pending.Completion;
                record.ReasoningTokens = _pending.Reasoning;
                record.CachedPromptTokens = _pending.Cached;
                record.TokenEff = record.PromptTokens + record.CompletionTokens;
                record.PrefixHitRate = record.PromptTokens > 0
                    ? (double)record.CachedPromptTokens / record.PromptTokens
                    : 0.0;

                _pending = null;
                _requests.Add(record);
                if (_requests.Count > RequestRingCapacity)
                    _requests.RemoveAt(0);
            }
        }

        /// <summary>进行中请求的账本侧累加器（记录本体的 token 字段是输出，不是输入）。</summary>
        sealed class PendingRequest
        {
            public AgentRequestRecord Record;
            public int Prompt;
            public int Completion;
            public int Reasoning;
            public int Cached;
        }

        // ── 序列化 / 持久化 ──

        /// <summary>产出完整 token_stats.json 形状（既有五字段 + requests 数组）。</summary>
        public JObject ToJson()
        {
            lock (_lock)
            {
                var daily = new JObject();
                foreach (var kv in _daily) // SortedDictionary ⇒ 日期序确定（字节级稳定）
                    daily[kv.Key] = new JObject
                    {
                        ["prompt_tokens"] = kv.Value.PromptTokens,
                        ["completion_tokens"] = kv.Value.CompletionTokens,
                        ["calls"] = kv.Value.Calls,
                    };

                var requests = new JArray();
                foreach (var r in _requests)
                    requests.Add(RecordToJson(r));

                return new JObject
                {
                    ["total_prompt_tokens"] = _totalPrompt,
                    ["total_completion_tokens"] = _totalCompletion,
                    ["total_calls"] = _totalCalls,
                    ["daily_stats"] = daily,
                    ["token_price_per_1k"] = PricePer1K,
                    ["total_reasoning_tokens"] = _totalReasoning,
                    ["total_cached_prompt_tokens"] = _totalCachedPrompt,
                    ["requests"] = requests,
                };
            }
        }

        static JArray ScopesToJson(string[] scopes)
        {
            var array = new JArray();
            if (scopes != null)
                foreach (var s in scopes) array.Add(s);
            return array;
        }

        static JObject RecordToJson(AgentRequestRecord r)
        {
            var roundMs = new JArray();
            if (r.RoundMs != null)
                foreach (var ms in r.RoundMs) roundMs.Add(ms);
            var tools = new JArray();
            if (r.Tools != null)
                foreach (var t in r.Tools) tools.Add(t);

            return new JObject
            {
                ["ts"] = r.Ts,
                ["command_hash"] = r.CommandHash,
                ["rounds"] = r.Rounds,
                ["ttft_ms"] = r.TtftMs,
                ["ttft_proxy"] = r.TtftProxy,
                ["round_ms"] = roundMs,
                ["total_ms"] = r.TotalMs,
                ["prompt_tokens"] = r.PromptTokens,
                ["completion_tokens"] = r.CompletionTokens,
                ["reasoning_tokens"] = r.ReasoningTokens,
                ["cached_prompt_tokens"] = r.CachedPromptTokens,
                ["prefix_hit_rate"] = r.PrefixHitRate,
                ["t_eff"] = r.TokenEff,
                ["karma"] = r.Karma,
                ["roll"] = r.Roll,
                ["surged"] = r.Surged,
                ["writes"] = r.Writes,
                ["scope"] = r.Scope ?? string.Empty,
                ["scope_weight"] = r.ScopeWeight,
                ["scopes"] = ScopesToJson(r.Scopes),
                ["tools"] = tools,
                ["effort_profile"] = r.EffortProfile,
                ["timed_out"] = r.TimedOut,
                ["cap_warned"] = r.CapWarned,
            };
        }

        /// <summary>从 JSON 还原（缺字段按零值/默认值；损坏键值不抛）。</summary>
        public static TokenLedger FromJson(JObject json, Func<DateTime> now = null)
        {
            var ledger = new TokenLedger(now);
            if (json == null) return ledger;

            ledger._totalPrompt = ReadLong(json, "total_prompt_tokens");
            ledger._totalCompletion = ReadLong(json, "total_completion_tokens");
            ledger._totalCalls = ReadLong(json, "total_calls");
            ledger._totalReasoning = ReadLong(json, "total_reasoning_tokens");
            ledger._totalCachedPrompt = ReadLong(json, "total_cached_prompt_tokens");
            ledger.PricePer1K = ReadDouble(json, "token_price_per_1k", 0.0015);

            var daily = json["daily_stats"] as JObject;
            if (daily != null)
            {
                foreach (var kv in daily)
                {
                    var day = kv.Value as JObject;
                    if (day == null) continue;
                    ledger._daily[kv.Key] = new DailyBucket
                    {
                        PromptTokens = ReadLong(day, "prompt_tokens"),
                        CompletionTokens = ReadLong(day, "completion_tokens"),
                        Calls = ReadLong(day, "calls"),
                    };
                }
            }

            var requests = json["requests"] as JArray;
            if (requests != null)
            {
                foreach (var t in requests)
                {
                    var ro = t as JObject;
                    if (ro == null) continue;
                    var r = new AgentRequestRecord
                    {
                        Ts = ReadString(ro, "ts"),
                        CommandHash = ReadString(ro, "command_hash"),
                        Rounds = (int)ReadLong(ro, "rounds"),
                        TtftMs = ReadLong(ro, "ttft_ms"),
                        TtftProxy = ReadBool(ro, "ttft_proxy"),
                        TotalMs = ReadLong(ro, "total_ms"),
                        PromptTokens = (int)ReadLong(ro, "prompt_tokens"),
                        CompletionTokens = (int)ReadLong(ro, "completion_tokens"),
                        ReasoningTokens = (int)ReadLong(ro, "reasoning_tokens"),
                        CachedPromptTokens = (int)ReadLong(ro, "cached_prompt_tokens"),
                        PrefixHitRate = ReadDouble(ro, "prefix_hit_rate", 0.0),
                        TokenEff = (int)ReadLong(ro, "t_eff"),
                        Karma = (int)ReadLong(ro, "karma"),
                        Roll = ReadDouble(ro, "roll", 0.0),
                        Surged = ReadBool(ro, "surged"),
                        Writes = (int)ReadLong(ro, "writes"),
                        Scope = ReadString(ro, "scope"),
                        ScopeWeight = ReadDouble(ro, "scope_weight", 1.0),
                        EffortProfile = ReadString(ro, "effort_profile"),
                        TimedOut = ReadBool(ro, "timed_out"),
                        CapWarned = ReadBool(ro, "cap_warned"),
                    };
                    var roundMs = ro["round_ms"] as JArray;
                    if (roundMs != null)
                    {
                        r.RoundMs = new int[roundMs.Count];
                        for (int i = 0; i < roundMs.Count; i++)
                        {
                            var v = roundMs[i] as JValue;
                            r.RoundMs[i] = v == null ? 0 : (int)v.Value<long>();
                        }
                    }
                    var tools = ro["tools"] as JArray;
                    if (tools != null)
                    {
                        r.Tools = new string[tools.Count];
                        for (int i = 0; i < tools.Count; i++) r.Tools[i] = (string)(tools[i] as JValue)?.Value<string>();
                    }
                    var scopes = ro["scopes"] as JArray;
                    if (scopes != null)
                    {
                        r.Scopes = new string[scopes.Count];
                        for (int i = 0; i < scopes.Count; i++)
                            r.Scopes[i] = (string)(scopes[i] as JValue)?.Value<string>();
                    }
                    ledger._requests.Add(r);
                }
                // 环形容量截断（旧文件可能超出）。
                while (ledger._requests.Count > RequestRingCapacity)
                    ledger._requests.RemoveAt(0);
            }
            return ledger;
        }

        /// <summary>写盘（{baseDir}/configs/token_stats.json）。失败静默——统计不可断主流程（同旧实现）。</summary>
        public void Save(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            try
            {
                var path = Path.Combine(baseDir, "configs", "token_stats.json");
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, ToJson().ToString(Formatting.Indented));
            }
            catch
            {
                // 统计写入失败不影响主流程（继承旧 PersistTokenStats 语义）。
            }
        }

        /// <summary>读盘；缺文件/损坏/IO 错误一律返回空账本（遥测可丢，主流程不可断）。</summary>
        public static TokenLedger Load(string baseDir, Func<DateTime> now = null)
        {
            var ledger = new TokenLedger(now);
            if (string.IsNullOrEmpty(baseDir)) return ledger;
            try
            {
                var path = Path.Combine(baseDir, "configs", "token_stats.json");
                if (!File.Exists(path)) return ledger;
                var text = File.ReadAllText(path);
                // DateParseHandling.None：ts 等字段必须原样保留字符串（字节级往返的前提）。
                var json = JsonConvert.DeserializeObject<JObject>(text,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                var loaded = FromJson(json, now);
                return loaded;
            }
            catch
            {
                return new TokenLedger(now);
            }
        }

        // ── 展示统计（旧 GetTokenStats 形状，供 HUD 直换） ──

        public JObject GetStats()
        {
            lock (_lock)
            {
                var today = DateKey(_now());
                var day = _daily.TryGetValue(today, out var d) ? d : null;
                long todayPrompt = day?.PromptTokens ?? 0;
                long todayCompletion = day?.CompletionTokens ?? 0;
                long todayCalls = day?.Calls ?? 0;
                long totalTokens = _totalPrompt + _totalCompletion;
                long todayTokens = todayPrompt + todayCompletion;

                return new JObject
                {
                    ["total_prompt_tokens"] = _totalPrompt,
                    ["total_completion_tokens"] = _totalCompletion,
                    ["total_tokens"] = totalTokens,
                    ["total_calls"] = _totalCalls,
                    ["today_prompt_tokens"] = todayPrompt,
                    ["today_completion_tokens"] = todayCompletion,
                    ["today_tokens"] = todayTokens,
                    ["today_calls"] = todayCalls,
                    ["estimated_cost_usd"] = Math.Round(totalTokens * PricePer1K / 1000, 4),
                    ["today_cost_usd"] = Math.Round(todayTokens * PricePer1K / 1000, 4),
                    ["token_price_per_1k_usd"] = PricePer1K,
                };
            }
        }

        // ── 内部工具 ──

        static string DateKey(DateTime now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        static long ReadLong(JObject json, string key)
        {
            var t = json[key];
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return t.Value<long>(); }
            catch { return 0; }
        }

        static double ReadDouble(JObject json, string key, double fallback)
        {
            var t = json[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<double>(); }
            catch { return fallback; }
        }

        static bool ReadBool(JObject json, string key)
        {
            var t = json[key];
            if (t == null || t.Type == JTokenType.Null) return false;
            try { return t.Value<bool>(); }
            catch { return false; }
        }

        static string ReadString(JObject json, string key)
        {
            var t = json[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            // 防御 Newtonsoft 默认把 ISO 日期串解析成 JTokenType.Date（再取 string 会得到
            // 区域相关的 "10/23/2026 12:00:00"，破坏字节级往返）——统一回格式化为 ISO。
            if (t.Type == JTokenType.Date)
                return t.Value<DateTime>().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            try { return t.Value<string>(); }
            catch { return null; }
        }
    }
}
