using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 程序化生成等距地形图集（Texture2D）。
    ///
    /// 正式美术资源到位前，用代码生成 13 种地形的菱形瓦片。
    ///
    /// 【本轮升级：从"单图"到"多变体 + 颗粒"】
    /// 历史版本每种地形只有 1 张纯色菱形，导致整张大地图是"色块拼图"——
    /// 每个格子逐像素完全相同，肉眼立刻识别出机械重复。现在：
    ///   · 每种地形生成 <see cref="Variants"/> 个<b>变体</b>（同一色系、不同的细节噪声），
    ///     渲染时按格子哈希取变体，彻底消除"整齐划一"的观感；
    ///   · 每个变体内加入<b>逐像素颗粒</b>（轻微明暗抖动）——即使同变体相邻也不同，
    ///     这同时把截图的"唯一色"从十几提升到成百上千。
    ///
    /// 图集布局：行 = 13 种地形；列 = 面（顶/左/右）× 变体（4）。
    /// 尺寸 1152×1248（约 5.6 MB），一次性生成后静态缓存。
    /// </summary>
    public static class TerrainAtlas
    {
        public const int Cell = 96;
        public const int Faces = 3;        // 顶面 / 左面 / 右面
        public const int Variants = 4;     // 每种地形的图形变体数
        public const int TerrainKinds = 13;
        public const int Columns = Faces * Variants;
        public const int AtlasWidth = Cell * Columns;
        public const int AtlasHeight = Cell * TerrainKinds;

        /// <summary>
        /// 图集的 PPU。**必须是 50 而不是 100**：
        /// 等距格的世界坐标由 <see cref="IsoGrid.CellToWorld"/> 决定，相邻同排格相距 <c>2*TileW = 2</c> 世界单位，
        /// 即一块菱形瓦片的“满宽”应为 2.0 世界单位。若按 100 PPU，96px 的图只映射到 0.96 世界单位——
        /// 恰好是应有尺寸的一半，于是每块瓦片只盖住格子的一半，中间露出背景色，
        /// 整张地图看起来就是“一颗颗浮空的菱形”（这正是历史截图“色块拼图感”的根因）。
        /// 取 50 PPU → 96/50 = 1.92 ≈ 2.0（配合 TileScale 0.96 精确吻合），瓦片即可无缝拼接。
        /// </summary>
        public const float PixelsPerUnit = 50f;

        static Texture2D _texture;
        // [terrain, face, variant]
        static Sprite[,,] _sprites;

        public static Texture2D Texture
        {
            get { if (_texture == null) Build(); return _texture; }
        }

        public static Sprite Top(TerrainKind t) => Sprite3(t, 0, 0);
        public static Sprite Left(TerrainKind t) => Sprite3(t, 1, 0);
        public static Sprite Right(TerrainKind t) => Sprite3(t, 2, 0);

        public static Sprite Top(TerrainKind t, int variant) => Sprite3(t, 0, variant);
        public static Sprite Left(TerrainKind t, int variant) => Sprite3(t, 1, variant);
        public static Sprite Right(TerrainKind t, int variant) => Sprite3(t, 2, variant);

        static Sprite Sprite3(TerrainKind t, int face, int variant)
        {
            if (_texture == null) Build();
            int ti = (int)t;
            if (ti < 0 || ti >= TerrainKinds) ti = 3;             // 越界回退草地，绝不崩
            if (variant < 0 || variant >= Variants) variant = 0;
            return _sprites[ti, face, variant];
        }

        static void Build()
        {
            _texture = new Texture2D(AtlasWidth, AtlasHeight, TextureFormat.RGBA32, mipChain: false)
            {
                name = "TerrainAtlas_Procedural",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _sprites = new Sprite[TerrainKinds, Faces, Variants];

            var pixels = new Color32[AtlasWidth * AtlasHeight];

            for (int t = 0; t < TerrainKinds; t++)
            {
                var kind = (TerrainKind)t;
                bool raised = TerrainPalette.IsRaised(kind);
                int row = TerrainKinds - 1 - t;      // 纹理原点在左下，行倒置

                for (int v = 0; v < Variants; v++)
                {
                    var top = TerrainPalette.Get(kind);
                    var side = raised ? TerrainPalette.SideFace(kind) : TerrainPalette.Get(kind);

                    DrawDiamond(pixels, FaceCol(0, v), row, top, kind, v);
                    DrawDiamond(pixels, FaceCol(1, v), row, side, kind, v);
                    DrawDiamond(pixels, FaceCol(2, v), row, side, kind, v);
                }

                for (int v = 0; v < Variants; v++)
                {
                    _sprites[t, 0, v] = MakeSprite(FaceCol(0, v), row);
                    _sprites[t, 1, v] = MakeSprite(FaceCol(1, v), row);
                    _sprites[t, 2, v] = MakeSprite(FaceCol(2, v), row);
                }
            }

            _texture.SetPixels32(pixels);
            _texture.Apply(false, false);
        }

        static int FaceCol(int face, int variant) => face * Variants + variant;

        static Sprite MakeSprite(int col, int row)
        {
            return Sprite.Create(_texture,
                new Rect(col * Cell, row * Cell, Cell, Cell),
                new Vector2(0.5f, 0.5f), PixelsPerUnit, 0,
                SpriteMeshType.FullRect);
        }

        /// <summary>
        /// 在 (col,row) 单元内画一个 2:1 等距菱形。
        /// 变体差异来自三处：①噪声颗粒的种子 ②基色的轻微明暗/色相偏移 ③细节点缀的种类。
        /// </summary>
        static void DrawDiamond(Color32[] px, int col, int row, Color color,
            TerrainKind kind, int variant)
        {
            int ox = col * Cell, oy = row * Cell;
            float cx = ox + Cell * 0.5f;
            float cy = oy + Cell * 0.5f;
            // 关键：菱形必须<b>填满整格</b>（半宽=Cell/2、半高=Cell/4），
            // 相邻瓦片才能无缝拼接。历史版本内缩 2px 导致瓦片之间露出背景色，
            // 整张地图看起来像"一颗颗浮在空中的菱形"——这正是"色块拼图"观的来源。
            float hw = Cell * 0.5f;
            float hh = Cell * 0.25f;

            // 变体基色：±6% 明度 + 轻微色相偏移，让相邻的同种地形不再"一模一样"。
            var rng = new DeterministicRng32(kind.GetHashCode() * 73856093 ^ variant * 19349663);
            float brighter = 1f + rng.Range(-0.06f, 0.06f);
            float hueShift = rng.Range(-0.03f, 0.03f);
            var baseCol = Shift(color, brighter, hueShift);

            var fill = (Color32)baseCol;
            // 极轻描边：仅用于在连续地表上保留"格"的阅读感（0.18 而非历史的 0.45）。
            var edge = (Color32)Color.Lerp(baseCol, Color.black, 0.10f);

            for (int y = 0; y < Cell; y++)
            {
                for (int x = 0; x < Cell; x++)
                {
                    float dx = Mathf.Abs((ox + x + 0.5f) - cx) / hw;
                    float dy = Mathf.Abs((oy + y + 0.5f) - cy) / hh;
                    float d = dx + dy;
                    if (d > 1.02f) continue;

                    float shade = 1f - Mathf.Clamp01((y - (cy - hh)) / (2f * hh)) * 0.12f;

                    // 逐像素颗粒：±4% 明暗。静止的纯色块在肉眼下"发死"，
                    // 一点点颗粒就能让它"活"起来——这是低分辨率像素风的通用手法。
                    float grain = 1f + Hash01(ox + x, oy + y, kind, variant) * 0.08f - 0.04f;

                    var c = fill;
                    c.r = (byte)Mathf.Clamp(fill.r * shade * grain, 0f, 255f);
                    c.g = (byte)Mathf.Clamp(fill.g * shade * grain, 0f, 255f);
                    c.b = (byte)Mathf.Clamp(fill.b * shade * grain, 0f, 255f);
                    c.a = 255;
                    px[(oy + y) * AtlasWidth + ox + x] = d > 0.94f ? edge : c;
                }
            }

            // 细节点缀（草叶 / 石纹 / 浪花 / 雪斑…）——变体间的差异主要来自这里。
            Detail(px, col, row, kind, variant, rng);
        }

        static void Detail(Color32[] px, int col, int row, TerrainKind kind, int variant,
            DeterministicRng32 rng)
        {
            int count;
            Color dot;
            switch (kind)
            {
                case TerrainKind.Grass:
                    count = 26; dot = Color.Lerp(TerrainPalette.Get(kind), Color.black, 0.20f); break;
                case TerrainKind.Hill:
                    count = 22; dot = Color.Lerp(TerrainPalette.Get(kind), Color.black, 0.24f); break;
                case TerrainKind.DeepWater:
                    count = 14; dot = Color.Lerp(TerrainPalette.Get(kind), Color.white, 0.35f); break;
                case TerrainKind.Shallow:
                    count = 18; dot = Color.Lerp(TerrainPalette.Get(kind), Color.white, 0.40f); break;
                case TerrainKind.Beach:
                    count = 20; dot = Color.Lerp(TerrainPalette.Get(kind), Color.white, 0.22f); break;
                case TerrainKind.SaltLake:
                    count = 18; dot = Color.Lerp(TerrainPalette.Get(kind), Color.white, 0.30f); break;
                case TerrainKind.DryRiver:
                    count = 20; dot = Color.Lerp(TerrainPalette.Get(kind), Color.black, 0.28f); break;
                case TerrainKind.Ridge:
                    count = 30; dot = Color.white; break;
                case TerrainKind.Ice:
                    count = 24; dot = Color.Lerp(TerrainPalette.Get(kind), Color.white, 0.55f); break;
                case TerrainKind.Mountain:
                case TerrainKind.BareRock:
                    count = 24; dot = Color.Lerp(TerrainPalette.Get(kind), Color.black, 0.35f); break;
                case TerrainKind.Wall:
                    count = 26; dot = Color.Lerp(TerrainPalette.Get(kind), Color.black, 0.4f); break;
                case TerrainKind.Lava:
                    count = 22; dot = new Color(1f, 0.78f, 0.30f); break;
                default:
                    count = 18; dot = Color.Lerp(TerrainPalette.Get(kind), Color.black, 0.25f); break;
            }

            // 变体改点缀数量与位置种子。
            count += variant * 4;
            Speckle(px, col, row, dot, count, rng);
        }

        /// <summary>在顶面菱形内撒点（变体化：位置与数量都由变体决定）。</summary>
        static void Speckle(Color32[] px, int col, int row, Color color, int count,
            DeterministicRng32 rng)
        {
            int ox = col * Cell, oy = row * Cell;
            float cx = ox + Cell * 0.5f, cy = oy + Cell * 0.5f;
            float hw = Cell * 0.5f - 6f, hh = Cell * 0.25f - 3f;
            var c32 = (Color32)color;

            for (int i = 0; i < count; i++)
            {
                float fx = rng.Range(-1f, 1f);
                float fy = rng.Range(-1f, 1f);
                if (Mathf.Abs(fx) + Mathf.Abs(fy) > 0.9f) continue;
                int px0 = Mathf.RoundToInt(cx + fx * hw);
                int py0 = Mathf.RoundToInt(cy + fy * hh);
                if (px0 < ox || py0 < oy || px0 >= ox + Cell || py0 >= oy + Cell) continue;
                // 部分点缀画成 2px，视觉更明显。
                px[py0 * AtlasWidth + px0] = c32;
                if ((i & 1) == 0 && px0 + 1 < ox + Cell) px[py0 * AtlasWidth + px0 + 1] = c32;
            }
        }

        static Color Shift(Color c, float mul, float hue)
        {
            // 极简色相偏移：对 R/B 做反向微调，成本低且足以打破"齐整"。
            float r = Mathf.Clamp01(c.r * mul + hue);
            float g = Mathf.Clamp01(c.g * mul);
            float b = Mathf.Clamp01(c.b * mul - hue);
            return new Color(r, g, b, c.a);
        }

        /// <summary>坐标哈希 → [0,1)，用于稳定颗粒（同格同色，不闪烁）。</summary>
        static float Hash01(int x, int y, TerrainKind kind, int variant)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + (int)kind * 2246822519u + variant * 3266489917u);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
