using System;
using System.Collections.Generic;

namespace ChessSage.Core.Agent
{
    /// <summary>f 曲线锚点（token → 业力）。</summary>
    public sealed class KarmaAnchor
    {
        public int Token;
        public int Karma;
        public KarmaAnchor(int token, int karma) { Token = token; Karma = karma; }
    }

    /// <summary>
    /// 分段线性曲线参数（E0-S5 参数化落点；重拟合只改 json、不动代码）。
    /// 合同默认值 = 设计侧 §1.4 六档校准锚点表（用户裁决 #2 确认）。
    /// </summary>
    public sealed class KarmaCurveConfig
    {
        /// <summary>六档合同默认值：2500→1 / 8000→16 / 12000→32 / 15000→48 / 16000→55 / 21000→91。</summary>
        public static KarmaCurveConfig Default { get; } = new KarmaCurveConfig(
            new[]
            {
                new KarmaAnchor(2500, 1),
                new KarmaAnchor(8000, 16),
                new KarmaAnchor(12000, 32),
                new KarmaAnchor(15000, 48),
                new KarmaAnchor(16000, 55),
                new KarmaAnchor(21000, 91),
            },
            2500, true, 1, 120);

        public IReadOnlyList<KarmaAnchor> Anchors { get; }
        /// <summary>T &lt; FloorToken 为平地价区（f=1）。</summary>
        public int FloorToken { get; }
        /// <summary>末锚之后是否以末段斜率继续延伸（触顶 120≈25K 即由此而来）。</summary>
        public bool ExtendLastSlope { get; }
        /// <summary>业力下限（≥1：成功即有价）。Compute 的实际下界 = max(1, Min)。</summary>
        public int Min { get; }
        /// <summary>基准单次上限（技能加成由调用方体现在 KarmaContext.SingleMax）。</summary>
        public int Max { get; }

        public KarmaCurveConfig(IList<KarmaAnchor> anchors, int floorToken, bool extendLastSlope, int min, int max)
        {
            if (anchors == null) throw new ArgumentNullException(nameof(anchors));
            if (anchors.Count < 2)
                throw new ArgumentException("至少需要 2 个锚点（一段线段）才能定义分段线性曲线", nameof(anchors));
            var copy = new KarmaAnchor[anchors.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                var a = anchors[i];
                if (a == null) throw new ArgumentException($"锚点 {i} 为 null", nameof(anchors));
                if (a.Karma < 1)
                    throw new ArgumentException($"锚点 {i} 业力 {a.Karma} 必须 ≥ 1（下限 1 = 成功即有价）", nameof(anchors));
                if (a.Token < 0)
                    throw new ArgumentException($"锚点 {i} token {a.Token} 不得为负", nameof(anchors));
                if (i > 0 && a.Token <= copy[i - 1].Token)
                    throw new ArgumentException(
                        $"锚点 token 必须严格递增（#{i - 1}={copy[i - 1].Token} → #{i}={a.Token}）", nameof(anchors));
                copy[i] = a;
            }
            if (floorToken < 0)
                throw new ArgumentException($"floor_token {floorToken} 不得为负", nameof(floorToken));
            if (floorToken > copy[copy.Length - 1].Token)
                throw new ArgumentException(
                    $"floor_token {floorToken} 不得大于末锚点 token {copy[copy.Length - 1].Token}（平地价区必须落在曲线定义域内）",
                    nameof(floorToken));
            if (min < 1)
                throw new ArgumentException($"min {min} 必须 ≥ 1", nameof(min));
            if (max < min)
                throw new ArgumentException($"max {max} 不得小于 min {min}", nameof(max));

            Anchors = copy;
            FloorToken = floorToken;
            ExtendLastSlope = extendLastSlope;
            Min = min;
            Max = max;
        }
    }

    /// <summary>
    /// 作用域深度权重（E3-C 校准补丁）：<c>karma = clamp(round(f(T) × σ × surge × discount × scopeWeight) + flat)</c>。
    ///
    /// 【为什么要它】真实链路 167 次实测显示：D/B/A/C 四档的 T_eff 都被结构性下限
    /// （3 轮 × ≈2.4K）压到 7.2–7.9K，纯 f(T) 在数学上无法还原四档旧价差。
    /// 但「补丁落到哪个配置作用域」是机械可观测的事实（沙箱写入的 target/JSON Pointer），
    /// 于是把「改动深度」作为独立乘数，乘在 discount 之后、flat 之前。
    ///
    /// 【语义边界】
    ///   · 权重只对**成功且真实产生变更**的写入生效（writes == 0 恒 1，不吃作用域）；
    ///   · 同一次提交取触及作用域中的**最大**权重（不做加法，避免叠加失控）；
    ///   · 未配置作用域按 1.0（中性）处理——新作用域上线不该静默涨价。
    ///
    /// 【参数来源】Assets/StreamingAssets/Agent/agent_remake.json 的 scope_weights 块；
    /// 本类型只是 json → 公式的适配层，代码零硬编码（校准只改 json）。
    /// </summary>
    public sealed class ScopeWeightsConfig
    {
        public const string UiConfig = "ui_config";
        public const string BoardState = "board_state";
        public const string Pieces = "pieces";
        public const string Rules = "rules";
        public const string Objectives = "objectives";

        /// <summary>合法作用域与固定顺序（配置缺键取默认；未知键整段回退）。</summary>
        public static readonly string[] ScopeOrder =
        {
            UiConfig, BoardState, Pieces, Rules, Objectives,
        };

        /// <summary>中性权重：无作用域/未知作用域均按此计价（不改变既有黄金值）。</summary>
        public const double NeutralWeight = 1.0;

        /// <summary>
        /// 合同默认值（E3-C 实测反推：E 无写入 / D=ui 1.0 / B=board_state 2.9 /
        /// A·C=rules 3.35 / C+=pieces 1.85；objectives 与 rules 同深度）。
        /// </summary>
        public static ScopeWeightsConfig Default { get; } = new ScopeWeightsConfig(
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                { UiConfig, 1.0 },
                { BoardState, 2.9 },
                { Pieces, 1.85 },
                { Rules, 3.35 },
                { Objectives, 3.35 },
            });

        readonly Dictionary<string, double> _weights;

        public ScopeWeightsConfig(IReadOnlyDictionary<string, double> weights)
        {
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            var copy = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var scope in ScopeOrder)
            {
                double value;
                if (!weights.TryGetValue(scope, out value))
                    throw new ArgumentException($"作用域 {scope} 缺少权重（合法作用域：{string.Join("/", ScopeOrder)}）", nameof(weights));
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new ArgumentException($"作用域 {scope} 的权重 {value} 不是有限数", nameof(weights));
                if (value <= 0.0)
                    throw new ArgumentException($"作用域 {scope} 的权重 {value} 必须 > 0（0 会让写入免费）", nameof(weights));
                if (value > 100.0)
                    throw new ArgumentException($"作用域 {scope} 的权重 {value} 超过上限 100（防误配把单次业力瞬间顶格）", nameof(weights));
                copy[scope] = value;
            }
            _weights = copy;
        }

        /// <summary>查权重；未知作用域返回中性 1.0（新作用域不静默涨价）。</summary>
        public double Get(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return NeutralWeight;
            double value;
            return _weights.TryGetValue(scope, out value) ? value : NeutralWeight;
        }

        /// <summary>
        /// 取一组作用域中的最大权重（并列时取**先出现**者，保证回放确定性）。
        /// 空集/全 null ⇒ (1.0, null)。
        /// </summary>
        public double Max(IEnumerable<string> scopes, out string dominant)
        {
            dominant = null;
            double best = NeutralWeight;
            if (scopes == null) return best;
            foreach (var scope in scopes)
            {
                if (string.IsNullOrEmpty(scope)) continue;
                var value = Get(scope);
                if (dominant == null || value > best)
                {
                    best = value;
                    dominant = scope;
                }
            }
            return dominant == null ? NeutralWeight : best;
        }

        /// <summary>已配置作用域快照（调试/探针用）。</summary>
        public IReadOnlyDictionary<string, double> Weights => _weights;
    }

    /// <summary>
    /// 混合制波动参数（设计 §1.6）：均匀 σ 保底 + T≥阈值时概率激增。
    /// 合同默认值：U(0.8,1.3)；T≥20K · p=0.30 · ×U(1.25,1.5)。
    /// </summary>
    public sealed class FluctuationConfig
    {
        public static FluctuationConfig Default { get; } = new FluctuationConfig(0.8, 1.3, 20000, 0.30, 1.25, 1.5);

        public double UniformMin { get; }
        public double UniformMax { get; }
        public int SurgeTokenThreshold { get; }
        public double SurgeProbability { get; }
        public double SurgeMin { get; }
        public double SurgeMax { get; }

        public FluctuationConfig(double uniformMin, double uniformMax, int surgeTokenThreshold,
            double surgeProbability, double surgeMin, double surgeMax)
        {
            if (uniformMin < 0.0)
                throw new ArgumentException($"uniform 下界 {uniformMin} 不得为负", nameof(uniformMin));
            if (uniformMax < uniformMin)
                throw new ArgumentException($"uniform 区间倒挂（{uniformMin} > {uniformMax}）", nameof(uniformMax));
            if (surgeTokenThreshold < 0)
                throw new ArgumentException($"surge.token_threshold {surgeTokenThreshold} 不得为负", nameof(surgeTokenThreshold));
            if (surgeProbability < 0.0 || surgeProbability > 1.0)
                throw new ArgumentException($"surge.probability {surgeProbability} 必须落在 [0,1]", nameof(surgeProbability));
            if (surgeMin < 0.0)
                throw new ArgumentException($"surge 因子下界 {surgeMin} 不得为负", nameof(surgeMin));
            if (surgeMax < surgeMin)
                throw new ArgumentException($"surge 区间倒挂（{surgeMin} > {surgeMax}）", nameof(surgeMax));

            UniformMin = uniformMin;
            UniformMax = uniformMax;
            SurgeTokenThreshold = surgeTokenThreshold;
            SurgeProbability = surgeProbability;
            SurgeMin = surgeMin;
            SurgeMax = surgeMax;
        }
    }
}
