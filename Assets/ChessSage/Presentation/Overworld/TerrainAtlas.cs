using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 程序化生成等距地形图集（Texture2D）。
    ///
    /// 正式美术资源到位前，用代码生成 13 种地形的菱形瓦片（含山体顶面/两个侧面），
    /// 使大地图<b>开箱即有画面</b>，而不是一块纯色 —— 这是"质量优先"底线之一。
    ///
    /// 图集布局：每种地形占 1 个 96×96 单元；山体需要 3 个面（顶 + 左 + 右），
    /// 因此图集高度 = 13 行 × 96，行内放 3 列 96（顶/左/右，未用面留空）。
    /// 共13×3 = 39 个面，图集尺寸 288 × 1248，显存占用约 1.4 MB，可接受。
    ///
    /// 生成一次后静态缓存；贴图设置为 Point 过滤（像素风锐利）+ 无 mipmap。
    /// </summary>
    public static class TerrainAtlas
    {
        public const int Cell = 96;
        public const int Columns = 3;      // 顶面 / 左面 / 右面
        public const int TerrainKinds = 13;
        public const int AtlasWidth = Cell * Columns;
        public const int AtlasHeight = Cell * TerrainKinds;

        static Texture2D _texture;
        static Sprite[] _top = new Sprite[TerrainKinds];
        static Sprite[] _left = new Sprite[TerrainKinds];
        static Sprite[] _right = new Sprite[TerrainKinds];

        public static Texture2D Texture
        {
            get { if (_texture == null) Build(); return _texture; }
        }

        public static Sprite Top(TerrainKind t)
        {
            if (_texture == null) Build();
            return _top[(int)t];
        }

        public static Sprite Left(TerrainKind t)
        {
            if (_texture == null) Build();
            return _left[(int)t];
        }

        public static Sprite Right(TerrainKind t)
        {
            if (_texture == null) Build();
            return _right[(int)t];
        }

        static void Build()
        {
            _texture = new Texture2D(AtlasWidth, AtlasHeight, TextureFormat.RGBA32, mipChain: false)
            {
                name = "TerrainAtlas_Procedural",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[AtlasWidth * AtlasHeight];
            var clear = new Color32(0, 0, 0, 0);

            for (int t = 0; t < TerrainKinds; t++)
            {
                var kind = (TerrainKind)t;
                bool raised = TerrainPalette.IsRaised(kind);

                // 行索引倒置：Unity 纹理原点在左下，TileMap 习惯 y 向下，故按 (kinds-1-t) 排布。
                int row = TerrainKinds - 1 - t;

                DrawDiamond(pixels, 0, row, TerrainPalette.Get(kind));
                DrawDiamond(pixels, 1, row, raised ? TerrainPalette.SideFace(kind) : TerrainPalette.Get(kind));
                DrawDiamond(pixels, 2, row, raised ? TerrainPalette.SideFace(kind) : TerrainPalette.Get(kind));

                // 顶面细节：雪峰加高光斑，草地加轻微噪声，避免大面积纯色显得"塑料"。
                if (TerrainPalette.IsSnowCap(kind)) Speckle(pixels, 0, row, Color.white, 220);
                else if (kind == TerrainKind.Grass) Speckle(pixels, 0, row, Color.Lerp(TerrainPalette.Get(kind), Color.black, 0.12f), 90);
                else if (kind == TerrainKind.Lava) Speckle(pixels, 0, row, new Color(1f, 0.75f, 0.25f), 130);

                _top[t] = MakeSprite(0, row);
                _left[t] = MakeSprite(1, row);
                _right[t] = MakeSprite(2, row);
            }

            _texture.SetPixels32(pixels);
            _texture.Apply(false, false);
        }

        static Sprite MakeSprite(int col, int row)
        {
            var tex = _texture;
            // 矩形坐标与 DrawDiamond 的写入像素一一对应（同为纹理左下原点）。
            return Sprite.Create(tex,
                new Rect(col * Cell, row * Cell, Cell, Cell),
                new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect);
        }

        /// <summary>
        /// 在 (col,row) 单元内画一个 2:1 等距菱形，顶点在单元正中。
        /// 采用「点是否在菱形内」的解析判定，边缘加 2px 深色描边以在相邻瓦片间形成分隔。
        /// </summary>
        static void DrawDiamond(Color32[] px, int col, int row, Color color)
        {
            int ox = col * Cell, oy = row * Cell;
            float cx = ox + Cell * 0.5f;
            float cy = oy + Cell * 0.5f;
            float hw = Cell * 0.5f - 2f;
            float hh = Cell * 0.25f - 1f;

            var fill = (Color32)color;
            var edge = (Color32)Color.Lerp(color, Color.black, 0.45f);

            for (int y = 0; y < Cell; y++)
            {
                for (int x = 0; x < Cell; x++)
                {
                    float dx = Mathf.Abs((ox + x + 0.5f) - cx) / hw;
                    float dy = Mathf.Abs((oy + y + 0.5f) - cy) / hh;
                    float d = dx + dy;                 // 曼哈顿型菱形判定
                    if (d <= 1f)
                    {
                        float shade = 1f - Mathf.Clamp01((y - (cy - hh)) / (2f * hh)) * 0.18f;
                        var c = fill;
                        c.r = (byte)(fill.r * shade);
                        c.g = (byte)(fill.g * shade);
                        c.b = (byte)(fill.b * shade);
                        c.a = 255;
                        px[(oy + y) * AtlasWidth + ox + x] = d > 0.88f ? edge : c;
                    }
                }
            }
        }

        /// <summary>在顶面菱形内随机撒点，用于雪斑/草噪/岩浆裂纹。</summary>
        static void Speckle(Color32[] px, int col, int row, Color color, int count)
        {
            int ox = col * Cell, oy = row * Cell;
            float cx = ox + Cell * 0.5f, cy = oy + Cell * 0.5f;
            float hw = Cell * 0.5f - 6f, hh = Cell * 0.25f - 3f;
            var rng = new System.Random(0xC0FFEE ^ (col * 31 + row * 7919));
            var c32 = (Color32)color;

            for (int i = 0; i < count; i++)
            {
                float fx = (float)rng.NextDouble() * 2f - 1f;
                float fy = (float)rng.NextDouble() * 2f - 1f;
                int px0 = Mathf.RoundToInt(cx + fx * hw);
                int py0 = Mathf.RoundToInt(cy + fy * hh);
                if (Mathf.Abs(fx) + Mathf.Abs(fy) > 0.92f) continue;
                if (px0 < ox || py0 < oy || px0 >= ox + Cell || py0 >= oy + Cell) continue;
                px[py0 * AtlasWidth + px0] = c32;
            }
        }
    }
}