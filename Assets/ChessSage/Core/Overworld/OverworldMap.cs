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

            foreach (var t in root["roads"] as JArray ?? new JArray())
            {
                if (t is not JObject o) continue;
                var pts = Flat(o["points"]) ?? Array.Empty<int>();
                map.Roads.Add(new RoadPath(o["id"]?.Value<string>() ?? "road", pts));
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
    }
}