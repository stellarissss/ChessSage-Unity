using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 限额与预算（工程侧 §13.1；由 20s 延迟预算反推的生产值）。
    /// 与 <see cref="AgentLimits"/>（单次调用硬限额谓词）互补：那是静态谓词，这是可调参数。
    /// </summary>
    public sealed class AgentLimitsConfig
    {
        /// <summary>合同默认值（裁决表 2026-10-23）：4 轮 / 重复 3 / 错误 3 / 60K / 24K / 20s。</summary>
        public static AgentLimitsConfig Default { get; } = new AgentLimitsConfig(4, 3, 3, 60000, 24000, 20);

        public int MaxRounds { get; }
        public int RepeatToolCap { get; }
        public int MaxToolErrors { get; }
        public int TokenHardStop { get; }
        public int ContextSoftCap { get; }
        public int HardTimeoutS { get; }

        public AgentLimitsConfig(int maxRounds, int repeatToolCap, int maxToolErrors,
            int tokenHardStop, int contextSoftCap, int hardTimeoutS)
        {
            if (maxRounds < 1)
                throw new ArgumentException($"max_rounds {maxRounds} 必须 ≥ 1（至少一轮）", nameof(maxRounds));
            if (repeatToolCap < 1)
                throw new ArgumentException($"repeat_tool_cap {repeatToolCap} 必须 ≥ 1", nameof(repeatToolCap));
            if (maxToolErrors < 0)
                throw new ArgumentException($"max_tool_errors {maxToolErrors} 不得为负", nameof(maxToolErrors));
            if (tokenHardStop < 0)
                throw new ArgumentException($"token_hard_stop {tokenHardStop} 不得为负", nameof(tokenHardStop));
            if (contextSoftCap < 0)
                throw new ArgumentException($"context_soft_cap {contextSoftCap} 不得为负", nameof(contextSoftCap));
            if (hardTimeoutS < 1)
                throw new ArgumentException(
                    $"hard_timeout_s {hardTimeoutS} 必须 ≥ 1（裁决 #4：20s 是玩家体验底线，不得上调）", nameof(hardTimeoutS));

            MaxRounds = maxRounds;
            RepeatToolCap = repeatToolCap;
            MaxToolErrors = maxToolErrors;
            TokenHardStop = tokenHardStop;
            ContextSoftCap = contextSoftCap;
            HardTimeoutS = hardTimeoutS;
        }
    }

    /// <summary>
    /// 生产速度配置（裁决 #4 联动；校准前提——先定型性能配置再校准锚点）。
    /// 三个字符串字段走「已知集 + 大小写归一」：拼错/未知值会抛错并由解析层整段回退，
    /// 防止静默降级成一个谁都不知道是什么的生产配置。
    /// </summary>
    public sealed class AgentPerformanceConfig
    {
        public const string EffortM1 = "M1";
        public const string EffortM2 = "M2";
        /// <summary>M3（E3-S2 实验矩阵）：工具轮 none（关闭 thinking）/ 终轮 high。</summary>
        public const string EffortM3 = "M3";
        public const string PrefixMaximize = "maximize";
        public const string TimeoutActionA = "A";

        static readonly string[] KnownEffortProfiles = { EffortM1, EffortM2, EffortM3 };
        static readonly string[] KnownPrefixCache = { PrefixMaximize };
        static readonly string[] KnownTimeoutActions = { TimeoutActionA };

        /// <summary>合同默认值：M2（工具轮 low + 终轮 high）/ maximize / A / 2048 / 4096。</summary>
        public static AgentPerformanceConfig Default { get; } =
            new AgentPerformanceConfig(EffortM2, PrefixMaximize, TimeoutActionA, 2048, 4096);

        public string EffortProfile { get; }
        public string PrefixCache { get; }
        public string TimeoutAction { get; }
        public int ToolRoundMaxTokens { get; }
        public int FinalRoundMaxTokens { get; }

        public AgentPerformanceConfig(string effortProfile, string prefixCache, string timeoutAction,
            int toolRoundMaxTokens, int finalRoundMaxTokens)
        {
            EffortProfile = Canonical(effortProfile, KnownEffortProfiles, nameof(effortProfile));
            PrefixCache = Canonical(prefixCache, KnownPrefixCache, nameof(prefixCache));
            TimeoutAction = Canonical(timeoutAction, KnownTimeoutActions, nameof(timeoutAction));
            if (toolRoundMaxTokens < 1)
                throw new ArgumentException($"per_round_max_tokens.tool {toolRoundMaxTokens} 必须 ≥ 1", nameof(toolRoundMaxTokens));
            if (finalRoundMaxTokens < 1)
                throw new ArgumentException($"per_round_max_tokens.final {finalRoundMaxTokens} 必须 ≥ 1", nameof(finalRoundMaxTokens));
            ToolRoundMaxTokens = toolRoundMaxTokens;
            FinalRoundMaxTokens = finalRoundMaxTokens;
        }

        /// <summary>
        /// 按档位解析某一轮的 reasoning_effort（§13.3 实验矩阵）。
        /// M1 = 全程 low；M2 = 工具轮 low + 终轮 high；M3 = 工具轮 none + 终轮 high。
        /// 返回 null 表示不发送该字段（服务端默认 = thinking 开）。
        /// </summary>
        public static string EffortForRound(string profile, int round, int maxRounds)
        {
            if (round < 1) round = 1;
            if (maxRounds < 1) maxRounds = 1;
            var isFinalRound = round >= maxRounds;
            switch (profile)
            {
                case EffortM1:
                    return "low";
                case EffortM3:
                    return isFinalRound ? "high" : "none";
                case EffortM2:
                default:
                    return isFinalRound ? "high" : "low";
            }
        }

        static string Canonical(string value, string[] known, string paramName)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"{paramName} 不得为空", paramName);
            var trimmed = value.Trim();
            foreach (var k in known)
                if (string.Equals(trimmed, k, StringComparison.OrdinalIgnoreCase))
                    return k;
            throw new ArgumentException(
                $"{paramName} \"{value}\" 不在已知集（{string.Join(" / ", known)}）", paramName);
        }
    }

    /// <summary>
    /// 道级业力平坦加数表（设计侧 D-7）：realm → condition → flat。
    /// 查询大小写不敏感（配置侧手写 "Animal" 与运行时 "animal" 等价）；
    /// flat 为非负整数——它是加项，负值属于隐藏折扣，一律拒绝。
    /// </summary>
    public sealed class RealmKarmaFlat
    {
        /// <summary>合同默认值：畜生道「改高等级棋子 +20」。</summary>
        public static RealmKarmaFlat Default { get; } = new RealmKarmaFlat(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "animal",
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "high_rank_piece", 20 },
                    }
                },
            });

        readonly Dictionary<string, Dictionary<string, int>> _table;

        public RealmKarmaFlat(Dictionary<string, Dictionary<string, int>> table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            var copy = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in table)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    throw new ArgumentException("道境键不得为空", nameof(table));
                if (kv.Value == null || kv.Value.Count == 0)
                    throw new ArgumentException($"道境 {kv.Key} 的条件表不得为空", nameof(table));
                var inner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in kv.Value)
                {
                    if (string.IsNullOrEmpty(pair.Key))
                        throw new ArgumentException($"道境 {kv.Key} 的条件键不得为空", nameof(table));
                    if (pair.Value < 0)
                        throw new ArgumentException(
                            $"道境 {kv.Key}.{pair.Key} 的 flat {pair.Value} 不得为负（平坦加数为加项）", nameof(table));
                    inner[pair.Key] = pair.Value;
                }
                copy[kv.Key] = inner;
            }
            _table = copy;
        }

        /// <summary>查表；未配置的道境/条件返回 0。</summary>
        public int Get(string realm, string condition)
        {
            if (realm == null || condition == null) return 0;
            Dictionary<string, int> inner;
            if (!_table.TryGetValue(realm, out inner)) return 0;
            int value;
            return inner.TryGetValue(condition, out value) ? value : 0;
        }

        /// <summary>已配置的道境键（大小写原样，测试/调试用）。</summary>
        public IReadOnlyCollection<string> Realms => _table.Keys;
    }

    /// <summary>
    /// agent_remake.json 全量参数（E0-S5；设计侧 §6.1 / 工程侧 §7.2）。
    ///
    /// 【参数化契约】校准重拟合只改 json、不动代码——本类型是 json 与
    /// <see cref="KarmaFormula"/> 之间的唯一适配层。
    /// 【兜底语义】段级原子回退：任一段出现非法值（类型错/越界/校验失败），
    /// 该段整段回退对应 Default 并记入 <see cref="Warnings"/>；其余段不受影响。
    /// 缺键/JSON null 视为合法缺省（静默取合同值，不告警）。
    /// </summary>
    public sealed class AgentRemakeConfig
    {
        /// <summary><see cref="Source"/> 取值：未加载任何文件。</summary>
        public const string BuiltinSource = "builtin";

        const int DefaultChatCap = 5;

        /// <summary>内置合同配置（缺文件/全损坏时的最终兜底）。</summary>
        public static AgentRemakeConfig Default { get; } = new AgentRemakeConfig(
            KarmaCurveConfig.Default, FluctuationConfig.Default, DefaultChatCap,
            AgentLimitsConfig.Default, AgentPerformanceConfig.Default, RealmKarmaFlat.Default,
            ScopeWeightsConfig.Default, new List<string>(), BuiltinSource);

        public KarmaCurveConfig KarmaCurve { get; }
        public FluctuationConfig Fluctuation { get; }
        /// <summary>作用域深度权重（E3-C：补丁落点的机械属性 → 业力乘数）。</summary>
        public ScopeWeightsConfig ScopeWeights { get; }
        public int ChatCap { get; }
        public AgentLimitsConfig Limits { get; }
        public AgentPerformanceConfig Performance { get; }
        public RealmKarmaFlat RealmFlat { get; }
        /// <summary>加载/解析过程中的回退告警（遥测与校准审计用；健康配置为空）。</summary>
        public IReadOnlyList<string> Warnings => _warnings;
        /// <summary>来源标识：文件路径 / "json"（内存）/ "builtin"（内置默认）。</summary>
        public string Source { get; }

        readonly List<string> _warnings;

        AgentRemakeConfig(KarmaCurveConfig curve, FluctuationConfig fluct, int chatCap,
            AgentLimitsConfig limits, AgentPerformanceConfig performance, RealmKarmaFlat realmFlat,
            ScopeWeightsConfig scopeWeights, List<string> warnings, string source)
        {
            KarmaCurve = curve;
            Fluctuation = fluct;
            ChatCap = chatCap;
            Limits = limits;
            Performance = performance;
            RealmFlat = realmFlat;
            ScopeWeights = scopeWeights;
            _warnings = warnings;
            Source = source;
        }

        /// <summary>
        /// 从 JSON 对象构建（逐键默认 + 段级原子回退）。
        /// </summary>
        public static AgentRemakeConfig FromJson(JObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var warnings = new List<string>();
            var curve = ParseKarmaCurve(root["karma_curve"], warnings);
            var fluct = ParseFluctuation(root["fluctuation"], warnings);
            int chatCap = ParseChatCap(root["chat_cap"], warnings);
            var limits = ParseLimits(root["limits"], warnings);
            var performance = ParsePerformance(root["performance"], warnings);
            var realmFlat = ParseRealmFlat(root["realm_karma_flat"], warnings);
            var scopeWeights = ParseScopeWeights(root["scope_weights"], warnings);
            return new AgentRemakeConfig(curve, fluct, chatCap, limits, performance, realmFlat,
                scopeWeights, warnings, "json");
        }

        /// <summary>替换来源标识（告警保留）。</summary>
        public AgentRemakeConfig WithSource(string source)
            => new AgentRemakeConfig(KarmaCurve, Fluctuation, ChatCap, Limits, Performance, RealmFlat,
                ScopeWeights, _warnings, source);

        /// <summary>追加加载器层告警（候选不可读/解析失败等）。</summary>
        public AgentRemakeConfig WithExtraWarnings(IEnumerable<string> extra)
        {
            if (extra == null) return this;
            var merged = new List<string>(_warnings);
            merged.AddRange(extra);
            return new AgentRemakeConfig(KarmaCurve, Fluctuation, ChatCap, Limits, Performance, RealmFlat,
                ScopeWeights, merged, Source);
        }

        /// <summary>道级平坦加数查询（未配置返回 0）。</summary>
        public int GetRealmFlat(string realm, string condition) => RealmFlat.Get(realm, condition);

        /// <summary>按本配置构造公式（校准改 json 后此处自动生效，代码零改）。</summary>
        public KarmaFormula CreateFormula()
            => new KarmaFormula(KarmaCurve, Fluctuation, ChatCap, ScopeWeights);

        // ───────────────────────────────────────────────
        // 分段解析（每段独立：缺键默认、非法整段回退 + 告警）
        // ───────────────────────────────────────────────

        static KarmaCurveConfig ParseKarmaCurve(JToken section, List<string> warnings)
        {
            if (IsAbsent(section)) return KarmaCurveConfig.Default;
            var obj = section as JObject;
            if (obj == null)
            {
                Warn(warnings, "karma_curve", "段不是 JSON 对象", "已整段回退默认六档锚点");
                return KarmaCurveConfig.Default;
            }

            var d = KarmaCurveConfig.Default;
            var errors = new List<string>();
            var anchors = new List<KarmaAnchor>(d.Anchors);

            var anchorsToken = obj["anchors"];
            if (!IsAbsent(anchorsToken))
            {
                var arr = anchorsToken as JArray;
                if (arr == null)
                {
                    errors.Add("anchors 必须是 [[token,karma],...] 数组");
                }
                else
                {
                    var parsed = new List<KarmaAnchor>();
                    bool ok = true;
                    foreach (var item in arr.Children())
                    {
                        var pair = item as JArray;
                        if (pair == null || pair.Count != 2)
                        {
                            ok = false;
                            errors.Add("anchors 每个元素必须是 [token, karma] 二元组");
                            break;
                        }
                        int token, karma;
                        if (!TryReadInt(pair[0], out token))
                        {
                            ok = false;
                            errors.Add($"anchors token \"{pair[0]}\" 不是整数");
                            break;
                        }
                        if (!TryReadInt(pair[1], out karma))
                        {
                            ok = false;
                            errors.Add($"anchors karma \"{pair[1]}\" 不是整数");
                            break;
                        }
                        parsed.Add(new KarmaAnchor(token, karma));
                    }
                    if (ok && parsed.Count < 2)
                    {
                        ok = false;
                        errors.Add("anchors 至少需要 2 个锚点");
                    }
                    if (ok) anchors = parsed;
                }
            }

            int floorToken = ReadInt(obj, "floor_token", d.FloorToken, errors);
            bool extendLastSlope = ReadBool(obj, "extend_last_slope", d.ExtendLastSlope, errors);
            int min = ReadInt(obj, "min", d.Min, errors);
            int max = ReadInt(obj, "max", d.Max, errors);

            if (errors.Count > 0)
            {
                Warn(warnings, "karma_curve", string.Join("；", errors.ToArray()), "已整段回退默认六档锚点");
                return KarmaCurveConfig.Default;
            }
            try
            {
                return new KarmaCurveConfig(anchors, floorToken, extendLastSlope, min, max);
            }
            catch (Exception ex)
            {
                Warn(warnings, "karma_curve", ex.Message, "已整段回退默认六档锚点");
                return KarmaCurveConfig.Default;
            }
        }

        static FluctuationConfig ParseFluctuation(JToken section, List<string> warnings)
        {
            if (IsAbsent(section)) return FluctuationConfig.Default;
            var obj = section as JObject;
            if (obj == null)
            {
                Warn(warnings, "fluctuation", "段不是 JSON 对象", "已整段回退默认波动参数");
                return FluctuationConfig.Default;
            }

            var d = FluctuationConfig.Default;
            var errors = new List<string>();
            double uniformMin = d.UniformMin, uniformMax = d.UniformMax;
            int surgeTokenThreshold = d.SurgeTokenThreshold;
            double surgeProbability = d.SurgeProbability, surgeMin = d.SurgeMin, surgeMax = d.SurgeMax;

            var uniformToken = obj["uniform"];
            if (!IsAbsent(uniformToken))
            {
                var arr = uniformToken as JArray;
                if (arr == null || arr.Count != 2
                    || !TryReadDouble(arr[0], out uniformMin) || !TryReadDouble(arr[1], out uniformMax))
                    errors.Add("uniform 必须是 [min,max] 两个数");
            }

            var surgeToken = obj["surge"];
            if (!IsAbsent(surgeToken))
            {
                var so = surgeToken as JObject;
                if (so == null)
                {
                    errors.Add("surge 必须是对象");
                }
                else
                {
                    surgeTokenThreshold = ReadInt(so, "token_threshold", d.SurgeTokenThreshold, errors);
                    surgeProbability = ReadDouble(so, "probability", d.SurgeProbability, errors);
                    var rangeToken = so["range"];
                    if (!IsAbsent(rangeToken))
                    {
                        var arr = rangeToken as JArray;
                        if (arr == null || arr.Count != 2
                            || !TryReadDouble(arr[0], out surgeMin) || !TryReadDouble(arr[1], out surgeMax))
                            errors.Add("surge.range 必须是 [min,max] 两个数");
                    }
                }
            }

            if (errors.Count > 0)
            {
                Warn(warnings, "fluctuation", string.Join("；", errors.ToArray()), "已整段回退默认波动参数");
                return FluctuationConfig.Default;
            }
            try
            {
                return new FluctuationConfig(uniformMin, uniformMax, surgeTokenThreshold,
                    surgeProbability, surgeMin, surgeMax);
            }
            catch (Exception ex)
            {
                Warn(warnings, "fluctuation", ex.Message, "已整段回退默认波动参数");
                return FluctuationConfig.Default;
            }
        }

        static int ParseChatCap(JToken section, List<string> warnings)
        {
            if (IsAbsent(section)) return DefaultChatCap;
            int value;
            if (!TryReadInt(section, out value) || value < 0)
            {
                Warn(warnings, "chat_cap", $"值 \"{section}\" 必须是非负整数", $"已回退默认 {DefaultChatCap}");
                return DefaultChatCap;
            }
            return value;
        }

        static AgentLimitsConfig ParseLimits(JToken section, List<string> warnings)
        {
            if (IsAbsent(section)) return AgentLimitsConfig.Default;
            var obj = section as JObject;
            if (obj == null)
            {
                Warn(warnings, "limits", "段不是 JSON 对象", "已整段回退默认限额");
                return AgentLimitsConfig.Default;
            }

            var d = AgentLimitsConfig.Default;
            var errors = new List<string>();
            int maxRounds = ReadInt(obj, "max_rounds", d.MaxRounds, errors);
            int repeatToolCap = ReadInt(obj, "repeat_tool_cap", d.RepeatToolCap, errors);
            int maxToolErrors = ReadInt(obj, "max_tool_errors", d.MaxToolErrors, errors);
            int tokenHardStop = ReadInt(obj, "token_hard_stop", d.TokenHardStop, errors);
            int contextSoftCap = ReadInt(obj, "context_soft_cap", d.ContextSoftCap, errors);
            int hardTimeoutS = ReadInt(obj, "hard_timeout_s", d.HardTimeoutS, errors);

            if (errors.Count > 0)
            {
                Warn(warnings, "limits", string.Join("；", errors.ToArray()), "已整段回退默认限额");
                return AgentLimitsConfig.Default;
            }
            try
            {
                return new AgentLimitsConfig(maxRounds, repeatToolCap, maxToolErrors,
                    tokenHardStop, contextSoftCap, hardTimeoutS);
            }
            catch (Exception ex)
            {
                Warn(warnings, "limits", ex.Message, "已整段回退默认限额");
                return AgentLimitsConfig.Default;
            }
        }

        static AgentPerformanceConfig ParsePerformance(JToken section, List<string> warnings)
        {
            if (IsAbsent(section)) return AgentPerformanceConfig.Default;
            var obj = section as JObject;
            if (obj == null)
            {
                Warn(warnings, "performance", "段不是 JSON 对象", "已整段回退默认性能配置");
                return AgentPerformanceConfig.Default;
            }

            var d = AgentPerformanceConfig.Default;
            var errors = new List<string>();
            string effortProfile = ReadString(obj, "effort_profile", d.EffortProfile, errors);
            string prefixCache = ReadString(obj, "prefix_cache", d.PrefixCache, errors);
            string timeoutAction = ReadString(obj, "timeout_action", d.TimeoutAction, errors);
            int toolTokens = d.ToolRoundMaxTokens, finalTokens = d.FinalRoundMaxTokens;

            var perRoundToken = obj["per_round_max_tokens"];
            if (!IsAbsent(perRoundToken))
            {
                var po = perRoundToken as JObject;
                if (po == null)
                {
                    errors.Add("per_round_max_tokens 必须是对象");
                }
                else
                {
                    toolTokens = ReadInt(po, "tool", d.ToolRoundMaxTokens, errors);
                    finalTokens = ReadInt(po, "final", d.FinalRoundMaxTokens, errors);
                }
            }

            if (errors.Count > 0)
            {
                Warn(warnings, "performance", string.Join("；", errors.ToArray()), "已整段回退默认性能配置");
                return AgentPerformanceConfig.Default;
            }
            try
            {
                return new AgentPerformanceConfig(effortProfile, prefixCache, timeoutAction,
                    toolTokens, finalTokens);
            }
            catch (Exception ex)
            {
                Warn(warnings, "performance", ex.Message, "已整段回退默认性能配置");
                return AgentPerformanceConfig.Default;
            }
        }

        static RealmKarmaFlat ParseRealmFlat(JToken section, List<string> warnings)
        {
            if (IsAbsent(section)) return RealmKarmaFlat.Default;
            var obj = section as JObject;
            if (obj == null)
            {
                Warn(warnings, "realm_karma_flat", "段不是 JSON 对象", "已整段回退默认道级加数");
                return RealmKarmaFlat.Default;
            }

            var table = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var kv in obj)
            {
                var inner = kv.Value as JObject;
                if (inner == null)
                {
                    errors.Add($"道境 {kv.Key} 的值不是对象");
                    break;
                }
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in inner)
                {
                    int value;
                    if (!TryReadInt(pair.Value, out value))
                    {
                        errors.Add($"{kv.Key}.{pair.Key} 的值 \"{pair.Value}\" 不是整数");
                        break;
                    }
                    map[pair.Key] = value;
                }
                if (errors.Count > 0) break;
                if (map.Count == 0)
                {
                    errors.Add($"道境 {kv.Key} 的条件表为空");
                    break;
                }
                table[kv.Key] = map;
            }

            if (errors.Count == 0 && table.Count == 0)
                errors.Add("配置为空对象");

            if (errors.Count > 0)
            {
                Warn(warnings, "realm_karma_flat", string.Join("；", errors.ToArray()), "已整段回退默认道级加数");
                return RealmKarmaFlat.Default;
            }
            try
            {
                return new RealmKarmaFlat(table);
            }
            catch (Exception ex)
            {
                Warn(warnings, "realm_karma_flat", ex.Message, "已整段回退默认道级加数");
                return RealmKarmaFlat.Default;
            }
        }

        /// <summary>
        /// 作用域深度权重段（E3-C）：缺键取默认、未知作用域/非法数值整段回退。
        /// 严格拒绝未知键——权重表是校准合同，拼错一个名字就等于静默改价。
        /// </summary>
        static ScopeWeightsConfig ParseScopeWeights(JToken section, List<string> warnings)
        {
            if (IsAbsent(section)) return ScopeWeightsConfig.Default;
            var obj = section as JObject;
            if (obj == null)
            {
                Warn(warnings, "scope_weights", "段不是 JSON 对象", "已整段回退默认作用域权重");
                return ScopeWeightsConfig.Default;
            }

            var defaults = ScopeWeightsConfig.Default;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var scope in ScopeWeightsConfig.ScopeOrder)
                values[scope] = defaults.Get(scope);

            var errors = new List<string>();
            foreach (var kv in obj)
            {
                if (Array.IndexOf(ScopeWeightsConfig.ScopeOrder, kv.Key) < 0)
                {
                    errors.Add($"未知作用域 \"{kv.Key}\"（合法：{string.Join("/", ScopeWeightsConfig.ScopeOrder)}）");
                    continue;
                }
                double value;
                if (!TryReadDouble(kv.Value, out value))
                {
                    errors.Add($"{kv.Key} 的值 \"{kv.Value}\" 不是数");
                    continue;
                }
                values[kv.Key] = value;
            }

            if (errors.Count > 0)
            {
                Warn(warnings, "scope_weights", string.Join("；", errors.ToArray()), "已整段回退默认作用域权重");
                return ScopeWeightsConfig.Default;
            }
            try
            {
                return new ScopeWeightsConfig(values);
            }
            catch (Exception ex)
            {
                Warn(warnings, "scope_weights", ex.Message, "已整段回退默认作用域权重");
                return ScopeWeightsConfig.Default;
            }
        }

        // ───────────────────────────────────────────────
        // JSON 读取助手（严格类型：int 只收 Integer，double 收 Integer/Float）
        // ───────────────────────────────────────────────
        static bool IsAbsent(JToken token)
            => token == null || token.Type == JTokenType.None || token.Type == JTokenType.Null;

        static bool TryReadInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer) return false;
            long raw = (long)token;
            if (raw < int.MinValue || raw > int.MaxValue) return false;
            value = (int)raw;
            return true;
        }

        static bool TryReadDouble(JToken token, out double value)
        {
            value = 0.0;
            if (token == null) return false;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            value = (double)token;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        static int ReadInt(JObject obj, string key, int fallback, List<string> errors)
        {
            var token = obj[key];
            if (IsAbsent(token)) return fallback;
            int value;
            if (!TryReadInt(token, out value))
            {
                errors.Add($"{key} \"{token}\" 不是整数");
                return fallback;
            }
            return value;
        }

        static double ReadDouble(JObject obj, string key, double fallback, List<string> errors)
        {
            var token = obj[key];
            if (IsAbsent(token)) return fallback;
            double value;
            if (!TryReadDouble(token, out value))
            {
                errors.Add($"{key} \"{token}\" 不是数");
                return fallback;
            }
            return value;
        }

        static bool ReadBool(JObject obj, string key, bool fallback, List<string> errors)
        {
            var token = obj[key];
            if (IsAbsent(token)) return fallback;
            if (token.Type == JTokenType.Boolean) return (bool)token;
            errors.Add($"{key} \"{token}\" 不是布尔值");
            return fallback;
        }

        static string ReadString(JObject obj, string key, string fallback, List<string> errors)
        {
            var token = obj[key];
            if (IsAbsent(token)) return fallback;
            if (token.Type == JTokenType.String) return (string)token;
            errors.Add($"{key} \"{token}\" 不是字符串");
            return fallback;
        }

        static void Warn(List<string> warnings, string section, string reason, string action)
            => warnings.Add($"配置段 {section}：{reason}，{action}");
    }
}
