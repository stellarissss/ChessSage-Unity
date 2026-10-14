using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 地形过渡（autotile）图集：4 个方向的<b>白色渐变楔形</b>，运行时用邻居地形的颜色染色。
    ///
    /// 【为什么用"白色 + 运行时染色"而不是为每种地表对预生成过渡面】
    /// 13 种地形两两相邻有 78 种组合，若各生成 4 向过渡面就是 312 张图——图集爆炸、
    /// 生成耗时、显存浪费。而过渡的本质只是"把邻居的颜色向本格内侧羽化"，
    /// 因此只需 4 张<b>中性</b>楔形，颜色交给 <c>SpriteRenderer.color</c> 顶点色即可。
    /// 一笔 4 张图换来全地形通用的软边界。
    ///
    /// 方向按等距投影定义（见 <see cref="IsoGrid"/>）：
    ///   N=(x,y-1) 屏幕右上，E=(x+1,y) 右下，S=(x,y+1) 左下，W=(x-1,y) 左上。
    /// </summary>
    public static class OverworldEdgeAtlas
    {
        public const int Cell = 96;
        public const int Directions = 4;      // N/E/S/W

        static Texture2D _tex;
        static Sprite[] _wedges;

        public static Texture2D Texture
        {
            get { if (_tex == null) Build(); return _tex; }
        }

        /// <summary>取某方向的过渡楔形（白色，需染成邻居色）。dir: 0=N,1=E,2=S,3=W。</summary>
        public static Sprite Wedge(int dir)
        {
            if (_tex == null) Build();
            return _wedges[((dir % Directions) + Directions) % Directions];
        }

        static void Build()
        {
            _tex = new Texture2D(Cell * Directions, Cell, TextureFormat.RGBA32, mipChain: false)
            {
                name = "EdgeTransition",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[Cell * Directions * Cell];
            for (int dir = 0; dir < Directions; dir++)
                DrawWedge(pixels, dir);

            _tex.SetPixels32(pixels);
            _tex.Apply(false, false);

            _wedges = new Sprite[Directions];
            for (int d = 0; d < Directions; d++)
                _wedges[d] = Sprite.Create(_tex,
                    new Rect(d * Cell, 0, Cell, Cell),
                    new Vector2(0.5f, 0.5f), TerrainAtlas.PixelsPerUnit, 0, SpriteMeshType.FullRect);
        }

        /// <summary>画第 dir 个楔形：菱形某条边到中心的线性 alpha 羽化。</summary>
        static void DrawWedge(Color32[] px, int dir)
        {
            int ox = dir * Cell;
            float cx = Cell * 0.5f, cy = Cell * 0.5f;                 // 单元内中心（与贴图同原点）
            float hw = Cell * 0.5f, hh = Cell * 0.25f;                // 与地形瓦片同几何，才能对齐

            // 菱形四顶点（单元坐标，y 向上）
            var top = new Vector2(cx, cy + hh);
            var right = new Vector2(cx + hw, cy);
            var bottom = new Vector2(cx, cy - hh);
            var left = new Vector2(cx - hw, cy);

            // 方向 → 该侧的边（两端点）
            Vector2 a, b;
            switch (dir)
            {
                case 0: a = top; b = right; break;    // N: 右上边
                case 1: a = right; b = bottom; break; // E: 右下边
                case 2: a = bottom; b = left; break;  // S: 左下边
                default: a = left; b = top; break;    // W: 左上边
            }

            float band = Cell * 0.34f;   // 羽化带宽
            for (int y = 0; y < Cell; y++)
            {
                for (int x = 0; x < Cell; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    // 只画在菱形内（用曼哈顿距离判定，与地形瓦片一致）
                    float dx = Mathf.Abs(fx - cx) / hw;
                    float dy = Mathf.Abs(fy - cy) / hh;
                    if (dx + dy > 1f) continue;

                    float dist = DistToSegment(fx, fy, a, b);
                    float alpha = Mathf.Clamp01(1f - dist / band);
                    alpha *= alpha;                                  // 平方衰减，过渡更柔和
                    if (alpha <= 0.001f) continue;
                    px[y * (Cell * Directions) + ox + x] =
                        new Color32(255, 255, 255, (byte)(alpha * 255));
                }
            }
        }

        static float DistToSegment(float px, float py, Vector2 a, Vector2 b)
        {
            float vx = b.x - a.x, vy = b.y - a.y;
            float wx = px - a.x, wy = py - a.y;
            float len2 = vx * vx + vy * vy;
            float t = len2 > 0f ? Mathf.Clamp01((wx * vx + wy * vy) / len2) : 0f;
            float dx = wx - vx * t, dy = wy - vy * t;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
    }
}
