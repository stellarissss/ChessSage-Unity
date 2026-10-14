using System.Collections.Generic;

namespace ChessSage.Core.Overworld
{
    /// <summary>地标建筑形制。六道各有一种可辨识的剪影，作为关卡入口的视觉锚点。</summary>
    public enum LandmarkStyle : byte
    {
        SpawnGate = 0,      // 轮回门（出生点）
        HeavenTemple = 1,   // 天道·白塔梵宫
        HellGate = 2,       // 地狱道·焦黑门阙
        HungryPit = 3,      // 饿鬼道·枯骨窟
        AnimalLodge = 4,    // 畜生道·茅顶兽棚
        HumanPalace = 5,    // 人道·黄沙宫阙
        AsuraFort = 6,      // 阿修罗道·赤铁战堡
        SandboxArena = 7,   // 沙盒训练场
        NpcHut = 8,         // 菩提老者·草庐
        BillboardPost = 9,  // 告示牌
        Monument = 10,      // 成就殿堂·碑
    }

    /// <summary>一个地标实例。</summary>
    public struct Landmark
    {
        public int X, Y;
        public LandmarkStyle Style;
        public string Id;
        public string Label;
        public float Scale;   // 世界尺寸倍率

        public override string ToString() => $"{Id} {Style} @({X},{Y}) x{Scale:F2}";
    }

    /// <summary>
    /// 地标布点（纯逻辑）。
    ///
    /// 输入 POI 表，输出"在哪、放什么形制"。尺寸/朝向由风格决定，
    /// 使六道入口一眼可辨 —— 这正是原版缺少的"探索动机"：玩家需要看到
    /// 远处有座塔/有座门，才会想走过去。
    /// </summary>
    public static class LandmarkPlanner
    {
        public static List<Landmark> Plan(OverworldMap map)
        {
            var list = new List<Landmark>();
            foreach (var poi in map.Pois)
            {
                if (poi == null) continue;
                var style = StyleFor(poi);
                list.Add(new Landmark
                {
                    X = poi.X,
                    Y = poi.Y,
                    Style = style,
                    Id = poi.Id,
                    Label = string.IsNullOrEmpty(poi.Label) ? poi.Realm ?? poi.Id : poi.Label,
                    Scale = ScaleFor(style),
                });
            }
            return list;
        }

        public static LandmarkStyle StyleFor(Poi poi)
        {
            if (poi.Type == "realm")
            {
                switch (poi.Realm)
                {
                    case "heaven": return LandmarkStyle.HeavenTemple;
                    case "hell": return LandmarkStyle.HellGate;
                    case "hungry": return LandmarkStyle.HungryPit;
                    case "animal": return LandmarkStyle.AnimalLodge;
                    case "human": return LandmarkStyle.HumanPalace;
                    case "asura": return LandmarkStyle.AsuraFort;
                    default: return LandmarkStyle.SpawnGate;
                }
            }
            switch (poi.Type)
            {
                case "sandbox": return LandmarkStyle.SandboxArena;
                case "npc": return LandmarkStyle.NpcHut;
                case "billboard": return LandmarkStyle.BillboardPost;
                case "achievements": return LandmarkStyle.Monument;
                default: return LandmarkStyle.SpawnGate;
            }
        }

        /// <summary>六道入口体量最大（视觉锚点），辅助设施次级。</summary>
        public static float ScaleFor(LandmarkStyle style)
        {
            switch (style)
            {
                case LandmarkStyle.HeavenTemple:
                case LandmarkStyle.AsuraFort:
                case LandmarkStyle.HumanPalace:
                    return 2.6f;
                case LandmarkStyle.HellGate:
                case LandmarkStyle.HungryPit:
                case LandmarkStyle.AnimalLodge:
                    return 2.2f;
                case LandmarkStyle.SpawnGate:
                    return 1.9f;
                case LandmarkStyle.Monument:
                    return 1.6f;
                case LandmarkStyle.SandboxArena:
                    return 1.5f;
                default:
                    return 1.0f;
            }
        }
    }
}
