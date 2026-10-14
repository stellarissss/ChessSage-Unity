using System.Collections.Generic;
using ChessSage.Core.Overworld;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace ChessSage.Tests
{
    /// <summary>
    /// 大地图"视觉丰富化"所依赖的<b>纯逻辑</b>回归：Poisson 采样、装饰布点、地标布点、
    /// 以及富数据（河流/湖泊/山脉/桥/道路）解析。
    ///
    /// 这些测试锁定的不是"好不好看"（那由截图取证），而是把"好看背后的规则"固化：
    ///   · 布点必须确定性（同种子同地图）——否则每次加载地图都不一样，无法回归；
    ///   · 装饰必须尊重地形生态（不把树种进水里）；
    ///   · 六道入口必须各有一种可辨识的地标形制。
    /// </summary>
    public sealed class OverworldRichnessTests
    {
        static OverworldMap Load()
        {
            var path = System.IO.Path.Combine(TestEnv.ConfigsRoot, "global", "overworld.json");
            Assert.IsTrue(System.IO.File.Exists(path), "缺少大地图配置：" + path);
            return OverworldMap.FromJson(JObject.Parse(System.IO.File.ReadAllText(path)));
        }

        // ── 确定性随机源 ─────────────────────────────────────────────────────

        [Test]
        public void Rng_SameSeedProducesSameSequence()
        {
            var a = new DeterministicRng32(12345);
            var b = new DeterministicRng32(12345);
            for (int i = 0; i < 64; i++)
                Assert.AreEqual(a.NextUInt(), b.NextUInt(), "同种子序列必须逐位一致");
        }

        [Test]
        public void Rng_DifferentSeedDiverges()
        {
            var a = new DeterministicRng32(1);
            var b = new DeterministicRng32(2);
            int same = 0;
            for (int i = 0; i < 64; i++) if (a.NextUInt() == b.NextUInt()) same++;
            Assert.Less(same, 4, "不同种子不应大体一致");
        }

        [Test]
        public void Rng_ZeroSeedIsNotDegenerate()
        {
            var r = new DeterministicRng32(0);
            var vals = new HashSet<uint>();
            for (int i = 0; i < 32; i++) vals.Add(r.NextUInt());
            Assert.Greater(vals.Count, 20, "种子 0 也必须产生丰富序列（不能恒 0）");
        }

        // ── Poisson-disk ─────────────────────────────────────────────────────

        [Test]
        public void Poisson_RespectsMinDistance()
        {
            var pts = PoissonDisk.Sample(0, 0, 63, 63, 5f, 42);
            Assert.Greater(pts.Count, 20, "64×64 区域、间距 5 应产出足够多的点");
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    float dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                    Assert.GreaterOrEqual(dx * dx + dy * dy, 25f - 1e-3f,
                        $"{pts[i]} 与 {pts[j]} 距离小于最小间距");
                }
        }

        [Test]
        public void Poisson_IsDeterministic()
        {
            var a = PoissonDisk.Sample(0, 0, 99, 79, 3f, 7);
            var b = PoissonDisk.Sample(0, 0, 99, 79, 3f, 7);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++) Assert.AreEqual(a[i].ToString(), b[i].ToString());
        }

        [Test]
        public void Poisson_HonoursPredicate()
        {
            // 只接受 x+y 为偶数的格 —— 所有点必须满足该条件。
            var pts = PoissonDisk.Sample(0, 0, 40, 40, 3f, 11, (x, y) => (x + y) % 2 == 0);
            Assert.Greater(pts.Count, 0);
            foreach (var p in pts) Assert.AreEqual(0, (p.X + p.Y) % 2, $"{p} 违反谓词");
        }

        [Test]
        public void Poisson_EmptyWhenPredicateAlwaysFalse()
        {
            var pts = PoissonDisk.Sample(0, 0, 40, 40, 3f, 11, (x, y) => false);
            Assert.AreEqual(0, pts.Count);
        }

        // ── 富数据解析 ───────────────────────────────────────────────────────

        [Test]
        public void Map_ParsesRichGeography()
        {
            var m = Load();
            Assert.AreEqual(5, m.Rivers.Count, "应有 5 条河流");
            Assert.AreEqual(6, m.Lakes.Count, "应有 6 个湖泊");
            Assert.AreEqual(7, m.Mountains.Count, "应有 7 条山脉");
            Assert.AreEqual(7, m.Bridges.Count, "应有 7 座桥");
            Assert.AreEqual(9, m.Passes.Count, "应有 9 个山口");
            Assert.AreEqual(9, m.DecorSpecs.Count, "九个区域都应带装饰规格");
            Assert.AreEqual(312, m.PrebakedDecor.Count, "原版预烘装饰应为 312 条");
        }

        [Test]
        public void Map_RoadSegmentsParsedAsAxisAligned()
        {
            var m = Load();
            Assert.Greater(m.RoadSegments.Count, 100, "道路段应被解析成轴对齐段（原 144 条）");
            foreach (var s in m.RoadSegments)
            {
                Assert.IsTrue(m.InBounds(s.Horizontal ? s.From : s.Axis, s.Horizontal ? s.Axis : s.From),
                    "路段端点应在图内");
                Assert.LessOrEqual(s.From, s.To, "路段区间应有序");
            }
        }

        [Test]
        public void Map_RiverSegmentsAreValidPolylines()
        {
            var m = Load();
            foreach (var r in m.Rivers)
            {
                Assert.IsNotEmpty(r.Segments, $"河流 {r.Id} 应有折线段");
                foreach (var seg in r.Segments)
                {
                    Assert.GreaterOrEqual(seg.Length, 4);
                    Assert.AreEqual(0, seg.Length % 2, "折线坐标应为偶数个（x,y 成对）");
                }
            }
        }

        [Test]
        public void Map_LakesAndMountainsHaveSaneGeometry()
        {
            var m = Load();
            foreach (var l in m.Lakes)
            {
                Assert.Greater(l.R, 0);
                Assert.IsTrue(m.InBounds(l.Cx, l.Cy), $"湖泊 {l.Id} 圆心应在地图内");
            }
            foreach (var mr in m.Mountains)
                Assert.GreaterOrEqual(mr.Spine.Count, 2, $"山脉 {mr.Id} 脊线至少 2 点");
        }

        // ── 装饰布点 ─────────────────────────────────────────────────────────

        [Test]
        public void Decor_IsDeterministicForSameSeed()
        {
            var m = Load();
            var a = DecorPlanner.Plan(m, 20260828);
            var b = DecorPlanner.Plan(m, 20260828);
            Assert.AreEqual(a.Count, b.Count, "同种子装饰数量必须一致");
            for (int i = 0; i < a.Count; i++)
                Assert.AreEqual(a[i].ToString(), b[i].ToString(), $"第 {i} 个装饰不一致");
        }

        [Test]
        public void Decor_DifferentSeedChangesLayout()
        {
            var m = Load();
            var a = DecorPlanner.Plan(m, 1);
            var b = DecorPlanner.Plan(m, 2);
            int diff = 0;
            int n = System.Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++) if (a[i].ToString() != b[i].ToString()) diff++;
            Assert.Greater(diff, n / 4, "不同种子应产生明显不同的布局");
        }

        [Test]
        public void Decor_NeverPlacedOnWaterOrWall()
        {
            var m = Load();
            var plan = DecorPlanner.Plan(m, 99);
            Assert.Greater(plan.Count, 200, "全图应有可观的装饰数量");
            foreach (var d in plan)
            {
                var t = m.GetTerrain(d.X, d.Y);
                Assert.AreNotEqual(TerrainKind.Wall, t, $"装饰不应落在边界群山上 {d}");
                Assert.IsFalse(OverworldMap.IsWater(t), $"装饰不应落在水面上 {d}");
                Assert.IsTrue(DecorSpecies.AllowedOn(d.Kind, t),
                    $"物种 {d.Kind} 与地形 {t} 不匹配 {d}");
            }
        }

        [Test]
        public void Decor_CoversMultipleRegionsAndSpecies()
        {
            var m = Load();
            var stats = new DecorPlanStats();
            DecorPlanner.Plan(m, 20260828, stats);
            Assert.Greater(stats.Total, 300);
            Assert.GreaterOrEqual(stats.PerRegion.Count, 8, "绝大多数区域都应有装饰");
            Assert.GreaterOrEqual(stats.PerKind.Count, 8, "应出现多种物种，避免单调");
        }

        [Test]
        public void Decor_ScaleAndVariantBreakUniformity()
        {
            var m = Load();
            var plan = DecorPlanner.Plan(m, 5);
            var scales = new HashSet<byte>();
            var variants = new HashSet<byte>();
            bool anyFlip = false;
            foreach (var d in plan)
            {
                scales.Add(d.ScalePct);
                variants.Add(d.Variant);
                anyFlip |= d.FlipX;
            }
            Assert.Greater(scales.Count, 8, "缩放应有变化（避免等大复制粘贴）");
            Assert.Greater(variants.Count, 2, "应有多个图形变体");
            Assert.IsTrue(anyFlip, "应有翻转样本");
        }

        [Test]
        public void Decor_TintSeedVaries()
        {
            var m = Load();
            var plan = DecorPlanner.Plan(m, 7);
            var tints = new HashSet<byte>();
            foreach (var d in plan) tints.Add(d.TintSeed);
            Assert.Greater(tints.Count, 16, "色调抖动参数应有足够变化");
        }

        // ── 地标 ─────────────────────────────────────────────────────────────

        [Test]
        public void Landmarks_OnePerPoiWithDistinctRealmStyles()
        {
            var m = Load();
            var lm = LandmarkPlanner.Plan(m);
            Assert.AreEqual(m.Pois.Count, lm.Count, "每个 POI 一个地标");

            var realmStyles = new HashSet<LandmarkStyle>();
            foreach (var p in m.RealmPois())
                realmStyles.Add(LandmarkPlanner.StyleFor(p));
            Assert.AreEqual(6, realmStyles.Count, "六道入口应有 6 种互不相同的形制");
            Assert.IsFalse(realmStyles.Contains(LandmarkStyle.SpawnGate), "六道不应退化为出生门形制");
        }

        [Test]
        public void Landmarks_BiggerForRealmEntrances()
        {
            var m = Load();
            foreach (var p in m.RealmPois())
            {
                var style = LandmarkPlanner.StyleFor(p);
                Assert.GreaterOrEqual(LandmarkPlanner.ScaleFor(style), 2.0f,
                    "六道入口体量应显著大于辅助设施");
            }
            Assert.Less(LandmarkPlanner.ScaleFor(LandmarkStyle.BillboardPost),
                LandmarkPlanner.ScaleFor(LandmarkStyle.HeavenTemple),
                "告示牌应小于天宫");
        }

        // ── 地形分组（过渡渲染的依据） ───────────────────────────────────────

        [Test]
        public void TerrainGroups_AreStableAndTotal()
        {
            foreach (TerrainKind t in System.Enum.GetValues(typeof(TerrainKind)))
            {
                // 每个地形都必须能落到一个分组（不应抛异常/落空）。
                var g = TerrainGroups.Of(t);
                Assert.IsTrue(System.Enum.IsDefined(typeof(TerrainGroup), g), $"地形 {t} 未分组");
            }
            Assert.AreEqual(TerrainGroup.Water, TerrainGroups.Of(TerrainKind.DeepWater));
            Assert.AreEqual(TerrainGroup.Snow, TerrainGroups.Of(TerrainKind.Ice));
            Assert.AreEqual(TerrainGroup.Rock, TerrainGroups.Of(TerrainKind.Wall));
            Assert.AreEqual(TerrainGroup.Grass, TerrainGroups.Of(TerrainKind.Grass));
        }
    }
}
