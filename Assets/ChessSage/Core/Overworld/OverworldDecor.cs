using System.Collections.Generic;

namespace ChessSage.Core.Overworld
{
    /// <summary>
    /// 装饰物种。<b>程序化绘制</b>（无外部美术依赖），每一种在 Presentation 层对应一族
    /// 差别明显的剪影，使同屏装饰不会"看起来是同一个图形复制粘贴"。
    /// </summary>
    public enum DecorKind : byte
    {
        BroadTree = 0,   // 阔叶树（密林/牧场）
        PineTree = 1,    // 针叶树（雪原/丘陵）
        DeadTree = 2,    // 枯树（荒原/沙漠）
        Bush = 3,        // 灌木
        Flower = 4,      // 花丛
        Reed = 5,        // 芦苇（水边）
        Rock = 6,        // 小石
        Boulder = 7,     // 巨岩
        Cactus = 8,      // 仙人掌（沙漠）
        Crystal = 9,     // 冰晶（雪原）
        RuinPillar = 10, // 断柱（地牢/遗迹）
        Banner = 11,     // 战旗（战场）
        Bone = 12,       // 兽骨（荒原/战场）
        Stump = 13,      // 树桩（密林采伐痕迹）
        Mushroom = 14,   // 蘑菇（密林/地牢）
        GrassTuft = 15,  // 草丛
        SnowMound = 16,  // 雪堆
        LavaRock = 17,   // 熔岩岩块
    }

    /// <summary>装饰物种的生态属性：决定它能长在什么地形上。</summary>
    public static class DecorSpecies
    {
        /// <summary>是否允许长在该地形上。地形门控是"视觉合理性"的底线——树不长在水里。</summary>
        public static bool AllowedOn(DecorKind k, TerrainKind t)
        {
            var g = TerrainGroups.Of(t);
            switch (k)
            {
                case DecorKind.Reed:
                    return t == TerrainKind.Shallow || t == TerrainKind.Beach || t == TerrainKind.Grass;
                case DecorKind.Cactus:
                    return g == TerrainGroup.Sand || t == TerrainKind.Grass || t == TerrainKind.Hill;
                case DecorKind.Crystal:
                case DecorKind.SnowMound:
                case DecorKind.PineTree:
                    return g == TerrainGroup.Snow || g == TerrainGroup.Rock || t == TerrainKind.Grass || t == TerrainKind.Hill;
                case DecorKind.LavaRock:
                    return g == TerrainGroup.Lava || g == TerrainGroup.Rock;
                case DecorKind.BroadTree:
                case DecorKind.Stump:
                case DecorKind.Mushroom:
                    return t == TerrainKind.Grass || t == TerrainKind.Hill;
                case DecorKind.Bush:
                case DecorKind.Flower:
                case DecorKind.GrassTuft:
                    return t == TerrainKind.Grass || t == TerrainKind.Hill || t == TerrainKind.Beach;
                case DecorKind.DeadTree:
                    return g == TerrainGroup.Sand || t == TerrainKind.Grass || t == TerrainKind.Hill
                           || t == TerrainKind.BareRock;
                case DecorKind.RuinPillar:
                case DecorKind.Bone:
                    return g == TerrainGroup.Sand || g == TerrainGroup.Rock
                           || t == TerrainKind.Grass || t == TerrainKind.BareRock;
                case DecorKind.Banner:
                    return t == TerrainKind.Grass || t == TerrainKind.Hill || t == TerrainKind.Beach;
                case DecorKind.Rock:
                case DecorKind.Boulder:
                    return g != TerrainGroup.Water && t != TerrainKind.Wall;
                default:
                    return false;
            }
        }
    }

    /// <summary>一个装饰实例（布点结果）。</summary>
    public struct DecorInstance
    {
        public int X, Y;
        public DecorKind Kind;
        public byte Variant;   // 图形变体（0..3）
        public byte ScalePct;  // 尺寸百分比（85..135），打破等大复制粘贴
        public bool FlipX;     // 水平翻转
        public byte TintSeed;  // 色调抖动参数
        public bool Solid;     // 是否阻挡（本版仅视觉，占位供后续碰撞）

        public override string ToString() =>
            $"({X},{Y}) {Kind} v{Variant} s{ScalePct} flip={FlipX}";
    }

    /// <summary>装饰规划结果统计（供测试与性能面板）。</summary>
    public sealed class DecorPlanStats
    {
        public int Total;
        public readonly Dictionary<DecorKind, int> PerKind = new Dictionary<DecorKind, int>();
        public readonly Dictionary<string, int> PerRegion = new Dictionary<string, int>();
    }

    /// <summary>
    /// 装饰布点规划器（纯逻辑，可单测）。
    ///
    /// 输入：地图 + 种子。输出：确定性的装饰实例列表。
    ///   · 每个区域按其 <see cref="DecorRegionSpec"/> 的密度决定 Poisson 最小间距；
    ///   · 物种池按区域主题/角色选取，并逐点做地形生态门控；
    ///   · 变体/缩放/翻转/色调由确定性 RNG 派生 —— 同一 seed 必得同一张地图。
    ///
    /// <b>为什么放在 Core</b>：布点规则（密度、间距、门控）是"玩法可见的规则"，
    /// 必须在无 Unity 的环境里可回归验证；渲染层只负责把实例画出来。
    /// </summary>
    public static class DecorPlanner
    {
        /// <summary>全图最小间距下限：防止密度过大导致同屏对象爆炸（软渲染硬约束）。</summary>
        const int MinDistFloor = 2;

        /// <summary>全图最小间距上限：沙漠这类极稀疏区域也要保证有零星点缀。</summary>
        const int MinDistCeil = 5;

        /// <summary>
        /// 密度增益。原版 <c>d_lo/d_hi</c> 是按"每格概率"标的，换算成 Poisson 最小间距后
        /// 实测偏稀（中央平原几乎看不到植被）。这里统一放大 1.6 倍，
        /// 让"空荡荡"的区域长出可见的点缀，同时仍远低于性能上限。
        /// </summary>
        const float DensityBoost = 1.6f;

        public static List<DecorInstance> Plan(OverworldMap map, int seed,
            DecorPlanStats stats = null)
        {
            var all = new List<DecorInstance>();
            var scratch = new List<DecorInstance>();

            foreach (var region in map.Regions)
            {
                if (region.Rect == null || region.Rect.Length < 4) continue;
                int x0 = System.Math.Max(0, region.Rect[0]);
                int y0 = System.Math.Max(0, region.Rect[1]);
                int x1 = System.Math.Min(map.Width - 1, region.Rect[2]);
                int y1 = System.Math.Min(map.Height - 1, region.Rect[3]);
                if (x1 <= x0 || y1 <= y0) continue;

                var spec = map.DecorSpecs.TryGetValue(region.Id ?? "", out var s) ? s : null;
                float density = spec != null ? (spec.DensityLo + spec.DensityHi) * 0.5f : 0.08f;
                density *= DensityBoost;

                // 期望"每格一个点"时密度=1；Poisson 的最小间距 ≈ 1/√密度。
                float rawDist = 1f / (float)System.Math.Sqrt(System.Math.Max(0.01f, density));
                int minDist = (int)System.Math.Round(rawDist) + 1;
                if (minDist < MinDistFloor) minDist = MinDistFloor;
                if (minDist > MinDistCeil) minDist = MinDistCeil;

                int regionSeed = seed ^ StableHash(region.Id);
                bool solidRegion = spec?.Solid ?? false;

                var points = PoissonDisk.Sample(x0, y0, x1, y1, minDist, regionSeed, null);
                var rng = new DeterministicRng32(regionSeed * 31 + 7);

                scratch.Clear();
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var terrain = map.GetTerrain(p.X, p.Y);
                    if (terrain == TerrainKind.Wall) continue;     // 外圈群山是边界，不撒装饰
                    if (map.IsWaterAt(p.X, p.Y)) continue;          // 深水不撒陆地装饰
                    if (map.InLake(p.X, p.Y)) continue;             // 湖泊中心也不撒

                    var kind = PickSpecies(region.Id, terrain, map, p.X, p.Y, ref rng);
                    if (kind == DecorKind.Reed && !map.NearWater(p.X, p.Y, 1))
                    {
                        // 芦苇必须临水，否则语义错乱；就近改判为草丛。
                        kind = DecorKind.GrassTuft;
                    }
                    if (!DecorSpecies.AllowedOn(kind, terrain)) continue;

                    scratch.Add(new DecorInstance
                    {
                        X = p.X,
                        Y = p.Y,
                        Kind = kind,
                        Variant = (byte)rng.Range(0, 4),
                        ScalePct = (byte)rng.Range(85, 136),
                        FlipX = rng.Chance(0.5f),
                        TintSeed = (byte)rng.Range(0, 256),
                        Solid = solidRegion && IsTree(kind),
                    });

                    if (stats != null)
                    {
                        stats.Total++;
                        stats.PerKind[kind] = stats.PerKind.TryGetValue(kind, out var c) ? c + 1 : 1;
                        stats.PerRegion[region.Id] = stats.PerRegion.TryGetValue(region.Id, out var rc) ? rc + 1 : 1;
                    }
                }

                all.AddRange(scratch);
            }

            // 稳定排序：渲染按 (y, x) 遍历时可连续取出同一块的装饰，避免每次重排。
            all.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            return all;
        }

        static bool IsTree(DecorKind k) =>
            k == DecorKind.BroadTree || k == DecorKind.PineTree || k == DecorKind.DeadTree;

        /// <summary>按区域角色选物种（带权重），并做地形二次筛选。</summary>
        static DecorKind PickSpecies(string regionId, TerrainKind terrain,
            OverworldMap map, int x, int y, ref DeterministicRng32 rng)
        {
            var pool = PoolFor(regionId);
            // 两次抽样取首个地形允许者；仍不允许则退化为小石/草丛。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                float total = 0f;
                for (int i = 0; i < pool.Length; i++) total += pool[i].W;
                float r = rng.Range(0f, total);
                for (int i = 0; i < pool.Length; i++)
                {
                    r -= pool[i].W;
                    if (r <= 0f)
                    {
                        var k = pool[i].Kind;
                        if (DecorSpecies.AllowedOn(k, terrain)) return k;
                        break;
                    }
                }
            }

            // 兜底：极夜/雪面等特殊地形给最贴合的小件，保证不空。
            var g = TerrainGroups.Of(terrain);
            if (g == TerrainGroup.Snow) return DecorKind.SnowMound;
            if (g == TerrainGroup.Lava) return DecorKind.LavaRock;
            return map.NearWater(x, y, 1) ? DecorKind.Reed : DecorKind.GrassTuft;
        }

        static (DecorKind Kind, float W)[] PoolFor(string regionId)
        {
            switch (regionId)
            {
                case Realm.NwForest:
                    return new[] { (DecorKind.BroadTree, 5f), (DecorKind.Bush, 2.5f), (DecorKind.Stump, 1f),
                                   (DecorKind.Mushroom, 1.2f), (DecorKind.Rock, 0.6f), (DecorKind.GrassTuft, 1f),
                                   (DecorKind.Reed, 0.8f) };
                case Realm.NSnow:
                    return new[] { (DecorKind.PineTree, 2.5f), (DecorKind.Crystal, 1.2f), (DecorKind.SnowMound, 2f),
                                   (DecorKind.Rock, 1.5f), (DecorKind.Boulder, 0.8f) };
                case Realm.NePasture:
                    return new[] { (DecorKind.GrassTuft, 3f), (DecorKind.Flower, 2.5f), (DecorKind.Bush, 1.5f),
                                   (DecorKind.BroadTree, 1.2f), (DecorKind.Rock, 0.6f) };
                case Realm.WWaste:
                    return new[] { (DecorKind.DeadTree, 2.5f), (DecorKind.Rock, 2f), (DecorKind.Boulder, 1.2f),
                                   (DecorKind.Bone, 0.8f), (DecorKind.GrassTuft, 0.8f) };
                case Realm.CPlain:
                    return new[] { (DecorKind.Flower, 2.5f), (DecorKind.GrassTuft, 2.5f), (DecorKind.Bush, 1.5f),
                                   (DecorKind.Rock, 0.8f), (DecorKind.BroadTree, 0.6f) };
                case Realm.EastRidge:
                    return new[] { (DecorKind.PineTree, 3f), (DecorKind.Rock, 2f), (DecorKind.Boulder, 1.2f),
                                   (DecorKind.Crystal, 0.8f), (DecorKind.GrassTuft, 1f) };
                case Realm.SwDungeon:
                    return new[] { (DecorKind.RuinPillar, 2.5f), (DecorKind.Bone, 1.5f), (DecorKind.Boulder, 1.5f),
                                   (DecorKind.Mushroom, 1.2f), (DecorKind.DeadTree, 0.8f) };
                case Realm.SDesert:
                    return new[] { (DecorKind.Cactus, 3f), (DecorKind.Rock, 2f), (DecorKind.Bone, 1f),
                                   (DecorKind.DeadTree, 0.8f) };
                case Realm.SeBattle:
                    return new[] { (DecorKind.Banner, 1.5f), (DecorKind.Boulder, 1.5f), (DecorKind.Rock, 1.5f),
                                   (DecorKind.DeadTree, 1.2f), (DecorKind.GrassTuft, 1f) };
                default:
                    return new[] { (DecorKind.GrassTuft, 2f), (DecorKind.Flower, 1.5f), (DecorKind.Rock, 1f) };
            }
        }

        /// <summary>字符串稳定散列（FNV-1a 变体），保证同一区域 id 恒得同一 seed。</summary>
        public static int StableHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }
}
