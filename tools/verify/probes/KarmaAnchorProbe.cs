using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ChessSage.Core.Agent;

namespace ChessSage.Harness
{
    /// <summary>
    /// P2 KarmaAnchorProbe（QA §4.4.1-P2 / E3-S4）：六档业力锚点抽查（纯函数，秒级）。
    ///
    /// 【输入】探针内置六档黄金镜像（合同 = 设计侧 §1.4 v1：E:1@2,500 / D:16@8,000 / B:32@12,000 /
    /// A:48@15,000 / C:55@16,000 / C+:91@21,000，带 <see cref="AgentProbeKit.DesignContract.Version"/> 标签）
    /// + FixedRandom 端点 + DeterministicRng 32 seed 全波动扫描 + clamp 边界（T=0、T=10⁹）。
    ///
    /// 【量化通过标准】全部满足才算 PASS：
    ///   ① roll=1.0（FixedRandom(0.4)：σ = 0.8+0.4×0.5 = 1.0 且 0.4 ≥ 0.3 不触发激增）时，
    ///      每档 karma 落在锚点值 ±1（round 语义）。
    ///   ② 每档 32 个 seed 全波动扫描，karma 全部落在设计区间：
    ///      E:1–5 / D:13–21 / B:26–42 / A:38–62 / C:44–72 / C+:73–120。
    ///   ③ clamp 端点：T=0 → 1；T=10⁹ → 120。
    ///   ④ 激增触发率：T=22,000 抽 1,000 次，触发率 ∈ [0.25,0.35]（p=0.30 ± 0.05）。
    ///   ⑤ **镜像 version 一致性**：内置镜像锚点与实现（内置默认 + 生产 agent_remake.json）逐值相等；
    ///      不一致即 FAIL 并提示走 §3.6 重拟合治理（禁「先跑着」）。
    ///   ⑥ 探针耗时 &lt; 1s。
    ///
    /// 【FAIL 判定】任一档点估偏差 &gt; ±1 / 任一 seed 出界 / clamp 端点错 / 触发率出界 /
    /// 镜像与实现锚点不等 / 耗时超预算。
    /// 【独立性纪律】期望值全部由 <see cref="AgentProbeKit.DesignContract"/> 的字面量重算，
    /// 不调用 <see cref="KarmaFormula.CurveValue"/> 产出期望值；实现值只与它比对。
    /// </summary>
    public static class KarmaAnchorProbe
    {
        const int SingleMax = 120;
        const int SeedsPerAnchor = 32;
        const int SurgeSamples = 1000;
        const int SurgeTokenEff = 22000;

        public static void Run()
        {
            var watch = Stopwatch.StartNew();

            var mirror = MirrorMatchesImplementation();
            var anchorLines = new List<string>();
            var pointEstimates = CheckAnchorPointEstimates(anchorLines);
            CheckAnchorSeedSweep(anchorLines);
            var bounds = CheckClampBounds();
            var surgeRate = CheckSurgeRate();
            var scopeLines = CheckScopeWeights();

            watch.Stop();
            ProbeEnv.Check(watch.Elapsed.TotalSeconds < 1.0,
                $"探针耗时 {watch.Elapsed.TotalSeconds:0.000}s 超预算（< 1s）");

            Console.WriteLine($"    · 镜像 {AgentProbeKit.DesignContract.Version}：锚点与实现一致={mirror.Identical}" +
                              $"（内置默认 + 生产 json：{mirror.Source}）");
            foreach (var line in anchorLines) Console.WriteLine("    · " + line);
            Console.WriteLine($"    · clamp 端点：T=0 → {bounds.Zero}（期望 1）/ T=10⁹ → {bounds.Huge}（期望 120）");
            Console.WriteLine($"    · 激增触发率（T={SurgeTokenEff}，{SurgeSamples} 抽）= {surgeRate:0.000}（区间 [0.25,0.35]）");
            foreach (var line in scopeLines) Console.WriteLine("    · " + line);
            Console.WriteLine($"    · 点估校验 {pointEstimates} 档、seed 扫描 {SeedsPerAnchor * 6} 次，耗时 {watch.Elapsed.TotalSeconds:0.000}s");
        }

        // ── ⑤ 镜像 version 一致性 ──────────────────────────────────────

        sealed class MirrorCheck
        {
            public bool Identical;
            public string Source;
        }

        static MirrorCheck MirrorMatchesImplementation()
        {
            var contract = AgentProbeKit.DesignContract.Anchors;
            var builtin = KarmaCurveConfig.Default;

            CompareAnchors(contract, builtin, "内置默认 KarmaCurveConfig.Default");
            ProbeEnv.Equal(AgentProbeKit.DesignContract.FloorToken, builtin.FloorToken, "内置 floor_token");
            ProbeEnv.Check(builtin.ExtendLastSlope, "内置 extend_last_slope 应为 true（触顶 120 ≈ 25K 的前提）");
            ProbeEnv.Equal(1, builtin.Min, "内置 min");
            ProbeEnv.Equal(SingleMax, builtin.Max, "内置 max");
            CompareScopeWeights(ScopeWeightsConfig.Default, "内置默认 ScopeWeightsConfig.Default");

            // 生产配置文件（若存在）：与镜像逐值比对——这就是「镜像 version 与实现系数 version 一致」的落地判据。
            var source = "内置默认";
            var path = Path.Combine(ProbeEnv.RepoRoot, "Assets", "StreamingAssets", "Agent", AgentConfigLoader.ConfigFileName);
            if (File.Exists(path))
            {
                var loaded = AgentConfigLoader.Load(path);
                CompareAnchors(contract, loaded.KarmaCurve, "生产 agent_remake.json（" + path + "）");
                ProbeEnv.Equal(AgentProbeKit.DesignContract.FloorToken, loaded.KarmaCurve.FloorToken, "生产 floor_token");
                ProbeEnv.Check(loaded.KarmaCurve.ExtendLastSlope, "生产 extend_last_slope 应为 true");
                ProbeEnv.Equal(SingleMax, loaded.KarmaCurve.Max, "生产 max");
                CompareScopeWeights(loaded.ScopeWeights, "生产 agent_remake.json 的 scope_weights");
                ProbeEnv.Check(loaded.Warnings.Count == 0,
                    "生产配置加载出现回退告警（" + (loaded.Warnings.Count > 0 ? loaded.Warnings[0] : "") + "），校准参数已漂移");
                source = "内置 + 生产 json 一致";
            }

            return new MirrorCheck { Identical = true, Source = source };
        }

        /// <summary>
        /// ⑤b 作用域权重镜像一致性（E3-C）：内置默认与生产 json 都必须与探针字面量逐值相等。
        /// 权重是校准合同——漂移必须走 §3.6 治理（改镜像标签 + 双改），禁「先跑着」。
        /// </summary>
        static void CompareScopeWeights(ScopeWeightsConfig impl, string label)
        {
            var ids = AgentProbeKit.DesignContract.ScopeIds;
            var weights = AgentProbeKit.DesignContract.ScopeWeights;
            for (int i = 0; i < ids.Length; i++)
            {
                var actual = impl.Get(ids[i]);
                if (Math.Abs(actual - weights[i]) > 1e-12)
                    throw new Exception(
                        $"{label}：作用域 {ids[i]} 权重 {actual} 与镜像 {AgentProbeKit.DesignContract.Version} 的 {weights[i]} 不一致" +
                        " —— 按 QA §3.6 走重校准治理（镜像标签 + 生产 json 双改）");
            }
        }

        static void CompareAnchors(int[][] mirror, KarmaCurveConfig impl, string label)
        {
            ProbeEnv.Equal(mirror.Length, impl.Anchors.Count, label + " 锚点数量");
            for (int i = 0; i < mirror.Length; i++)
            {
                var a = impl.Anchors[i];
                if (a.Token != mirror[i][0] || a.Karma != mirror[i][1])
                    throw new Exception(
                        $"{label} 第 {i} 档锚点与镜像 {AgentProbeKit.DesignContract.Version} 不一致：" +
                        $"实现 ({a.Token},{a.Karma}) vs 镜像 ({mirror[i][0]},{mirror[i][1]})" +
                        " —— 按 QA §3.6 走重拟合治理（夹具 version+1 双改），禁「先跑着」");
            }
        }

        // ── ① 点估（roll = 1.0）────────────────────────────────────────

        static int CheckAnchorPointEstimates(List<string> lines)
        {
            var formula = AgentRemakeConfig.Default.CreateFormula();
            var ctx = new KarmaContext { SingleMax = SingleMax };
            var anchors = AgentProbeKit.DesignContract.Anchors;
            int checkedCount = 0;

            for (int i = 0; i < anchors.Length; i++)
            {
                var token = anchors[i][0];
                var target = anchors[i][1];
                var id = AgentProbeKit.DesignContract.Ids[i];

                var rng = FixedRandom.UnitRoll();
                var result = formula.Compute(token, writes: 1, success: true, ctx: ctx, rng: rng);

                if (Math.Abs(result.Roll - 1.0) > 1e-12)
                    throw new Exception($"{id} 档 roll 端点未命中 1.0（实际 {result.Roll:0.000000}，检查 FixedRandom 取值与激增阈值）");
                if (Math.Abs(result.CurveValue - target) > 1e-9)
                    throw new Exception($"{id}@{token}: f 曲线值 {result.CurveValue:0.000000} 未过锚点 {target}");
                if (Math.Abs(result.Karma - target) > 1)
                    throw new Exception($"{id}@{token}: 点估 {result.Karma} 偏离锚点 {target} 超过 ±1");

                var independent = AgentProbeKit.DesignContract.ExpectedKarma(token, 1.0, SingleMax);
                if (result.Karma != independent)
                    throw new Exception($"{id}@{token}: karma {result.Karma} ≠ 独立推导 {independent}");

                lines.Add($"{id.Trim()}@{token,-5} → karma {result.Karma,-3}（锚点 {target}，独立推导 {independent}，roll=1.0 命中 ±1）");
                checkedCount++;
            }
            return checkedCount;
        }

        // ── ② 32 seed 全波动扫描 ──────────────────────────────────────

        static void CheckAnchorSeedSweep(List<string> lines)
        {
            var formula = AgentRemakeConfig.Default.CreateFormula();
            var ctx = new KarmaContext { SingleMax = SingleMax };
            var anchors = AgentProbeKit.DesignContract.Anchors;

            for (int i = 0; i < anchors.Length; i++)
            {
                var token = anchors[i][0];
                var id = AgentProbeKit.DesignContract.Ids[i];
                var min = AgentProbeKit.DesignContract.RangeMin[i];
                var max = AgentProbeKit.DesignContract.RangeMax[i];

                int observedMin = int.MaxValue, observedMax = int.MinValue;
                for (int seed = 1; seed <= SeedsPerAnchor; seed++)
                {
                    var karma = formula.Compute(token, 1, true, ctx, new DeterministicRng(seed)).Karma;
                    if (karma < min || karma > max)
                        throw new Exception(
                            $"{id}@{token} seed={seed}: karma {karma} 超出设计区间 [{min},{max}]（锚点 {anchors[i][1]}）");
                    if (karma < observedMin) observedMin = karma;
                    if (karma > observedMax) observedMax = karma;
                }
                lines.Add($"{id.Trim()}@{token,-5} seed×{SeedsPerAnchor} 落 [{observedMin},{observedMax}] ⊂ 设计 [{min},{max}]");
            }
        }

        // ── ③ clamp 端点 ──────────────────────────────────────────────

        sealed class Bounds
        {
            public int Zero;
            public int Huge;
        }

        static Bounds CheckClampBounds()
        {
            var formula = AgentRemakeConfig.Default.CreateFormula();
            var ctx = new KarmaContext { SingleMax = SingleMax };

            // T=0：平地价区（f=1），任何 σ 都不该把它抬到 0 或 2
            var zero = formula.Compute(0, 1, true, ctx, FixedRandom.UnitRoll()).Karma;
            ProbeEnv.Equal(1, zero, "T=0 业力（下限 1，不是 0）");

            // 下界不受 σ 下沿影响：σ=0.8 时 round(1×0.8)=1，仍为 1
            var zeroLow = formula.Compute(0, 1, true, ctx, new FixedRandom(0.0)).Karma;
            ProbeEnv.Equal(1, zeroLow, "T=0 / σ=0.8 业力下界");

            var huge = formula.Compute(1000000000, 1, true, ctx, FixedRandom.UnitRoll()).Karma;
            ProbeEnv.Equal(120, huge, "T=10⁹ 业力（上界 120）");
            var hugeHigh = formula.Compute(1000000000, 1, true, ctx, new FixedRandom(1.0)).Karma;
            ProbeEnv.Equal(120, hugeHigh, "T=10⁹ / σ=1.3 业力上界");

            // 触顶点反查：f(T) ≥ 120 的最小 T 应落在 ≈25K（±10%），跨过即说明末段斜率漂移
            var capToken = formula.ProjectCapToken(120);
            ProbeEnv.Check(capToken >= 22500 && capToken <= 27500,
                $"触顶 token 反查 {capToken} 偏离设计侧 §1.5 的 ≈25K（±10%）");

            return new Bounds { Zero = zero, Huge = huge };
        }

        // ── ⑤c 作用域权重应用（E3-C） ─────────────────────────────────

        /// <summary>
        /// roll=1.0、T=8000（f=16）时逐作用域校验：
        /// ui 16 / pieces round(16×1.85)=30 / board_state round(16×2.9)=46 / rules round(16×3.35)=54；
        /// 以及 max 语义（ui+pieces+rules ⇒ 3.35，不相乘不相加）。
        /// 期望值由 <see cref="AgentProbeKit.DesignContract.ExpectedKarma"/> 独立重算。
        /// </summary>
        static List<string> CheckScopeWeights()
        {
            var lines = new List<string>();
            var formula = AgentRemakeConfig.Default.CreateFormula();
            var ids = AgentProbeKit.DesignContract.ScopeIds;

            for (int i = 0; i < ids.Length; i++)
            {
                var ctx = new KarmaContext { SingleMax = SingleMax, ScopeTags = new[] { ids[i] } };
                var result = formula.Compute(8000, writes: 1, success: true, ctx: ctx, rng: FixedRandom.UnitRoll());
                var expected = AgentProbeKit.DesignContract.ExpectedKarma(8000, 1.0, SingleMax, 0,
                    AgentProbeKit.DesignContract.ScopeWeightOf(ids[i]));
                if (result.Karma != expected)
                    throw new Exception($"作用域 {ids[i]}：karma {result.Karma} ≠ 独立推导 {expected}");
                if (Math.Abs(result.ScopeWeight - AgentProbeKit.DesignContract.ScopeWeightOf(ids[i])) > 1e-12)
                    throw new Exception($"作用域 {ids[i]}：结果 ScopeWeight {result.ScopeWeight} 与镜像不一致");
                lines.Add($"{ids[i],-11} ×{AgentProbeKit.DesignContract.ScopeWeightOf(ids[i]):0.00} @8000 → karma {result.Karma,-3}（独立推导 {expected}）");
            }

            var combined = new KarmaContext
            {
                SingleMax = SingleMax,
                ScopeTags = new[] { "ui_config", "pieces", "rules" },
            };
            var maxResult = formula.Compute(8000, 1, true, combined, FixedRandom.UnitRoll());
            ProbeEnv.Check(Math.Abs(maxResult.ScopeWeight - 3.35) < 1e-12,
                $"多作用域必须取 max（不是加和/连乘），实际 ×{maxResult.ScopeWeight}");
            ProbeEnv.Equal("rules", maxResult.DominantScope, "主导作用域应为 rules");
            lines.Add($"max(ui,pieces,rules)=×{maxResult.ScopeWeight:0.00} → karma {maxResult.Karma}（不与旧口径混算）");
            return lines;
        }

        // ── ④ 激增触发率 ──────────────────────────────────────────────
        static double CheckSurgeRate()
        {
            var formula = AgentRemakeConfig.Default.CreateFormula();
            var ctx = new KarmaContext { SingleMax = SingleMax };
            var rng = new DeterministicRng(424242);
            int surged = 0;
            int observedMax = int.MinValue;

            for (int i = 0; i < SurgeSamples; i++)
            {
                var result = formula.Compute(SurgeTokenEff, 1, true, ctx, rng);
                if (result.Surged) surged++;
                if (result.Karma > observedMax) observedMax = result.Karma;
                ProbeEnv.Check(result.Karma >= 73 && result.Karma <= 120,
                    $"激增样本 karma {result.Karma} 超出 C+ 区间 [73,120]");
            }

            var rate = (double)surged / SurgeSamples;
            ProbeEnv.Check(rate >= 0.25 && rate <= 0.35,
                $"激增触发率 {rate:0.000} 超出 [0.25,0.35]（p=0.30 ± 0.05；实际 max karma={observedMax}）");
            return rate;
        }
    }
}
