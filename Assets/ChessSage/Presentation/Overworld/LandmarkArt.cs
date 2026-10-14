using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 程序化地标建筑图集：11 种形制，每种一张 128×128 精灵（底部为锚点）。
    ///
    /// 【为什么地标是"探索动机"的关键】上一版大地图里六道入口只是一枚小菱形图标，
    /// 玩家看不出"那边有什么"。给每个入口一座<b>体量明显、剪影独特</b>的建筑，
    /// 远处一眼就能认出"那边是天道白塔 / 阿修罗赤堡"，才形成"走过去看看"的驱动。
    ///
    /// 全部程序化绘制：白塔的金顶、地牢的断柱、战场的赤堡……用最少的图元做出可辨识剪影。
    /// </summary>
    public static class LandmarkArt
    {
        public const int Cell = 128;
        const int Cols = 4;
        const int Rows = 3;
        const int Styles = 11;

        static Texture2D _tex;
        static Sprite[] _sprites;

        // 常用配色
        static readonly Color32 Stone = new Color32(150, 146, 138, 255);
        static readonly Color32 StoneDark = new Color32(104, 100, 94, 255);
        static readonly Color32 Gold = new Color32(214, 176, 84, 255);
        static readonly Color32 White = new Color32(238, 240, 244, 255);
        static readonly Color32 Red = new Color32(158, 56, 48, 255);
        static readonly Color32 DarkRock = new Color32(52, 46, 52, 255);
        static readonly Color32 Orange = new Color32(232, 128, 44, 255);
        static readonly Color32 Thatch = new Color32(176, 146, 92, 255);
        static readonly Color32 Wood = new Color32(112, 82, 52, 255);
        static readonly Color32 Sand = new Color32(224, 198, 142, 255);
        static readonly Color32 Bone = new Color32(228, 224, 206, 255);
        static readonly Color32 Cyan = new Color32(150, 214, 232, 255);

        static Sprite _bridge;

        /// <summary>桥：中性木色纹理，运行时按 stone/wood 染色。锚点在中心（平铺在河面上）。</summary>
        public static Sprite Bridge()
        {
            if (_bridge != null) return _bridge;
            const int w = 72, h = 36;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "BridgeSprite",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[w * h];
            ProceduralPaint.FillRect(px, w, 0, 0, w, h, 4, 12, 68, 28, new Color32(196, 190, 176, 255));
            for (int i = 0; i < 7; i++)   // 横向板缝
                ProceduralPaint.FillRect(px, w, 0, 0, w, h, 6, 12 + i * 2, 66, 13 + i * 2, new Color32(150, 144, 130, 255));
            ProceduralPaint.FillRect(px, w, 0, 0, w, h, 4, 10, 8, 30, new Color32(120, 114, 100, 255));
            ProceduralPaint.FillRect(px, w, 0, 0, w, h, 64, 10, 68, 30, new Color32(120, 114, 100, 255));
            tex.SetPixels32(px);
            tex.Apply(false, false);
            _bridge = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _bridge.name = "bridge";
            return _bridge;
        }

        public static Texture2D Texture
        {
            get { if (_tex == null) Build(); return _tex; }
        }

        public static Sprite Get(LandmarkStyle style)
        {
            if (_tex == null) Build();
            int i = (int)style;
            if (i < 0 || i >= Styles) i = 0;
            return _sprites[i];
        }

        static void Build()
        {
            _tex = new Texture2D(Cols * Cell, Rows * Cell, TextureFormat.RGBA32, mipChain: false)
            {
                name = "LandmarkAtlas_Procedural",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _sprites = new Sprite[Styles];

            int aw = Cols * Cell;
            var px = new Color32[aw * Rows * Cell];

            for (int i = 0; i < Styles; i++)
            {
                int col = i % Cols, row = i / Cols;
                int ox = col * Cell, oy = row * Cell;
                Draw(px, aw, ox, oy, (LandmarkStyle)i);
                _sprites[i] = Sprite.Create(_tex, new Rect(ox, oy, Cell, Cell),
                    new Vector2(0.5f, 0.06f), 100f, 0, SpriteMeshType.FullRect);
            }

            _tex.SetPixels32(px);
            _tex.Apply(false, false);
        }

        static void Draw(Color32[] px, int aw, int ox, int oy, LandmarkStyle style)
        {
            switch (style)
            {
                case LandmarkStyle.SpawnGate:          // 轮回门：石拱
                    R(px, aw, ox, oy, 26, 10, 42, 74, Stone);
                    R(px, aw, ox, oy, 86, 10, 102, 74, Stone);
                    R(px, aw, ox, oy, 24, 72, 104, 88, StoneDark);
                    R(px, aw, ox, oy, 30, 60, 98, 70, new Color32(80, 120, 160, 255));
                    R(px, aw, ox, oy, 60, 88, 68, 100, Gold);
                    break;

                case LandmarkStyle.HeavenTemple:       // 天道·白塔梵宫
                    R(px, aw, ox, oy, 34, 10, 94, 34, White);
                    R(px, aw, ox, oy, 40, 34, 88, 56, White);
                    T(px, aw, ox, oy, 64, 80, 40, Gold);
                    R(px, aw, ox, oy, 46, 56, 82, 74, White);
                    T(px, aw, ox, oy, 64, 100, 32, Gold);
                    R(px, aw, ox, oy, 58, 74, 70, 94, White);
                    T(px, aw, ox, oy, 64, 118, 20, Gold);
                    C(px, aw, ox, oy, 64, 120, 5, Color32Of(255, 250, 200));
                    break;

                case LandmarkStyle.HellGate:           // 地狱道·焦黑门阙
                    R(px, aw, ox, oy, 22, 10, 40, 84, DarkRock);
                    R(px, aw, ox, oy, 88, 10, 106, 84, DarkRock);
                    R(px, aw, ox, oy, 22, 84, 106, 100, DarkRock);
                    T(px, aw, ox, oy, 36, 100, 12, DarkRock);
                    T(px, aw, ox, oy, 92, 100, 12, DarkRock);
                    R(px, aw, ox, oy, 44, 10, 84, 78, new Color32(120, 40, 22, 255));
                    R(px, aw, ox, oy, 52, 20, 76, 62, Orange);
                    R(px, aw, ox, oy, 58, 30, 70, 50, new Color32(255, 214, 120, 255));
                    break;

                case LandmarkStyle.HungryPit:          // 饿鬼道·枯骨窟
                    C(px, aw, ox, oy, 64, 34, 40, new Color32(74, 66, 80, 255));
                    C(px, aw, ox, oy, 64, 34, 30, new Color32(36, 30, 42, 255));
                    C(px, aw, ox, oy, 40, 22, 8, Bone);
                    C(px, aw, ox, oy, 88, 24, 8, Bone);
                    R(px, aw, ox, oy, 52, 40, 76, 62, Bone);      // 立骨
                    C(px, aw, ox, oy, 52, 62, 6, Bone);
                    C(px, aw, ox, oy, 76, 62, 6, Bone);
                    break;

                case LandmarkStyle.AnimalLodge:        // 畜生道·茅顶兽棚
                    R(px, aw, ox, oy, 30, 10, 98, 46, Wood);
                    T(px, aw, ox, oy, 64, 92, 48, Thatch);
                    T(px, aw, ox, oy, 64, 78, 38, Lighter(Thatch, 0.12f));
                    R(px, aw, ox, oy, 54, 10, 74, 42, new Color32(48, 36, 26, 255));
                    break;

                case LandmarkStyle.HumanPalace:        // 人道·黄沙宫阙
                    R(px, aw, ox, oy, 26, 10, 102, 40, Sand);
                    C(px, aw, ox, oy, 64, 40, 30, Lighter(Sand, 0.10f));
                    R(px, aw, ox, oy, 22, 30, 36, 96, Sand);
                    R(px, aw, ox, oy, 92, 30, 106, 96, Sand);
                    T(px, aw, ox, oy, 29, 108, 12, Gold);
                    T(px, aw, ox, oy, 99, 108, 12, Gold);
                    R(px, aw, ox, oy, 56, 10, 72, 34, new Color32(120, 90, 50, 255));
                    break;

                case LandmarkStyle.AsuraFort:          // 阿修罗道·赤铁战堡
                    R(px, aw, ox, oy, 22, 10, 106, 60, Red);
                    R(px, aw, ox, oy, 22, 60, 106, 70, Darker(Red, 0.25f));
                    // 垛口
                    for (int i = 0; i < 5; i++) R(px, aw, ox, oy, 24 + i * 17, 70, 34 + i * 17, 82, Red);
                    R(px, aw, ox, oy, 28, 30, 44, 52, DarkRock);
                    R(px, aw, ox, oy, 84, 30, 100, 52, DarkRock);
                    R(px, aw, ox, oy, 58, 82, 62, 122, Wood);
                    T(px, aw, ox, oy, 68, 120, 16, new Color32(200, 60, 50, 255));
                    break;

                case LandmarkStyle.SandboxArena:       // 沙盒训练场
                    C(px, aw, ox, oy, 64, 30, 44, new Color32(196, 168, 118, 255));
                    C(px, aw, ox, oy, 64, 30, 36, new Color32(226, 206, 158, 255));
                    for (int i = 0; i < 10; i++)
                    {
                        float a = i / 10f * 6.2831853f;
                        int x = Mathf.RoundToInt(64 + Mathf.Cos(a) * 40);
                        int y = Mathf.RoundToInt(30 + Mathf.Sin(a) * 40 * 0.5f);
                        R(px, aw, ox, oy, x - 2, y, x + 2, y + 22, Wood);
                    }
                    break;

                case LandmarkStyle.NpcHut:             // 菩提老者·草庐
                    R(px, aw, ox, oy, 40, 10, 88, 40, Wood);
                    T(px, aw, ox, oy, 64, 78, 42, Thatch);
                    R(px, aw, ox, oy, 58, 10, 70, 34, new Color32(46, 34, 24, 255));
                    break;

                case LandmarkStyle.BillboardPost:      // 告示牌
                    R(px, aw, ox, oy, 56, 10, 72, 42, Wood);
                    R(px, aw, ox, oy, 34, 42, 94, 76, new Color32(150, 112, 70, 255));
                    R(px, aw, ox, oy, 40, 62, 88, 68, Color32Of(90, 70, 46));
                    R(px, aw, ox, oy, 40, 52, 78, 58, Color32Of(90, 70, 46));
                    break;

                case LandmarkStyle.Monument:           // 成就殿堂·碑
                    R(px, aw, ox, oy, 34, 10, 94, 26, StoneDark);
                    Poly(px, aw, ox, oy, new[] { (50, 26), (78, 26), (70, 108), (58, 108) }, Stone);
                    C(px, aw, ox, oy, 64, 82, 8, Gold);
                    R(px, aw, ox, oy, 60, 40, 68, 64, Darker(Stone, 0.2f));
                    break;
            }
        }

        static void R(Color32[] px, int aw, int ox, int oy, int x0, int y0, int x1, int y1, Color32 c)
            => ProceduralPaint.FillRect(px, aw, ox, oy, Cell, Cell, x0, y0, x1, y1, c);

        static void C(Color32[] px, int aw, int ox, int oy, float cx, float cy, float r, Color32 c)
            => ProceduralPaint.FillCircle(px, aw, ox, oy, Cell, Cell, cx, cy, r, c);

        static void T(Color32[] px, int aw, int ox, int oy, float cx, float apexY, float halfW, Color32 c)
            => ProceduralPaint.Tri(px, aw, ox, oy, Cell, Cell, cx, apexY, halfW, c);

        static void Poly(Color32[] px, int aw, int ox, int oy, (int x, int y)[] pts, Color32 c)
            => ProceduralPaint.Poly(px, aw, ox, oy, Cell, Cell, pts, c);

        static Color32 Lighter(Color32 c, float t) => ProceduralPaint.Lighter(c, t);
        static Color32 Darker(Color32 c, float t) => ProceduralPaint.Darker(c, t);
        static Color32 Color32Of(byte r, byte g, byte b) => new Color32(r, g, b, 255);
    }
}
