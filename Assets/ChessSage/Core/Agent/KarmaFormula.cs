using System;
using System.Collections.Generic;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 业力结算分解（{karma, roll, surged, breakdown}，设计侧 §6.1 接口草案）。
    /// 供 UI 计数器、遥测（§13.7 requests 数组）与 QA 回放使用。
    /// </summary>
    public sealed class KarmaResult
    {
        /// <summary>业力终值（已 clamp）。</summary>
        public int Karma;
        /// <summary>f(T_eff) 曲线值（零写入/失败路径也如实上报，遥测用）。</summary>
        public double CurveValue;
        /// <summary>σ 分量（零写入/失败路径为 0——该路径不抽签）。</summary>
        public double Sigma;
        /// <summary>激增分量（未触发为 1.0；零写入/失败路径为 0）。</summary>
        public double Surge;
        /// <summary>本次是否触发激增。</summary>
        public bool Surged;
        /// <summary>roll = σ × surge。</summary>
        public double Roll;
        /// <summary>折扣乘数（1.0 = 无折扣）。</summary>
        public double Discount;
        /// <summary>作用域深度权重（1.0 = 中性；零写入/失败路径恒 1.0）。</summary>
        public double ScopeWeight = 1.0;
        /// <summary>本次结算的主导作用域（权重最大者；无作用域为 null）。供校准审计。</summary>
        public string DominantScope;
        /// <summary>道级平坦加数（实际计入的 flat；零写入/失败路径为 0）。</summary>
        public int Flat;
        /// <summary>本次结算使用的单次上限。</summary>
        public int SingleMax;
        /// <summary>是否走了「零写入」分支（恒 1 / 失败 0）。</summary>
        public bool ZeroWrite;
        /// <summary>是否为失败卡路径（不收费）。</summary>
        public bool Failed;
    }

    /// <summary>
    /// token → 业力公式（E0-S2，校准合同载体；工程侧 §7.1-§7.5）。
    ///
    /// 公式：T_eff = Σ(prompt + completion)（completion 含 thinking，DeepSeek 计费口径）
    ///   writes == 0 &&  success → 恒 1（chat_cap 为保险丝，默认不触发；不走 f/roll/discount/flat）
    ///   writes == 0 && !success → 0（失败卡不收费）
    ///   writes &gt;  0            → clamp(round(f(T_eff) × σ × surge × discount × scopeWeight) + flat, 1, singleMax)
    ///
    /// scopeWeight（E3-C 校准补丁）= 本次提交成功写入触及作用域中的最大权重
    /// （<see cref="ScopeWeightsConfig"/>，乘在 discount 之后、flat 之前；无作用域 = 1.0）。
    ///
    /// 纯函数、零 IO：所有随机经 <see cref="IRandom"/> 注入，同种子同 roll（QA 可回放）。
    /// </summary>
    public sealed class KarmaFormula
    {
        // ── 减费技能常量（旧价目表实现逐字平移，折扣语义不变；旧文件随 E2-S7 删除）──
        const double EfficiencyFraudTriggerProbability = 0.30; // 「神思省力」30% 触发
        const double EfficiencyFraudFactor = 0.7;
        const double RealmDiscountFactor = 0.75;               // 地狱/饿鬼、天道/阿修罗

        readonly KarmaCurveConfig _curve;
        readonly FluctuationConfig _fluct;
        readonly ScopeWeightsConfig _scopes;
        readonly int _chatCap;

        /// <param name="chatCap">零写入路径业力硬上限·保险丝（设计 D-1；恒 1 规则下默认不触发）。</param>
        /// <param name="scopeWeights">作用域深度权重表（null = 内置合同默认；校准改 json 后由配置注入）。</param>
        public KarmaFormula(KarmaCurveConfig curve, FluctuationConfig fluctuation, int chatCap = 5,
            ScopeWeightsConfig scopeWeights = null)
        {
            _curve = curve ?? throw new ArgumentNullException(nameof(curve));
            _fluct = fluctuation ?? throw new ArgumentNullException(nameof(fluctuation));
            _chatCap = chatCap;
            _scopes = scopeWeights ?? ScopeWeightsConfig.Default;
        }

        /// <summary>
        /// 分段线性凸函数 f(T_eff)（幂律口径已按裁决 #2 作废）：
        /// 平地价区（T &lt; floor_token）恒 1；锚点间线性插值；末锚后按末段斜率延伸
        /// （默认 0.0072/token ⇒ 触顶 120 落在 ≈25K token）。曲线内部不设上限——
        /// clamp 只发生在 <see cref="Compute"/>，投射阶段需要知道「已远超」。
        /// </summary>
        public double CurveValue(int tokenEff)
        {
            if (tokenEff < 0)
                throw new ArgumentOutOfRangeException(nameof(tokenEff), tokenEff, "token 累计不得为负");

            var anchors = _curve.Anchors;
            if (tokenEff < _curve.FloorToken) return 1.0;

            if (tokenEff <= anchors[anchors.Count - 1].Token)
            {
                if (tokenEff <= anchors[0].Token) return anchors[0].Karma; // 首锚前取常数（不反向外推，保 f≥1 且单调）
                for (int i = 1; i < anchors.Count; i++)
                {
                    var b = anchors[i];
                    if (tokenEff <= b.Token)
                    {
                        var a = anchors[i - 1];
                        return a.Karma + (b.Karma - a.Karma) * (double)(tokenEff - a.Token) / (b.Token - a.Token);
                    }
                }
            }

            var last = anchors[anchors.Count - 1];
            if (_curve.ExtendLastSlope)
            {
                var prev = anchors[anchors.Count - 2];
                double slope = (double)(last.Karma - prev.Karma) / (last.Token - prev.Token);
                return last.Karma + (tokenEff - last.Token) * slope;
            }
            return last.Karma;
        }

        /// <summary>
        /// 投射展示值（§5.5 / §7.5）：round(f(T) × scopeWeight)，「预估·未含天意浮动」。
        /// 不 clamp——超过 singleMax 正是顶格警示（CapWarning）的判据；UI 自行决定呈现。
        /// scopeWeight 传 1.0（默认）即旧口径；引擎传当前已触及作用域的最大权重，让投射与结算同源。
        /// </summary>
        public int ProjectDisplay(int tokenEff, double scopeWeight = ScopeWeightsConfig.NeutralWeight)
        {
            if (double.IsNaN(scopeWeight) || double.IsInfinity(scopeWeight) || scopeWeight <= 0.0)
                scopeWeight = ScopeWeightsConfig.NeutralWeight;
            return (int)Math.Round(CurveValue(tokenEff) * scopeWeight, MidpointRounding.AwayFromZero);
        }

        /// <summary>一组作用域的最大权重（供引擎投射与遥测；空集 = 1.0）。</summary>
        public double ResolveScopeWeight(IEnumerable<string> scopes)
        {
            string dominant;
            return _scopes.Max(scopes, out dominant);
        }

        /// <summary>一组作用域中的主导作用域（权重最大者；无作用域 null）。</summary>
        public string ResolveDominantScope(IEnumerable<string> scopes)
        {
            string dominant;
            _scopes.Max(scopes, out dominant);
            return dominant;
        }

        /// <summary>
        /// 反查触顶 token：f(T) ≥ singleMax 的最小 T（上取整）。
        /// singleMax ≤ 首锚业力 ⇒ 0（平地价区即已触顶）；
        /// 末锚后不延伸且 singleMax 超出末锚业力 ⇒ int.MaxValue（永不触顶，哨兵值）。
        /// </summary>
        public int ProjectCapToken(int singleMax)
        {
            var anchors = _curve.Anchors;
            if (singleMax <= anchors[0].Karma) return 0;
            for (int i = 1; i < anchors.Count; i++)
            {
                var b = anchors[i];
                if (singleMax <= b.Karma)
                {
                    var a = anchors[i - 1];
                    double slope = (double)(b.Karma - a.Karma) / (b.Token - a.Token);
                    return (int)Math.Ceiling(a.Token + (singleMax - a.Karma) / slope);
                }
            }
            var last = anchors[anchors.Count - 1];
            if (_curve.ExtendLastSlope)
            {
                var prev = anchors[anchors.Count - 2];
                double slope = (double)(last.Karma - prev.Karma) / (last.Token - prev.Token);
                return (int)Math.Ceiling(last.Token + (singleMax - last.Karma) / slope);
            }
            return int.MaxValue;
        }

        /// <summary>
        /// 业力结算（Confirm 时调用一次；预览用 <see cref="ProjectDisplay"/>，不抽签）。
        /// 固定抽取序（QA §3.4）：先 σ，后 surge（仅 T≥阈值时），再折扣（仅 efficiency_fraud 时）——
        /// 顺序即契约，任何变动都会破坏黄金回放。
        /// </summary>
        public KarmaResult Compute(int tokenEff, int writes, bool success, KarmaContext ctx, IRandom rng)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (tokenEff < 0)
                throw new ArgumentOutOfRangeException(nameof(tokenEff), tokenEff, "token 累计不得为负");
            if (writes < 0)
                throw new ArgumentOutOfRangeException(nameof(writes), writes, "写入数不得为负");

            double curve = CurveValue(tokenEff);
            var result = new KarmaResult
            {
                CurveValue = curve,
                SingleMax = ctx.SingleMax,
                Discount = 1.0,
            };

            if (writes == 0)
            {
                result.ZeroWrite = true;
                if (!success)
                {
                    // 失败路径不进入计价（设计 §1.4.1 / 工程 §13.6）：失败卡不收费。
                    result.Failed = true;
                    result.Karma = 0;
                    return result;
                }
                // 成功收敛且零写入 ⇒ 恒 1（旧 E 类同口径：折扣钩子之前直返）。
                // chat_cap 语义为「零写入路径业力硬上限·保险丝」（D-1）——恒 1 规则下默认不触发。
                result.Karma = _chatCap >= 0 ? Math.Min(1, _chatCap) : 1;
                return result;
            }

            // writes > 0（含 20s 到点/收手的 partial 预览，收尾策略 A §13.6）：按公式计价。
            double sigma, surge;
            bool surged = DrawRoll(tokenEff, rng, out sigma, out surge);
            double roll = sigma * surge;
            double discount = DiscountFactor(ctx, rng);
            // E3-C：作用域深度权重乘在 discount 之后、flat 之前（乘数链顺序即契约）。
            string dominant;
            double scopeWeight = _scopes.Max(ctx.ScopeTags, out dominant);

            int karma = (int)Math.Round(curve * roll * discount * scopeWeight, MidpointRounding.AwayFromZero) + ctx.Flat;
            int lower = Math.Max(1, _curve.Min);
            karma = Math.Max(lower, Math.Min(karma, ctx.SingleMax));

            result.Sigma = sigma;
            result.Surge = surge;
            result.Surged = surged;
            result.Roll = roll;
            result.Discount = discount;
            result.ScopeWeight = scopeWeight;
            result.DominantScope = dominant;
            result.Flat = ctx.Flat;
            result.Karma = karma;
            return result;
        }

        /// <summary>roll = σ × surge。抽取序即契约：先 σ；T≥阈值才抽 surge 触发签，触发再抽因子签。</summary>
        bool DrawRoll(int tokenEff, IRandom rng, out double sigma, out double surge)
        {
            sigma = _fluct.UniformMin + rng.NextDouble() * (_fluct.UniformMax - _fluct.UniformMin);
            surge = 1.0;
            if (tokenEff >= _fluct.SurgeTokenThreshold && rng.NextDouble() < _fluct.SurgeProbability)
            {
                surge = _fluct.SurgeMin + rng.NextDouble() * (_fluct.SurgeMax - _fluct.SurgeMin);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 折扣链（旧价目表实现平移）：efficiency_fraud 30% 触发 ×0.7；
        /// 地狱/饿鬼道 ×0.75、天道/阿修罗道 ×0.75（技能开关命中且道境匹配才生效）。
        /// 折扣在 roll 之后、flat 加数之前（§7.4），与旧「先折扣后 clamp」等价。
        /// </summary>
        double DiscountFactor(KarmaContext ctx, IRandom rng)
        {
            double m = 1.0;
            if (ctx.EfficiencyFraud && rng.NextDouble() < EfficiencyFraudTriggerProbability)
                m *= EfficiencyFraudFactor;

            var realm = ctx.Realm ?? string.Empty;
            if (ctx.HellHungryDiscount && IsRealm(realm, "hell", "hungry"))
                m *= RealmDiscountFactor;
            if (ctx.HeavenAsuraDiscount && IsRealm(realm, "heaven", "asura"))
                m *= RealmDiscountFactor;
            return m;
        }

        static bool IsRealm(string realm, string a, string b)
            => string.Equals(realm, a, StringComparison.OrdinalIgnoreCase)
            || string.Equals(realm, b, StringComparison.OrdinalIgnoreCase);
    }
}
