using System;
using System.Collections.Generic;
using ChessSage.Core.Agent;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E0-S2：token → 业力公式（校准合同载体，QA 文档 §3 / 设计文档 §1.4 已签署）。
    ///
    /// 【独立推导原则】期望值一律用测试侧字面量独立计算，不调用被测实现：
    ///   - 锚点表 = 设计侧 §1.4 六档合同（2500→1 / 8000→16 / 12000→32 / 15000→48 / 16000→55 / 21000→91）；
    ///   - 段斜率/千 token：0（平地价区）→ 15/5.5 → 16/4 → 16/3 → 7/1 → 36/5 → 36/5（末段延伸）。
    /// 【随机源】σ 钉死：σ = 0.8 + u×0.5，u=0.4 ⇒ σ=1.0；激增触发签 u=0.9（不触发）/0.1（触发）。
    /// </summary>
    [TestFixture]
    public sealed class KarmaFormulaTests
    {
        static KarmaFormula Formula() => new KarmaFormula(KarmaCurveConfig.Default, FluctuationConfig.Default);

        static KarmaContext Ctx(
            string realm = "human", int singleMax = 120, int flat = 0,
            bool efficiencyFraud = false, bool hellHungry = false, bool heavenAsura = false)
            => new KarmaContext
            {
                Realm = realm,
                SingleMax = singleMax,
                Flat = flat,
                EfficiencyFraud = efficiencyFraud,
                HellHungryDiscount = hellHungry,
                HeavenAsuraDiscount = heavenAsura,
            };

        // ───────────────────────────────────────────────
        // A3 / KRM-07…12：六锚点黄金值（合同 = 设计 §1.4）
        // ───────────────────────────────────────────────

        [TestCase(2500, 1)]
        [TestCase(8000, 16)]
        [TestCase(12000, 32)]
        [TestCase(15000, 48)]
        [TestCase(16000, 55)]
        [TestCase(21000, 91)]
        public void A3_CurveValue_AtAnchor_ReturnsSignedKarma(int tokenEff, int expected)
        {
            Assert.That(Formula().CurveValue(tokenEff), Is.EqualTo((double)expected).Within(1e-9),
                $"锚点 {tokenEff} 的 f 值应精确为 {expected}");
        }

        [TestCase(2500, 1)]
        [TestCase(8000, 16)]
        [TestCase(12000, 32)]
        [TestCase(15000, 48)]
        [TestCase(16000, 55)]
        [TestCase(21000, 91)]
        public void A3_Compute_AtAnchor_WithPinnedSigma_ReturnsSignedKarma(int tokenEff, int expected)
        {
            // σ=1.0 钉死；T≥20K 时补一签 0.9 使激增不触发（固定抽取序：先 σ 后 surge）。
            var rng = tokenEff >= 20000 ? new ScriptedRandom(0.4, 0.9) : (IRandom)new ScriptedRandom(0.4);
            int karma = Formula().Compute(tokenEff, writes: 1, success: true, ctx: Ctx(), rng: rng).Karma;
            Assert.AreEqual(expected, karma, $"锚点 {tokenEff}（σ=1）应精确落 {expected}");
        }

        // ───────────────────────────────────────────────
        // A1 / KRM-01：单调性（QA §3.2 与设计 §1.4 的调和，见测试体注释）
        // ───────────────────────────────────────────────

        static readonly int[] MonotonicSamples = { 0, 100, 500, 1000, 2000, 4000, 8000, 16000, 32000 };

        [Test]
        public void A1_CurveValue_IsNonDecreasing_AcrossFullDomain()
        {
            // 全区间（含平地价区）非降——QA §3.2 字面「严格递增」与设计 §1.4「T<2,500 平地价 f=1」
            // 在 [0,2500) 上不可同时成立；设计侧锚点表为已签署合同（裁决 #2），故：
            // 全区间断言非降，严格递增只覆盖平地价区以上（下一用例）。
            var f = Formula();
            for (int i = 1; i < MonotonicSamples.Length; i++)
            {
                var prev = f.CurveValue(MonotonicSamples[i - 1]);
                var cur = f.CurveValue(MonotonicSamples[i]);
                Assert.That(cur, Is.GreaterThanOrEqualTo(prev),
                    $"f({MonotonicSamples[i]})={cur} 不得低于 f({MonotonicSamples[i - 1]})={prev}");
            }
        }

        [Test]
        public void A1_CurveValue_IsStrictlyIncreasing_AboveFloorZone()
        {
            var f = Formula();
            int prev = KarmaCurveConfig.Default.FloorToken; // 2500
            for (int t = prev + 250; t <= 32000; t += 250)
            {
                var a = f.CurveValue(prev);
                var b = f.CurveValue(t);
                Assert.That(b, Is.GreaterThan(a), $"f({t})={b} 应严格大于 f({prev})={a}");
                prev = t;
            }
        }

        // ───────────────────────────────────────────────
        // A2 / KRM-02：凸性（相邻段边际业价非降）
        // ───────────────────────────────────────────────

        [Test]
        public void A2_SegmentMarginalPrices_AreNonDecreasing()
        {
            // 测试侧独立推导：各段斜率（点/千 token），含平地价区 0 与末段延伸。
            var expected = new[] { 0.0, 15.0 / 5.5, 16.0 / 4.0, 16.0 / 3.0, 7.0 / 1.0, 36.0 / 5.0, 36.0 / 5.0 };
            var anchors = KarmaCurveConfig.Default.Anchors;
            var slopes = new List<double> { 0.0 }; // 平地价区
            for (int i = 1; i < anchors.Count; i++)
                slopes.Add((anchors[i].Karma - anchors[i - 1].Karma) * 1000.0 / (anchors[i].Token - anchors[i - 1].Token));
            slopes.Add(slopes[slopes.Count - 1]); // 末段延伸斜率 = 末段斜率

            Assert.AreEqual(expected.Length, slopes.Count, "段数应一致");
            for (int i = 0; i < slopes.Count; i++)
                Assert.That(slopes[i], Is.EqualTo(expected[i]).Within(1e-9), $"第 {i} 段斜率");
            for (int i = 1; i < slopes.Count; i++)
                Assert.That(slopes[i], Is.GreaterThanOrEqualTo(slopes[i - 1]), "边际业价必须非降（凸性）");
        }

        [Test]
        public void A2_DiscreteDifferences_AreNonDecreasing()
        {
            // 与段枚举无关的独立凸性检查：固定步长差分 d(T)=f(T+Δ)-f(T) 随 T 非降。
            var f = Formula();
            const int step = 500, delta = 1000;
            double prevDiff = double.NegativeInfinity;
            for (int t = 0; t + delta <= 32000; t += step)
            {
                double d = f.CurveValue(t + delta) - f.CurveValue(t);
                Assert.That(d, Is.GreaterThanOrEqualTo(prevDiff - 1e-9),
                    $"t={t} 处差分 {d} 不得低于前一差分 {prevDiff}");
                prevDiff = d;
            }
        }

        [Test]
        public void A2_ExtensionSlope_EqualsLastSegmentSlope()
        {
            // 末段斜率 36/5000 = 0.0072/token（设计 §1.5）。
            var f = Formula();
            double slope = (f.CurveValue(30000) - f.CurveValue(25000)) / 5000.0;
            Assert.That(slope, Is.EqualTo(36.0 / 5000.0).Within(1e-12), "延伸斜率应等于末段斜率 0.0072");
        }

        // ───────────────────────────────────────────────
        // A4 / KRM-03/04：clamp 边界
        // ───────────────────────────────────────────────

        [Test]
        public void A4_Compute_AtZeroToken_ReturnsFloor1()
        {
            // f(0)=1（平地价区）；FixedRandom(0.8) ⇒ σ=1.2 ⇒ round(1×1.2)=1。
            var karma = Formula().Compute(0, writes: 1, success: true, ctx: Ctx(), rng: new FixedRandom(0.8)).Karma;
            Assert.AreEqual(1, karma);
        }

        [Test]
        public void A4_Compute_RoundsBelowHalf_ClampsTo1()
        {
            // KRM-03b：round 后为 0 也应被下限捞回 1。
            // f(2500)=1；σ=0.8（u=0）；efficiency_fraud 触发（签 0）×0.7；畜生/饿鬼道 ×0.75
            // ⇒ 1×0.8×0.525=0.42 ⇒ round=0 ⇒ clamp 1。
            var ctx = Ctx(realm: "hell", efficiencyFraud: true, hellHungry: true);
            var karma = Formula().Compute(2500, writes: 1, success: true, ctx: ctx, rng: new ScriptedRandom(0.0, 0.0)).Karma;
            Assert.AreEqual(1, karma);
        }

        [Test]
        public void A4_Compute_HugeToken_ClampsToSingleMax()
        {
            // KRM-04：T=10^9，FixedRandom(1.0) ⇒ σ=1.3；T≥20K 激升签 1.0 ⇒ 不触发。
            var karma = Formula().Compute(1000000000, writes: 1, success: true, ctx: Ctx(), rng: new FixedRandom(1.0)).Karma;
            Assert.AreEqual(120, karma);
        }

        [TestCase(160)]
        [TestCase(150)]
        public void A4_Compute_RaisedSingleMax_ClampsToRaisedCap(int singleMax)
        {
            // KRM-04b：技能抬高上限（karma_capacity_t2a / cheat_mastery_t3b，+40/次）。
            var karma = Formula().Compute(1000000000, writes: 1, success: true,
                ctx: Ctx(singleMax: singleMax), rng: new FixedRandom(1.0)).Karma;
            Assert.AreEqual(singleMax, karma);
        }

        // ───────────────────────────────────────────────
        // A5 / D-1：零写入恒 1（chat_cap=5 为保险丝，默认不触发）
        // ───────────────────────────────────────────────

        [Test]
        public void A5_ZeroWriteSuccess_IsConstant1_Across64Seeds()
        {
            var f = Formula();
            for (int seed = 1; seed <= 64; seed++)
                foreach (var t in new[] { 2500, 8000, 16000, 1000000000 })
                    Assert.AreEqual(1, f.Compute(t, writes: 0, success: true, ctx: Ctx(), rng: new DeterministicRng(seed)).Karma,
                        $"零写入成功必须恒 1（seed={seed}, T={t}）");
        }

        [Test]
        public void A5_ZeroWriteSuccess_IgnoresDiscountAndFlat()
        {
            // 旧 E 类路径在折扣钩子之前直返（KarmaEstimator.cs:162）；新语义同口径：
            // 恒 1，不过 f、不随 σ/surge、不吃折扣与道级加数。
            var ctx = Ctx(realm: "animal", flat: 20, efficiencyFraud: true, hellHungry: true);
            var karma = Formula().Compute(16000, writes: 0, success: true, ctx: ctx, rng: new ExplodingRandom()).Karma;
            Assert.AreEqual(1, karma);
        }

        [Test]
        public void A5_ZeroWriteSuccess_ConsumesNoRandomness()
        {
            // 恒 1 路径不抽签：任何抽取都是 bug（会破坏同种子 roll 序列的可复现性）。
            Assert.DoesNotThrow(() =>
                Formula().Compute(16000, writes: 0, success: true, ctx: Ctx(), rng: new ExplodingRandom()));
        }

        [Test]
        public void A5_ZeroWriteNotConverged_Returns0()
        {
            // 失败卡不收费（§1.4.1 / §13.6 策略 B）：20s 到点/收手/校验耗尽且零写入。
            foreach (var t in new[] { 2500, 16000, 1000000000 })
            {
                var ctx = Ctx(realm: "animal", flat: 20, efficiencyFraud: true);
                Assert.AreEqual(0, Formula().Compute(t, writes: 0, success: false, ctx: ctx, rng: new ExplodingRandom()).Karma,
                    $"零写入未收敛必须 0 费（T={t}）");
            }
        }

        [Test]
        public void A5_ChatCap_IsFuseOnly_NeverTriggersUnderConstant1()
        {
            // chat_cap=5 语义为「零写入路径业力硬上限·保险丝」（设计 §1.4.1/D-1）：
            // 恒 1 规则下默认不触发；即使显式调低到 3/1 也不改变恒 1 结论。
            foreach (var cap in new[] { 5, 3, 1 })
            {
                var f = new KarmaFormula(KarmaCurveConfig.Default, FluctuationConfig.Default, cap);
                Assert.AreEqual(1, f.Compute(2500, writes: 0, success: true, ctx: Ctx(), rng: new ExplodingRandom()).Karma,
                    $"chat_cap={cap} 时零写入仍应恒 1");
            }
        }

        [Test]
        public void A5_WritesPositiveNotConverged_StillPriced()
        {
            // 收尾策略 A（§13.6）：20s 到点/收手时有写入 ⇒ partial 预览按 f(T_stop) 计价；
            // success 只区分零写入路径的 1 与 0。
            var karma = Formula().Compute(16000, writes: 1, success: false, ctx: Ctx(), rng: new ScriptedRandom(0.4)).Karma;
            Assert.AreEqual(55, karma);
        }

        // ───────────────────────────────────────────────
        // A6 / A7 / KRM-05/06：波动区间与可复现性
        // ───────────────────────────────────────────────

        [TestCase(8000, 13, 21)]    // D：f=16，无激增 ⇒ [round(12.8), round(20.8)]
        [TestCase(12000, 26, 42)]   // B：f=32 ⇒ [round(25.6), round(41.6)]
        [TestCase(15000, 38, 62)]   // A：f=48 ⇒ [round(38.4), round(62.4)]
        [TestCase(16000, 44, 72)]   // C：f=55 ⇒ [round(44.0), round(71.5)]（远零舍入 ⇒ 72）
        [TestCase(21000, 73, 120)]  // C+：f=91，激增可至 91×1.3×1.5=177.45 ⇒ clamp 120
        [TestCase(2500, 1, 5)]      // E（有写入）：f=1 ⇒ [round(0.8), round(1.3)] = [1,1] ⊂ [1,5]
        public void A6_AnchorBand_64Seeds_AllLandsInSignedRange(int tokenEff, int min, int max)
        {
            var f = Formula();
            for (int seed = 1; seed <= 64; seed++)
            {
                int karma = f.Compute(tokenEff, writes: 1, success: true, ctx: Ctx(), rng: new DeterministicRng(seed)).Karma;
                Assert.That(karma, Is.InRange(min, max), $"T={tokenEff} seed={seed} 落点 {karma} 出签约带 [{min},{max}]");
            }
        }

        [Test]
        public void A6_SameSeed_SameRoll_SameKarma()
        {
            var f = Formula();
            foreach (var t in new[] { 16000, 22000 })
            {
                var a = f.Compute(t, writes: 1, success: true, ctx: Ctx(), rng: new DeterministicRng(20261023, requestSeq: 3)).Karma;
                var b = f.Compute(t, writes: 1, success: true, ctx: Ctx(), rng: new DeterministicRng(20261023, requestSeq: 3)).Karma;
                Assert.AreEqual(a, b, $"同种子（会话 20261023 / 请求 3）必须同 roll（T={t}）");
            }
        }

        [Test]
        public void A7_SurgeTriggerRate_IsPointThree_Over10k()
        {
            // QA §3.4：T≥20K 时激增触发率 ∈ [0.25, 0.35]（10k 样本，p=0.3±0.05）。
            var f = Formula();
            int hits = 0;
            const int N = 10000;
            for (int i = 0; i < N; i++)
                if (f.Compute(22000, writes: 1, success: true, ctx: Ctx(), rng: new DeterministicRng(777, i)).Surged)
                    hits++;
            double rate = (double)hits / N;
            Assert.That(rate, Is.InRange(0.25, 0.35), $"激增触发率 {rate:F4} 偏离 p=0.3");
        }

        [Test]
        public void A7_NoSurgeBand_BelowThreshold_10k()
        {
            // QA §3.4：T=10K（<20K 无激增）f=24 ⇒ 样本 min ≥ round(24×0.8)=19、max ≤ round(24×1.3)=31。
            var f = Formula();
            int min = int.MaxValue, max = int.MinValue;
            double sum = 0;
            var histogram = new int[13]; // 业力 19..31
            const int N = 10000;
            for (int i = 0; i < N; i++)
            {
                int karma = f.Compute(10000, writes: 1, success: true, ctx: Ctx(), rng: new DeterministicRng(31337, i)).Karma;
                min = Math.Min(min, karma);
                max = Math.Max(max, karma);
                sum += karma;
                if (karma >= 19 && karma <= 31) histogram[karma - 19]++;
            }
            Assert.GreaterOrEqual(min, 19, "无激增区下界");
            Assert.LessOrEqual(max, 31, "无激增区上界");
            Assert.That(sum / N, Is.InRange(24.5, 26.0), "无激增区均值应接近 f×E[σ]=25.2");
            foreach (var c in histogram)
                Assert.Greater(c, 0, "13 个业力桶均应非空（防假随机，QA §3.4 分布形状抽查）");
        }

        [Test]
        public void A7_SurgeSequence_Reproducible_100Seeds()
        {
            var f = Formula();
            var first = RunBand(f, 22000, 100);
            var second = RunBand(f, 22000, 100);
            CollectionAssert.AreEqual(first, second, "激增序列必须逐 seed 可复现");
        }

        static int[] RunBand(KarmaFormula f, int tokenEff, int seeds)
        {
            var a = new int[seeds];
            for (int seed = 1; seed <= seeds; seed++)
                a[seed - 1] = f.Compute(tokenEff, writes: 1, success: true, ctx: Ctx(), rng: new DeterministicRng(seed)).Karma;
            return a;
        }

        // ───────────────────────────────────────────────
        // A8：触顶 token（120 ≈ 25K，±10%）
        // ───────────────────────────────────────────────

        [Test]
        public void A8_ProjectCapToken_120_LandsAt25K()
        {
            // 独立推导：21000 + (120-91)/0.0072 = 25027.78 ⇒ 上取整 25028。
            int capToken = Formula().ProjectCapToken(120);
            Assert.AreEqual(25028, capToken, "f(T)≥120 的最小 T 应为 25028");
            Assert.That(capToken, Is.InRange(22500, 27500), "触顶 token 应落在 25K±10% 带内");
        }

        [TestCase(150, 29195)]  // 21000 + ceil(59/0.0072)   ≈ 29.2K（设计 §1.5）
        [TestCase(160, 30584)]  // 21000 + ceil(69/0.0072)   ≈ 30.6K（设计 §1.5）
        public void A8_ProjectCapToken_RaisedCaps_MatchDesignFigures(int singleMax, int expected)
        {
            Assert.AreEqual(expected, Formula().ProjectCapToken(singleMax),
                $"技能抬高上限 {singleMax} 的触顶 token 应匹配设计 §1.5 图示");
        }

        [TestCase(16, 8000)]
        [TestCase(55, 16000)]
        [TestCase(91, 21000)]
        [TestCase(1, 0)]       // 平地价区即已「触顶」
        public void A8_ProjectCapToken_AnchorLevels_ExactReverse(int singleMax, int expected)
        {
            Assert.AreEqual(expected, Formula().ProjectCapToken(singleMax));
        }

        [Test]
        public void A8_ProjectDisplay_AtCapToken_Returns120()
        {
            Assert.AreEqual(120, Formula().ProjectDisplay(25028));
        }

        [Test]
        public void A8_CurveValue_KeepsGrowing_BeyondCap()
        {
            // 曲线内部不设上限（clamp 在 Compute）；投射阶段需要知道「已远超」。
            var f = Formula();
            Assert.That(f.CurveValue(30000), Is.EqualTo(155.8).Within(1e-9), "91 + 9000×0.0072");
            Assert.AreEqual(156, f.ProjectDisplay(30000));
        }

        [Test]
        public void A8_ProjectDisplay_UsesAwayFromZeroRounding()
        {
            // f(5250) = 1 + 15×2750/5500 = 8.5 ⇒ 远零舍入 9（银行家舍入会给 8）。
            Assert.AreEqual(9, Formula().ProjectDisplay(5250));
        }

        // ───────────────────────────────────────────────
        // KRM-15/16/17：折扣链（KarmaEstimator 平移）与道级加数（D-7）
        // ───────────────────────────────────────────────

        [Test]
        public void D_EfficiencyFraud_Triggered_Applies07()
        {
            // 55×1.0×0.7 = 38.5 ⇒ 远零舍入 39（钉死舍入模式：银行家舍入会给 38）。
            var ctx = Ctx(efficiencyFraud: true);
            var karma = Formula().Compute(16000, writes: 1, success: true, ctx: ctx, rng: new ScriptedRandom(0.4, 0.0)).Karma;
            Assert.AreEqual(39, karma);
        }

        [Test]
        public void D_EfficiencyFraud_NotTriggered_NoDiscount()
        {
            var ctx = Ctx(efficiencyFraud: true);
            var karma = Formula().Compute(16000, writes: 1, success: true, ctx: ctx, rng: new ScriptedRandom(0.4, 0.9)).Karma;
            Assert.AreEqual(55, karma);
        }

        [TestCase("hell", true, false, 41)]      // 55×0.75 = 41.25 ⇒ 41
        [TestCase("hungry", true, false, 41)]
        [TestCase("heaven", false, true, 41)]
        [TestCase("asura", false, true, 41)]
        [TestCase("heaven", true, false, 55)]    // 道境不匹配 ⇒ 折扣不生效
        [TestCase("hell", false, true, 55)]
        [TestCase("human", true, true, 55)]
        public void D_RealmDiscounts_ApplyOnlyInMatchingRealm(string realm, bool hellHungry, bool heavenAsura, int expected)
        {
            var ctx = Ctx(realm: realm, hellHungry: hellHungry, heavenAsura: heavenAsura);
            var karma = Formula().Compute(16000, writes: 1, success: true, ctx: ctx, rng: new ScriptedRandom(0.4)).Karma;
            Assert.AreEqual(expected, karma);
        }

        [Test]
        public void D_Discounts_Stack_Multiplicatively()
        {
            // 饿鬼道 ×0.75 与 efficiency_fraud ×0.7 叠乘：55×0.7×0.75 = 28.875 ⇒ 29。
            var ctx = Ctx(realm: "hungry", efficiencyFraud: true, hellHungry: true);
            var karma = Formula().Compute(16000, writes: 1, success: true, ctx: ctx, rng: new ScriptedRandom(0.4, 0.0)).Karma;
            Assert.AreEqual(29, karma);
        }

        [Test]
        public void D_RealmFlat_AddedAfterRound_BeforeClamp()
        {
            // D-7：畜生道高等级棋子 +20，flat 在 round 之后、clamp 之前。
            var ctx = Ctx(realm: "animal", flat: 20);
            var karma = Formula().Compute(16000, writes: 1, success: true, ctx: ctx, rng: new ScriptedRandom(0.4)).Karma;
            Assert.AreEqual(75, karma);
        }

        [Test]
        public void D_Flat_AppliesAfterDiscountRounding()
        {
            // round(55×0.7)=39，再加 flat 20 ⇒ 59（不是 round(55×0.7)+20 之外的其它顺序）。
            var ctx = Ctx(realm: "animal", flat: 20, efficiencyFraud: true);
            var karma = Formula().Compute(16000, writes: 1, success: true, ctx: ctx, rng: new ScriptedRandom(0.4, 0.0)).Karma;
            Assert.AreEqual(59, karma);
        }

        [Test]
        public void D_Flat_PushesToSingleMax_Clamped()
        {
            // 91 + 40 = 131 ⇒ clamp 120；上限抬高到 160 则放行 131。
            var ctx120 = Ctx(flat: 40);
            Assert.AreEqual(120, Formula().Compute(21000, writes: 1, success: true, ctx: ctx120, rng: new ScriptedRandom(0.4, 0.9)).Karma);
            var ctx160 = Ctx(singleMax: 160, flat: 40);
            Assert.AreEqual(131, Formula().Compute(21000, writes: 1, success: true, ctx: ctx160, rng: new ScriptedRandom(0.4, 0.9)).Karma);
        }

        [Test]
        public void D_Discount_DoesNotTouchCurveValue()
        {
            // KRM-17：折扣是业力乘数，不得触碰 token 账本（f 与 T_eff 无关）。
            var f = Formula();
            Assert.That(f.CurveValue(16000), Is.EqualTo(55.0).Within(1e-9));
            var bare = f.Compute(16000, writes: 1, success: true, ctx: Ctx(), rng: new ScriptedRandom(0.4));
            var skilled = f.Compute(16000, writes: 1, success: true, ctx: Ctx(efficiencyFraud: true), rng: new ScriptedRandom(0.4, 0.0));
            Assert.AreEqual(bare.CurveValue, skilled.CurveValue, "f 值不得因折扣改变");
            Assert.AreEqual(55, bare.Karma);
            Assert.AreEqual(39, skilled.Karma);
        }

        // ───────────────────────────────────────────────
        // 结果分解（{karma, roll, surged, breakdown}，设计 §6.1）
        // ───────────────────────────────────────────────

        [Test]
        public void Result_Breakdown_Fields_MatchScript()
        {
            // σ=1.0（签 0.4）；激增触发（签 0.1）且因子取下端 u=0.0 ⇒ 1.25+0×0.25=1.25。
            var r = Formula().Compute(21000, writes: 1, success: true, ctx: Ctx(), rng: new ScriptedRandom(0.4, 0.1, 0.0));
            Assert.AreEqual(91, r.CurveValue);
            Assert.AreEqual(1.0, r.Sigma);
            Assert.AreEqual(1.25, r.Surge);
            Assert.IsTrue(r.Surged);
            Assert.AreEqual(1.25, r.Roll);
            Assert.AreEqual(1.0, r.Discount);
            Assert.AreEqual(0, r.Flat);
            Assert.AreEqual(120, r.SingleMax);
            Assert.IsFalse(r.ZeroWrite);
            Assert.IsFalse(r.Failed);
            Assert.AreEqual(114, r.Karma); // round(91×1.25)=round(113.75)=114（≤120 不触 clamp）
        }

        [Test]
        public void Result_ZeroWrite_MarksPath_AndSkipsRoll()
        {
            var r = Formula().Compute(16000, writes: 0, success: true, ctx: Ctx(), rng: new ExplodingRandom());
            Assert.IsTrue(r.ZeroWrite);
            Assert.IsFalse(r.Failed);
            Assert.AreEqual(1, r.Karma);
            Assert.AreEqual(0.0, r.Roll, "恒 1 路径不抽 roll");
            Assert.AreEqual(55.0, r.CurveValue, "曲线值仍如实上报（遥测用）");
        }

        [Test]
        public void Result_ZeroWriteFailed_MarksFailure()
        {
            var r = Formula().Compute(16000, writes: 0, success: false, ctx: Ctx(), rng: new ExplodingRandom());
            Assert.IsTrue(r.Failed);
            Assert.IsTrue(r.ZeroWrite);
            Assert.AreEqual(0, r.Karma);
        }

        // ───────────────────────────────────────────────
        // 参数化（E0-S5 地基）：换 anchors，f 随之而变，代码零改
        // ───────────────────────────────────────────────

        static KarmaFormula CustomFormula()
        {
            var curve = new KarmaCurveConfig(
                new[] { new KarmaAnchor(1000, 5), new KarmaAnchor(2000, 10) },
                floorToken: 1000, extendLastSlope: false, min: 1, max: 100);
            return new KarmaFormula(curve, FluctuationConfig.Default);
        }

        [Test]
        public void P_CustomAnchors_ChangeCurveOutput()
        {
            var f = CustomFormula();
            Assert.That(f.CurveValue(1500), Is.EqualTo(7.5).Within(1e-9), "线性插值 5+(10-5)×500/1000");
            Assert.AreEqual(8, f.ProjectDisplay(1500));      // 7.5 远零舍入
            Assert.AreEqual(8, f.Compute(1500, writes: 1, success: true, ctx: Ctx(), rng: new ScriptedRandom(0.4)).Karma);
            Assert.That(f.CurveValue(5000), Is.EqualTo(10.0).Within(1e-9), "关闭延伸 ⇒ 末锚后恒定");
            Assert.AreEqual(2000, f.ProjectCapToken(10));
            Assert.AreEqual(1400, f.ProjectCapToken(7));     // 1000 + (7-5)/(5/1000)
        }

        [Test]
        public void P_ExtensionDisabled_DefaultCurve_NeverCaps()
        {
            var curve = new KarmaCurveConfig(
                new[] { new KarmaAnchor(2500, 1), new KarmaAnchor(8000, 16) },
                floorToken: 2500, extendLastSlope: false, min: 1, max: 120);
            var f = new KarmaFormula(curve, FluctuationConfig.Default);
            Assert.That(f.CurveValue(50000), Is.EqualTo(16.0).Within(1e-9));
            Assert.AreEqual(int.MaxValue, f.ProjectCapToken(120), "不延伸则永不触顶（哨兵值）");
        }

        // ───────────────────────────────────────────────
        // 入参校验（fail fast，E0-S5 加载器据此兜底）
        // ───────────────────────────────────────────────

        [Test]
        public void V_CurveConfig_RejectsInvalidShapes()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new KarmaCurveConfig(null, 2500, true, 1, 120));
            Assert.Throws<ArgumentException>(() =>
                new KarmaCurveConfig(new KarmaAnchor[0], 2500, true, 1, 120));
            Assert.Throws<ArgumentException>(() =>
                new KarmaCurveConfig(new[] { new KarmaAnchor(2500, 1) }, 2500, true, 1, 120));
            Assert.Throws<ArgumentException>(() =>
                new KarmaCurveConfig(new[] { new KarmaAnchor(8000, 16), new KarmaAnchor(2500, 1) }, 2500, true, 1, 120));
            Assert.Throws<ArgumentException>(() =>
                new KarmaCurveConfig(new[] { new KarmaAnchor(2500, 0), new KarmaAnchor(8000, 16) }, 2500, true, 1, 120));
            Assert.Throws<ArgumentException>(() =>
                new KarmaCurveConfig(new[] { new KarmaAnchor(2500, 1), new KarmaAnchor(8000, 16) }, -1, true, 1, 120));
            Assert.Throws<ArgumentException>(() =>
                new KarmaCurveConfig(new[] { new KarmaAnchor(2500, 1), new KarmaAnchor(8000, 16) }, 2500, true, 5, 1));
        }

        [Test]
        public void V_FluctuationConfig_RejectsInvalidRanges()
        {
            Assert.Throws<ArgumentException>(() => new FluctuationConfig(1.3, 0.8, 20000, 0.3, 1.25, 1.5));
            Assert.Throws<ArgumentException>(() => new FluctuationConfig(-0.1, 1.3, 20000, 0.3, 1.25, 1.5));
            Assert.Throws<ArgumentException>(() => new FluctuationConfig(0.8, 1.3, 20000, 1.5, 1.25, 1.5));
            Assert.Throws<ArgumentException>(() => new FluctuationConfig(0.8, 1.3, 20000, 0.3, 1.5, 1.25));
            Assert.Throws<ArgumentException>(() => new FluctuationConfig(0.8, 1.3, -1, 0.3, 1.25, 1.5));
            Assert.DoesNotThrow(() => new FluctuationConfig(0.8, 1.3, 20000, 0.0, 1.25, 1.5));
            Assert.DoesNotThrow(() => new FluctuationConfig(0.8, 1.3, 20000, 1.0, 1.25, 1.5));
        }

        [Test]
        public void V_Formula_RejectsNullInputs()
        {
            Assert.Throws<ArgumentNullException>(() => new KarmaFormula(null, FluctuationConfig.Default));
            Assert.Throws<ArgumentNullException>(() => new KarmaFormula(KarmaCurveConfig.Default, null));
            var f = Formula();
            Assert.Throws<ArgumentNullException>(() => f.Compute(16000, 1, true, null, new ScriptedRandom(0.4)));
            Assert.Throws<ArgumentNullException>(() => f.Compute(16000, 1, true, Ctx(), null));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Compute(-1, 1, true, Ctx(), new ScriptedRandom(0.4)));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Compute(16000, -1, true, Ctx(), new ScriptedRandom(0.4)));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.CurveValue(-1));
        }

        // ───────────────────────────────────────────────
        // 测试替身：钉死/脚本化随机源
        // ───────────────────────────────────────────────

        sealed class FixedRandom : IRandom
        {
            readonly double _value;
            public FixedRandom(double value) { _value = value; }
            public double NextDouble() => _value;
        }

        sealed class ScriptedRandom : IRandom
        {
            readonly double[] _script;
            int _index;
            public ScriptedRandom(params double[] script) { _script = script; }
            public double NextDouble()
            {
                if (_index >= _script.Length)
                    throw new InvalidOperationException($"脚本随机源耗尽（已取 {_index} 次，脚本长度 {_script.Length}）");
                return _script[_index++];
            }
        }

        /// <summary>任何抽取即抛错——用于断言「该路径不得消耗随机源」。</summary>
        sealed class ExplodingRandom : IRandom
        {
            public double NextDouble() => throw new InvalidOperationException("该路径不得消耗随机源");
        }
    }
}
