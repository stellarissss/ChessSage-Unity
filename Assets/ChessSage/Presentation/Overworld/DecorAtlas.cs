using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 程序化装饰物图集：18 种物种 × 4 个变体，共 72 张 64×64 精灵。
    ///
    /// 【为什么全部用程序化图形】任务约束"不引入联网资源包，美术只能用程序化生成
    /// 或项目内既有资源"。程序化生成的好处是零体积、零授权风险，且能随种子稳定复现。
    /// 为了不像"复制粘贴"，每个物种提供 4 个变体（外形比例/数量不同），
    /// 再叠加渲染期的随机缩放/翻转/色调抖动（见 <see cref="OverworldDecorRenderer"/>）。
    ///
    /// 图集为一张 768×384 纹理（约 1.2 MB），所有装饰共享材质，利于合批。
    /// </summary>
    public static class DecorAtlas
    {
        public const int Cell = 64;
        public const int Variants = 4;
        public const int Kinds = 18;               // 与 DecorKind 数量一致
        const int Cols = 12;
        const int Rows = 6;

        static Texture2D _tex;
        static Sprite[,] _sprites;                 // [kind, variant]

        public static Texture2D Texture
        {
            get { if (_tex == null) Build(); return _tex; }
        }

        public static Sprite Get(DecorKind kind, int variant)
        {
            if (_tex == null) Build();
            int k = (int)kind;
            if (k < 0 || k >= Kinds) k = 0;
            return _sprites[k, ((variant % Variants) + Variants) % Variants];
        }

        static Sprite _shadow;

        /// <summary>
        /// 一个柔和的椭圆投影，垫在高大装饰/地标/角色脚下。
        /// 没有投影的 2D 物件会"漂浮"在瓦片上，加上后立刻"落地"——这是廉价但极有效的立体感手段。
        /// </summary>
        public static Sprite Shadow()
        {
            if (_shadow != null) return _shadow;
            const int w = 48, h = 22;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "DecorShadow",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = (x + 0.5f - cx) / (w * 0.5f);
                    float dy = (y + 0.5f - cy) / (h * 0.5f);
                    float d = dx * dx + dy * dy;
                    if (d > 1f) continue;
                    byte a = (byte)(Mathf.Clamp01(1f - d) * 110f);
                    px[y * w + x] = new Color32(0, 0, 0, a);
                }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            _shadow = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _shadow.name = "decor_shadow";
            return _shadow;
        }

        static void Build()
        {
            _tex = new Texture2D(Cols * Cell, Rows * Cell, TextureFormat.RGBA32, mipChain: false)
            {
                name = "DecorAtlas_Procedural",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _sprites = new Sprite[Kinds, Variants];

            var px = new Color32[Cols * Cell * Rows * Cell];
            int w = Cols * Cell;

            for (int k = 0; k < Kinds; k++)
            {
                for (int v = 0; v < Variants; v++)
                {
                    int idx = k * Variants + v;
                    int col = idx % Cols, row = idx / Cols;
                    int ox = col * Cell, oy = row * Cell;
                    var rng = new DeterministicRng32(unchecked(k * 0x5BD1E995 ^ (v * 40503 + 1)));
                    Draw(px, w, ox, oy, (DecorKind)k, v, rng);
                    _sprites[k, v] = Sprite.Create(_tex,
                        new Rect(ox, oy, Cell, Cell),
                        new Vector2(0.5f, 0.10f), 100f, 0, SpriteMeshType.FullRect);
                }
            }

            _tex.SetPixels32(px);
            _tex.Apply(false, false);
        }

        // ── 各物种绘制 ──────────────────────────────────────────────────────

        static void Draw(Color32[] px, int aw, int ox, int oy, DecorKind kind, int v, DeterministicRng32 rng)
        {
            float s = 1f + rng.Range(-0.08f, 0.08f);      // 变体尺度
            switch (kind)
            {
                case DecorKind.BroadTree:
                {
                    var trunk = new Color32(92, 62, 38, 255);
                    var leaf = Jit(new Color32(58, 132, 52, 255), rng, 0.10f);
                    FillRect(px, aw, ox, oy, 30, 6, 34, 30, trunk);
                    FillCircle(px, aw, ox, oy, 32, 40 * s, 15 * s, leaf);
                    FillCircle(px, aw, ox, oy, 22, 36 * s, 11 * s, Lighter(leaf, 0.12f));
                    FillCircle(px, aw, ox, oy, 43, 35 * s, 11 * s, Darker(leaf, 0.10f));
                    FillCircle(px, aw, ox, oy, 32, 50 * s, 10 * s, Lighter(leaf, 0.2f));
                    break;
                }
                case DecorKind.PineTree:
                {
                    var trunk = new Color32(80, 54, 36, 255);
                    var leaf = Jit(new Color32(42, 108, 74, 255), rng, 0.10f);
                    FillRect(px, aw, ox, oy, 30, 6, 34, 26, trunk);
                    Tri(px, aw, ox, oy, 32, 58 * s, 20 * s, leaf);
                    Tri(px, aw, ox, oy, 32, 46 * s, 24 * s, Darker(leaf, 0.08f));
                    Tri(px, aw, ox, oy, 32, 34 * s, 26 * s, Lighter(leaf, 0.06f));
                    break;
                }
                case DecorKind.DeadTree:
                {
                    var bark = Jit(new Color32(108, 96, 78, 255), rng, 0.08f);
                    FillRect(px, aw, ox, oy, 30, 6, 34, 34, bark);
                    Line(px, aw, ox, oy, 32, 30, 20, 46, bark, 3);
                    Line(px, aw, ox, oy, 32, 36, 44, 50, bark, 3);
                    Line(px, aw, ox, oy, 32, 24, 42, 30, bark, 2);
                    break;
                }
                case DecorKind.Bush:
                {
                    var c = Jit(new Color32(66, 128, 58, 255), rng, 0.12f);
                    FillCircle(px, aw, ox, oy, 26, 16 * s, 10 * s, c);
                    FillCircle(px, aw, ox, oy, 38, 16 * s, 10 * s, c);
                    FillCircle(px, aw, ox, oy, 32, 24 * s, 11 * s, Lighter(c, 0.14f));
                    break;
                }
                case DecorKind.Flower:
                {
                    var petal = Jit(new Color32(226, 116, 154, 255), rng, 0.18f);
                    var stem = new Color32(66, 130, 60, 255);
                    int n = 3 + v % 3;
                    for (int i = 0; i < n; i++)
                    {
                        int fx = 20 + i * 8;
                        FillRect(px, aw, ox, oy, fx, 6, fx + 2, 18, stem);
                        FillCircle(px, aw, ox, oy, fx + 1, 20, 4, petal);
                    }
                    break;
                }
                case DecorKind.Reed:
                {
                    var c = Jit(new Color32(122, 150, 70, 255), rng, 0.12f);
                    for (int i = 0; i < 5; i++)
                    {
                        int bx = 22 + i * 5;
                        Line(px, aw, ox, oy, bx, 6, bx + (i - 2), 40, c, 2);
                    }
                    break;
                }
                case DecorKind.Rock:
                {
                    var c = Jit(new Color32(126, 124, 122, 255), rng, 0.10f);
                    FillCircle(px, aw, ox, oy, 32, 14 * s, 12 * s, c);
                    FillCircle(px, aw, ox, oy, 26, 18 * s, 7 * s, Lighter(c, 0.18f));
                    break;
                }
                case DecorKind.Boulder:
                {
                    var c = Jit(new Color32(112, 110, 108, 255), rng, 0.10f);
                    Poly(px, aw, ox, oy, new[] { (20, 8), (44, 8), (52, 26), (40, 40), (18, 34), (12, 20) }, c);
                    Poly(px, aw, ox, oy, new[] { (24, 12), (40, 14), (40, 24), (24, 22) }, Lighter(c, 0.16f));
                    break;
                }
                case DecorKind.Cactus:
                {
                    var c = Jit(new Color32(74, 142, 82, 255), rng, 0.10f);
                    FillRect(px, aw, ox, oy, 28, 6, 36, 46, c);
                    FillRect(px, aw, ox, oy, 18, 22, 28, 28, c);
                    FillRect(px, aw, ox, oy, 36, 30, 46, 36, c);
                    break;
                }
                case DecorKind.Crystal:
                {
                    var c = Jit(new Color32(150, 220, 236, 255), rng, 0.10f);
                    Tri(px, aw, ox, oy, 32, 52 * s, 12 * s, c);
                    Tri(px, aw, ox, oy, 22, 34 * s, 8 * s, Darker(c, 0.12f));
                    Tri(px, aw, ox, oy, 42, 38 * s, 8 * s, Lighter(c, 0.2f));
                    break;
                }
                case DecorKind.RuinPillar:
                {
                    var c = Jit(new Color32(168, 162, 148, 255), rng, 0.08f);
                    FillRect(px, aw, ox, oy, 24, 6, 40, 40, c);
                    FillRect(px, aw, ox, oy, 20, 40, 44, 46, Darker(c, 0.15f));
                    FillRect(px, aw, ox, oy, 22, 6, 16, 12, Darker(c, 0.2f));
                    break;
                }
                case DecorKind.Banner:
                {
                    var pole = new Color32(96, 74, 50, 255);
                    var cloth = Jit(new Color32(176, 54, 46, 255), rng, 0.12f);
                    FillRect(px, aw, ox, oy, 30, 6, 33, 54, pole);
                    Poly(px, aw, ox, oy, new[] { (33, 50), (52, 46), (33, 30) }, cloth);
                    break;
                }
                case DecorKind.Bone:
                {
                    var c = Jit(new Color32(228, 224, 206, 255), rng, 0.05f);
                    FillCircle(px, aw, ox, oy, 24, 14, 5, c);
                    FillCircle(px, aw, ox, oy, 24, 26, 5, c);
                    FillRect(px, aw, ox, oy, 24, 18, 42, 22, c);
                    FillCircle(px, aw, ox, oy, 42, 16, 5, c);
                    FillCircle(px, aw, ox, oy, 42, 24, 5, c);
                    break;
                }
                case DecorKind.Stump:
                {
                    var c = Jit(new Color32(118, 84, 54, 255), rng, 0.08f);
                    FillRect(px, aw, ox, oy, 24, 6, 40, 22, c);
                    FillCircle(px, aw, ox, oy, 32, 22, 8, Lighter(c, 0.18f));
                    break;
                }
                case DecorKind.Mushroom:
                {
                    var stem = new Color32(226, 218, 198, 255);
                    var cap = Jit(new Color32(186, 62, 66, 255), rng, 0.12f);
                    FillRect(px, aw, ox, oy, 30, 8, 34, 20, stem);
                    FillCircle(px, aw, ox, oy, 32, 22, 10 * s, cap);
                    FillCircle(px, aw, ox, oy, 28, 24, 2, Color.white);
                    FillCircle(px, aw, ox, oy, 37, 21, 2, Color.white);
                    break;
                }
                case DecorKind.GrassTuft:
                {
                    var c = Jit(new Color32(96, 156, 74, 255), rng, 0.14f);
                    for (int i = 0; i < 4; i++)
                    {
                        int bx = 24 + i * 5;
                        Line(px, aw, ox, oy, bx, 6, bx + (i - 1) * 2, 22, c, 2);
                    }
                    break;
                }
                case DecorKind.SnowMound:
                {
                    var c = Jit(new Color32(238, 244, 250, 255), rng, 0.04f);
                    FillCircle(px, aw, ox, oy, 32, 12 * s, 13 * s, c);
                    FillCircle(px, aw, ox, oy, 24, 15 * s, 8 * s, Color.white);
                    break;
                }
                case DecorKind.LavaRock:
                {
                    var c = Jit(new Color32(72, 58, 56, 255), rng, 0.10f);
                    var glow = new Color32(232, 122, 40, 255);
                    Poly(px, aw, ox, oy, new[] { (18, 8), (46, 8), (50, 24), (36, 40), (16, 30) }, c);
                    Line(px, aw, ox, oy, 24, 16, 40, 26, glow, 2);
                    Line(px, aw, ox, oy, 32, 34, 38, 16, glow, 2);
                    break;
                }
            }
        }

        // ── 图形原语：委托给共享的 ProceduralPaint（统一像素风），此处只绑定单元尺寸 ──

        static void Put(Color32[] px, int aw, int ox, int oy, int x, int y, Color32 c)
            => ProceduralPaint.Put(px, aw, ox, oy, Cell, Cell, x, y, c);

        static void FillRect(Color32[] px, int aw, int ox, int oy, int x0, int y0, int x1, int y1, Color32 c)
            => ProceduralPaint.FillRect(px, aw, ox, oy, Cell, Cell, x0, y0, x1, y1, c);

        static void FillCircle(Color32[] px, int aw, int ox, int oy, float cx, float cy, float r, Color32 c)
            => ProceduralPaint.FillCircle(px, aw, ox, oy, Cell, Cell, cx, cy, r, c);

        static void Tri(Color32[] px, int aw, int ox, int oy, float cx, float apexY, float halfW, Color32 c)
            => ProceduralPaint.Tri(px, aw, ox, oy, Cell, Cell, cx, apexY, halfW, c);

        static void Poly(Color32[] px, int aw, int ox, int oy, (int x, int y)[] pts, Color32 c)
            => ProceduralPaint.Poly(px, aw, ox, oy, Cell, Cell, pts, c);

        static void Line(Color32[] px, int aw, int ox, int oy, int x0, int y0, int x1, int y1, Color32 c, int thick)
            => ProceduralPaint.Line(px, aw, ox, oy, Cell, Cell, x0, y0, x1, y1, c, thick);

        static Color32 Jit(Color32 c, DeterministicRng32 rng, float amt)
            => ProceduralPaint.Jit(c, rng, amt);

        static Color32 Lighter(Color32 c, float t) => ProceduralPaint.Lighter(c, t);

        static Color32 Darker(Color32 c, float t) => ProceduralPaint.Darker(c, t);
    }
}
