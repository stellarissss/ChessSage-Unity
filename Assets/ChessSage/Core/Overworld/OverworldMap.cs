using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ChessSage.Core.Util;

namespace ChessSage.Core.Overworld
{
    /// <summary>地形类别。数值与 legacy-web <c>configs/overworld.json</c> 的 <c>baked.terrain</c> 字码一致，
    /// 以保证 Unity 版与既有地图数据逐格兼容、无需重新生成。</summary>
    public enum TerrainKind : byte
    {
        DeepWater = 0,   // 深水
        Shallow = 1,     // 浅滩
        Beach = 2,       // 沙滩
        Grass = 3,       // 草地
        Hill = 4,        // 丘陵
        Mountain = 5,    // 山地
        Ridge = 6,       // 高岭
        Lava = 7,        // 岩浆
        Wall = 8,        // 外圈群山（地图边界，不可通行）
        Ice = 9,         // 冰面
        SaltLake = 10,   // 盐湖
        DryRiver = 11,   // 干河床
        BareRock = 12,   // 裸岩
    }

    /// <summary>九大区域 id。</summary>
    public static class Realm
    {
        public const string NwForest = "nw_forest";
        public const string NSnow = "n_snow";
        public const string NePasture = "ne_pasture";
        public const string WWaste = "w_waste";
        public const string CPlain = "c_plain";
        public const string EastRidge = "east_ridge";
        public const string SwDungeon = "sw_dungeon";
        public const string SDesert = "s_desert";
        public const string SeBattle = "se_battle";

        /// <summary>六道入口 → 区域 id 的映射（与 POI 的 realm 键一一对应）。</summary>
        public static string RealmToRegion(string realm) => realm switch
        {
            "hell" => CPlain,
            "hungry" => NwForest,
            "animal" => NePasture,
            "human" => SDesert,
            "asura" => SeBattle,
            "heaven" => NSnow,
            _ => CPlain,
        };
    }

    /// <summary>地图上的兴趣点。</summary>
    public sealed class Poi
    {
        public string Id;
        public string Type;      // spawn / realm / sandbox / npc / billboard / achievements
        public string Realm;     // 仅 realm 类型有值
        public int X, Y;
        public string Label;
        public string Emoji;
    }

    /// <summary>区域定义。</summary>
    public sealed class RegionInfo
    {
        public string Id;
        public string Name;
        public string Theme;
        public int[] Rect = new int[4];
        public int[] Center = new int[2];
        public int Radius;
        public double Weight;
        public string Color;
    }

    /// <summary>道路段（折线）。</summary>
    public struct RoadPath
    {
        public readonly string Id;
        public readonly int[] Points;   // 扁平化坐标 [x0,y0,x1,y1,...]
        public RoadPath(string id, int[] points) { Id = id; Points = points; }
    }

    /// <summary>
    /// 地形视觉分组。<b>分组而非逐一对比</b>是过渡渲染性能的关键：
    /// 13 种地形两两组合有 78 种边界，若按精确地形配对生成过渡面，图集会爆炸；
    /// 归并为 6 组后，只需判断"两侧是否同组"，边界数量骤降一个数量级。
    /// </summary>
    public enum TerrainGroup : byte
    {
        Water = 0,   // 深水 / 浅滩
        Sand = 1,    // 沙滩 / 盐湖 / 干河床
        Grass = 2,   // 草地 / 丘陵
        Forest = 3,  // （预留：密林类草地）
        Rock = 4,    // 山地 / 高岭 / 裸岩 / 外圈群山
        Snow = 5,    // 冰面
        Lava = 6,    // 岩浆
    }

    /// <summary>轴对齐的道路段。原版道路数据是「横段 / 竖段」两种，非自由折线。</summary>
    public struct RoadSegment
    {
        public readonly string Id;
        public readonly bool Horizontal;   // true: 沿 x 铺开(y 固定)；false: 沿 y 铺开(x 固定)
        public readonly int Axis;          // Horizontal 时为 y，否则为 x
        public readonly int From, To;      // 区间端点（含）
        public RoadSegment(string id, bool horizontal, int axis, int from, int to)
        { Id = id; Horizontal = horizontal; Axis = axis; From = from; To = to; }
    }

    /// <summary>河流：若干折线段（segments of polylines）。</summary>
    public sealed class RiverData
    {
        public string Id;
        public string Name;
        public bool Lava;
        public bool Dry;
        public readonly List<int[]> Segments = new List<int[]>();   // 每段扁平化 [x,y,x,y,...]
    }

    /// <summary>湖泊（圆形）。</summary>
    public sealed class LakeData
    {
        public string Id;
        public string Name;
        public int Cx, Cy, R;
        public string Kind;   // ice / fresh / oasis / lava
    }

    /// <summary>山脉脊线（用于绘制山脊与投影）。</summary>
    public sealed class MountainRangeData
    {
        public string Id;
        public string Name;
        public readonly List<int[]> Spine = new List<int[]>();   // [x,y] 点列
        public float Peak;
        public float Band;
    }

    /// <summary>桥梁。</summary>
    public sealed class BridgeData
    {
        public string Id;
        public string Name;
        public int X, Y;
        public string Type;   // stone / wood
    }

    /// <summary>
    /// 每个区域的装饰规格（来自 overworld.json 的 <c>decor_plant</c>）。
    /// 这是原版设计意图的直接载体：不同区域的植被密度与种类本就不同。
    /// </summary>
    public sealed class DecorRegionSpec
    {
        public string RegionId;
        public string Theme;          // farm / town / dungeon / battle
        public int[] Pool = System.Array.Empty<int>();
        public float DensityLo, DensityHi;
        public int RadiusLo, RadiusHi;
        public bool Solid;
    }

    /// <summary>地形分组映射：驱动过渡面的生成。</summary>
    public static class TerrainGroups
    {
        public static TerrainGroup Of(TerrainKind t) => t switch
        {
            TerrainKind.DeepWater => TerrainGroup.Water,
            TerrainKind.Shallow => TerrainGroup.Water,
            TerrainKind.Beach => TerrainGroup.Sand,
            TerrainKind.SaltLake => TerrainGroup.Sand,
            TerrainKind.DryRiver => TerrainGroup.Sand,
            TerrainKind.Grass => TerrainGroup.Grass,
            TerrainKind.Hill => TerrainGroup.Grass,
            TerrainKind.Mountain => TerrainGroup.Rock,
            TerrainKind.Ridge => TerrainGroup.Snow,
            TerrainKind.BareRock => TerrainGroup.Rock,
            TerrainKind.Wall => TerrainGroup.Rock,
            TerrainKind.Ice => TerrainGroup.Snow,
            TerrainKind.Lava => TerrainGroup.Lava,
            _ => TerrainGroup.Grass,
        };
    }

    /// <summary>
    /// 六道大陆地图数据（<b>单一真相源</b>）。
    ///
    /// 直接承载 legacy-web <c>configs/overworld.json</c> 的 <c>baked</c> 字段：
    /// <c>terrain</c> / <c>region</c> / <c>height</c> 三条按行优先（row-major）排列的字节串，
    /// 长度恒为 <c>width × height</c>。这样：
    ///   · 地图设计与迭代成果<b>不丢弃</b>，Unity 直接继承；
    ///   · 不引入 Python 依赖，运行时零生成开销；
    ///   · 数据是纯字节数组，解析后可做 O(1) 随机访问，非常适合流式加载与寻路。
    /// </summary>
    public sealed class OverworldMap
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int TileSize { get; private set; } = 16;
        public int Seed { get; private set; }
        public int PlayerSpeed { get; private set; } = 140;
        public float InteractTiles { get; private set; } = 3f;

        /// <summary>行优先地形码，长度 width*height。</summary>
        public byte[] Terrain { get; private set; } = Array.Empty<byte>();

        /// <summary>行优先区域索引，长度 width*height。</summary>
        public byte[] RegionIndex { get; private set; } = Array.Empty<byte>();

        /// <summary>行优先高程（0~255），长度 width*height。用于山体视觉高度与相机。</summary>
        public byte[] Elevation { get; private set; } = Array.Empty<byte>();

        public readonly List<RegionInfo> Regions = new List<RegionInfo>();
        public readonly List<Poi> Pois = new List<Poi>();
        public readonly List<RoadPath> Roads = new List<RoadPath>();

        // ── 富数据（决定性视觉分层的来源，全部继承自原版设计） ────────────────

        /// <summary>轴对齐道路段。</summary>
        public readonly List<RoadSegment> RoadSegments = new List<RoadSegment>();

        /// <summary>河流（含岩浆河/干河床）。</summary>
        public readonly List<RiverData> Rivers = new List<RiverData>();

        /// <summary>湖泊。</summary>
        public readonly List<LakeData> Lakes = new List<LakeData>();

        /// <summary>山脉脊线。</summary>
        public readonly List<MountainRangeData> Mountains = new List<MountainRangeData>();

        /// <summary>桥梁。</summary>
        public readonly List<BridgeData> Bridges = new List<BridgeData>();

        /// <summary>山口（成对端点，用于在地图上标出可穿越的隘口）。</summary>
        public readonly List<int[]> Passes = new List<int[]>();

        /// <summary>区域 id → 装饰规格。</summary>
        public readonly Dictionary<string, DecorRegionSpec> DecorSpecs = new Dictionary<string, DecorRegionSpec>();

        /// <summary>原版预烘的装饰实例：[x, y, theme, tileIndex, solid]。</summary>
        public readonly List<int[]> PrebakedDecor = new List<int[]>();

        public int[] PlayerSpawn = { 0, 0 };

        // ── 查询 ──────────────────────────────────────────────────────────────

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public TerrainKind GetTerrain(int x, int y)
            => InBounds(x, y) ? (TerrainKind)Terrain[y * Width + x] : TerrainKind.Wall;

        public byte GetHeight(int x, int y)
            => InBounds(x, y) ? Elevation[y * Width + x] : (byte)0;

        /// <summary>高程归一化到0~1，供渲染层使用。</summary>
        public float GetElevationSafe(int x, int y) => GetHeight(x, y) / 255f;

        /// <summary>该格所属区域索引；无定义时返回 -1。</summary>
        public int GetRegionIndex(int x, int y)
            => InBounds(x, y) ? RegionIndex[y * Width + x] : -1;

        public RegionInfo GetRegion(int x, int y)
        {
            int i = GetRegionIndex(x, y);
            return i >= 0 && i < Regions.Count ? Regions[i] : null;
        }

        /// <summary>该格所属区域的装饰规格（无则 null）。</summary>
        public DecorRegionSpec DecorSpecAt(int x, int y)
        {
            var r = GetRegion(x, y);
            if (r == null || r.Id == null) return null;
            return DecorSpecs.TryGetValue(r.Id, out var spec) ? spec : null;
        }

        /// <summary>该格是否在任意湖泊内（湖泊是正圆，O(湖泊数)=6，可直接算）。</summary>
        public bool InLake(int x, int y)
        {
            for (int i = 0; i < Lakes.Count; i++)
            {
                var l = Lakes[i];
                int dx = x - l.Cx, dy = y - l.Cy;
                if (dx * dx + dy * dy <= l.R * l.R) return true;
            }
            return false;
        }

        /// <summary>该格是否为水面地形。</summary>
        public bool IsWaterAt(int x, int y) => IsWater(GetTerrain(x, y));

        /// <summary>
        /// 该格是否邻近水面（切比雪夫距离 ≤ <paramref name="r"/>）。
        /// 用于"芦苇只长在水边""树木避开沼泽"这类生态门控，比统一撒点自然得多。
        /// </summary>
        public bool NearWater(int x, int y, int r)
        {
            if (IsWaterAt(x, y)) return true;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (IsWaterAt(x + dx, y + dy)) return true;
            return false;
        }

        /// <summary>
        /// 是否可通行。
        ///
        /// 严格遵循 web 版的既定结论：<b>只有外圈群山（Wall）阻挡</b>，水面/山体/岩浆一律可通行。
        /// 这是玩家友好的设计（避免大地图被障碍割裂成孤岛），也是 web 版踩坑后确定的口径。
        /// </summary>
        public bool IsWalkable(int x, int y) => InBounds(x, y) && GetTerrain(x, y) != TerrainKind.Wall;

        /// <summary>深水/浅滩之外的"水面"，用于禁止船行逻辑与相机模式判定。</summary>
        public static bool IsWater(TerrainKind t) => t == TerrainKind.DeepWater || t == TerrainKind.Shallow;

        /// <summary>山体类地形（影响移速与视野，不阻挡）。</summary>
        public static bool IsMountain(TerrainKind t)
            => t == TerrainKind.Mountain || t == TerrainKind.Ridge || t == TerrainKind.BareRock;

        public Poi FindPoi(string id)
        {
            for (int i = 0; i < Pois.Count; i++) if (Pois[i].Id == id) return Pois[i];
            return null;
        }

        /// <summary>全部六道入口 POI（供大地图 UI 快速定位）。</summary>
        public List<Poi> RealmPois()
        {
            var list = new List<Poi>();
            for (int i = 0; i < Pois.Count; i++) if (Pois[i].Type == "realm") list.Add(Pois[i]);
            return list;
        }

        // ── 加载 ──────────────────────────────────────────────────────────────

        public static OverworldMap FromJson(JObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var map = new OverworldMap();

            var world = root["world"] as JObject;
            if (world != null)
            {
                map.Width = world["width"]?.Value<int>() ?? 0;
                map.Height = world["height"]?.Value<int>() ?? 0;
                map.TileSize = world["tile"]?.Value<int>() ?? 16;
                map.Seed = world["seed"]?.Value<int>() ?? 0;
                map.PlayerSpeed = world["speed"]?.Value<int>() ?? 140;
            }

            var baked = root["baked"] as JObject
                        ?? throw new InvalidOperationException("overworld.json 缺少 baked 字段（单一真相源）");
            var size = baked["size"] as JArray;
            if (size != null && size.Count == 2)
            {
                map.Width = size[0].Value<int>();
                map.Height = size[1].Value<int>();
            }
            map.Terrain = ParseRowMajor(baked["terrain"]?.Value<string>(), map.Width, map.Height, "baked.terrain", true);
            map.RegionIndex = ParseRowMajor(baked["region"]?.Value<string>(), map.Width, map.Height, "baked.region", true);
            map.Elevation = ParseRowMajor(baked["height"]?.Value<string>(), map.Width, map.Height, "baked.height", false);

            foreach (var t in root["regions"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                map.Regions.Add(new RegionInfo
                {
                    Id = o["id"]?.Value<string>(),
                    Name = o["name"]?.Value<string>(),
                    Theme = o["theme"]?.Value<string>(),
                    Rect = Flat(o["rect"]),
                    Center = Flat(o["center"]),
                    Radius = o["radius"]?.Value<int>() ?? 0,
                    Weight = o["weight"]?.Value<double>() ?? 0.5,
                    Color = o["color"]?.Value<string>(),
                });
            }

            foreach (var t in root["pois"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                map.Pois.Add(new Poi
                {
                    Id = o["id"]?.Value<string>(),
                    Type = o["type"]?.Value<string>(),
                    Realm = o["realm"]?.Value<string>(),
                    X = o["x"]?.Value<int>() ?? 0,
                    Y = o["y"]?.Value<int>() ?? 0,
                    Label = o["label"]?.Value<string>() ?? "",
                    Emoji = o["emoji"]?.Value<string>() ?? "",
                });
            }

            // 道路：两种形态并存 —— 折线(points) 或 轴对齐段(x,y0,y1 / y,x0,x1)。
            foreach (var t in root["roads"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                var id = o["id"]?.Value<string>() ?? "road";
                if (o["points"] is JArray pts)
                {
                    map.Roads.Add(new RoadPath(id, Flat(pts) ?? Array.Empty<int>()));
                    continue;
                }
                if (o["x"] != null && o["y0"] != null && o["y1"] != null)
                    map.RoadSegments.Add(new RoadSegment(id, false,
                        o["x"].Value<int>(), o["y0"].Value<int>(), o["y1"].Value<int>()));
                else if (o["y"] != null && o["x0"] != null && o["x1"] != null)
                    map.RoadSegments.Add(new RoadSegment(id, true,
                        o["y"].Value<int>(), o["x0"].Value<int>(), o["x1"].Value<int>()));
            }

            // 河流：segs 为「折线数组的数组」，每段扁平化成 [x,y,x,y,...]。
            foreach (var prop in (root["rivers"] as JObject) ?? new JObject())
            {
                if (prop.Value is not JObject ro) continue;
                var river = new RiverData
                {
                    Id = prop.Key,
                    Name = ro["name"]?.Value<string>() ?? prop.Key,
                    Lava = ro["lava"]?.Value<bool>() ?? false,
                    Dry = ro["dry"]?.Value<bool>() ?? false,
                };
                foreach (var seg in ro["segs"] as JArray ?? new JArray())
                {
                    var flat = new List<int>();
                    foreach (var pt in seg as JArray ?? new JArray())
                    {
                        if (pt is JArray xy && xy.Count >= 2)
                        { flat.Add(xy[0].Value<int>()); flat.Add(xy[1].Value<int>()); }
                    }
                    if (flat.Count >= 4) river.Segments.Add(flat.ToArray());
                }
                map.Rivers.Add(river);
            }

            // 湖泊 / 山脉 / 桥 / 山口：结构简单，逐字段读取。
            foreach (var t in root["lakes"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                map.Lakes.Add(new LakeData
                {
                    Id = o["id"]?.Value<string>(),
                    Name = o["name"]?.Value<string>() ?? "",
                    Cx = o["cx"]?.Value<int>() ?? 0,
                    Cy = o["cy"]?.Value<int>() ?? 0,
                    R = o["r"]?.Value<int>() ?? 0,
                    Kind = o["kind"]?.Value<string>() ?? "fresh",
                });
            }

            foreach (var t in root["mountains"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                var mr = new MountainRangeData
                {
                    Id = o["id"]?.Value<string>(),
                    Name = o["name"]?.Value<string>() ?? "",
                    Peak = (float)(o["peak"]?.Value<double>() ?? 0.8),
                    Band = (float)(o["band"]?.Value<double>() ?? 4.0),
                };
                foreach (var sp in o["spine"] as JArray ?? new JArray())
                    if (sp is JArray xy && xy.Count >= 2)
                        mr.Spine.Add(new[] { xy[0].Value<int>(), xy[1].Value<int>() });
                map.Mountains.Add(mr);
            }

            foreach (var t in root["bridges"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                map.Bridges.Add(new BridgeData
                {
                    Id = o["id"]?.Value<string>(),
                    Name = o["name"]?.Value<string>() ?? "",
                    X = o["x"]?.Value<int>() ?? 0,
                    Y = o["y"]?.Value<int>() ?? 0,
                    Type = o["type"]?.Value<string>() ?? "stone",
                });
            }

            foreach (var t in root["passes"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                map.Passes.Add(new[] { o["x"]?.Value<int>() ?? 0, o["y"]?.Value<int>() ?? 0 });
            }

            // 区域装饰规格。
            foreach (var prop in (root["decor_plant"] as JObject) ?? new JObject())
            {
                if (prop.Value is not JObject o) continue;
                map.DecorSpecs[prop.Key] = new DecorRegionSpec
                {
                    RegionId = prop.Key,
                    Theme = o["theme"]?.Value<string>() ?? "farm",
                    Pool = Flat(o["pool"]) ?? Array.Empty<int>(),
                    DensityLo = (float)(o["d_lo"]?.Value<double>() ?? 0.1),
                    DensityHi = (float)(o["d_hi"]?.Value<double>() ?? 0.2),
                    RadiusLo = o["r_lo"]?.Value<int>() ?? 3,
                    RadiusHi = o["r_hi"]?.Value<int>() ?? 4,
                    Solid = o["solid"]?.Value<bool>() ?? false,
                };
            }

            // 预烘装饰（原版产物，作为"设计意图"参考与兜底；运行时以 Poisson 重排）。
            // 行结构为混合类型 [x:int, y:int, theme:string, tile:int, solid:bool]，不能走 Flat()。
            foreach (var t in root["decor"] as JArray ?? new JArray())
            {
                if (t is not JArray row || row.Count < 5) continue;
                map.PrebakedDecor.Add(new[]
                {
                    row[0].Value<int>(),
                    row[1].Value<int>(),
                    ThemeIndex(row[2]?.Value<string>()),
                    row[3].Value<int>(),
                    row[4].Value<bool>() ? 1 : 0,
                });
            }

            var player = root["player"] as JObject;
            var spawn = player?["spawn"] as JObject;
            map.PlayerSpawn = new[] { spawn?["x"]?.Value<int>() ?? 0, spawn?["y"]?.Value<int>() ?? 0 };
            map.InteractTiles = player?["interact_tiles"]?.Value<double>() is { } v ? (float)v : 3f;

            return map;
        }

        /// <summary>
        /// 解析"行优先 1 字节/格"的字符串。
        ///
        /// <b>两种编码并存</b>（实测 legacy-web 生成器的实际产物）：
        ///   · <paramref name="digitEncoded"/> = true：字符是<b>十进制数字字符</b>（'0'~'9'），
        ///     用于 terrain 与 region —— 便于人工检视与 diff；
        ///   · false：字符的 <b>Latin-1 码位即字节值</b>，用于 height ——
        ///     高程本质是连续量，用数字字符反而损失精度（0~255 无法用单字符表达）。
        ///
        /// 长度必须恰为 width×height，否则直接抛异常——地图数据与尺寸不匹配时
        /// 必须立刻失败，绝不能静默错位读取（那会导致整张地图错乱且极难排查）。
        /// </summary>
        static byte[] ParseRowMajor(string s, int w, int h, string field, bool digitEncoded)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
            int need = w * h;
            if (s.Length != need)
            {
                throw new InvalidOperationException(
                    $"{field} 长度应为 {need}（{w}×{h}），实际 {s.Length}。地图数据与 world.size 不匹配。");
            }
            var result = new byte[need];
            for (int i = 0; i < need; i++)
            {
                int v = s[i];
                if (digitEncoded)
                {
                    if (v < '0' || v > '9')
                        throw new InvalidOperationException(
                            $"{field} 第 {i} 格为非法数字字符 '{(char)v}'（序数 {i}）。");
                    v -= '0';
                }
                else if (v > 255)
                {
                    throw new InvalidOperationException(
                        $"{field} 第 {i} 格码位 {v} 超出单字节范围（高程应为 Latin-1 字节）。");
                }
                result[i] = (byte)v;
            }
            return result;
        }

        static int[] Flat(JToken t)
        {
            if (t is not JArray a) return null;
            var r = new int[a.Count];
            for (int i = 0; i < a.Count; i++) r[i] = a[i].Value<int>();
            return r;
        }

        /// <summary>主题名 → 紧凑索引（原版装饰行里的 theme 是字符串，统一成 int 便于 Core 处理）。</summary>
        static int ThemeIndex(string theme) => theme switch
        {
            "town" => 0,
            "farm" => 1,
            "dungeon" => 2,
            "battle" => 3,
            _ => 1,
        };
    }
}