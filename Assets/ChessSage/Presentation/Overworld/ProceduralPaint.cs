using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 程序化绘图原语（像素级，纯 CPU，用于生成图集）。
    ///
    /// 大地图上所有美术（地形/装饰/地标/角色/道路）都由这些原语拼出，
    /// 集中一处是为了保证"同一套像素风观感"：同样的描边/抖动规则，
    /// 避免各处各画一套导致风格漂移。
    ///
    /// 所有写入都被裁剪到 <paramref name="cellW"/>×<paramref name="cellH"/> 单元内，越界即丢弃——
    /// 图集里各单元紧邻，任何越界都会污染相邻图形。
    /// <b>宽高必须分开传</b>：角色立绘是 64×80（非正方形），
    /// 早期按正方形裁剪会把头顶整段切掉（角色只剩半截——历史截图里那个"黑方块"的一部分）。
    /// </summary>
    internal static class ProceduralPaint
    {
        public static void Put(Color32[] px, int atlasWidth, int ox, int oy, int cellW, int cellH,
            int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= cellW || y >= cellH) return;
            int idx = (oy + y) * atlasWidth + ox + x;
            if (c.a == 255) { px[idx] = c; return; }
            var d = px[idx];
            float a = c.a / 255f;
            px[idx] = new Color32((byte)(c.r * a + d.r * (1 - a)),
                (byte)(c.g * a + d.g * (1 - a)),
                (byte)(c.b * a + d.b * (1 - a)), 255);
        }

        public static void FillRect(Color32[] px, int aw, int ox, int oy, int cellW, int cellH,
            int x0, int y0, int x1, int y1, Color32 c)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) Put(px, aw, ox, oy, cellW, cellH, x, y, c);
        }

        public static void FillCircle(Color32[] px, int aw, int ox, int oy, int cellW, int cellH,
            float cx, float cy, float r, Color32 c)
        {
            int x0 = Mathf.FloorToInt(cx - r), x1 = Mathf.CeilToInt(cx + r);
            int y0 = Mathf.FloorToInt(cy - r), y1 = Mathf.CeilToInt(cy + r);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    if (dx * dx + dy * dy <= r * r) Put(px, aw, ox, oy, cellW, cellH, x, y, c);
                }
        }

        /// <summary>等腰三角形：顶点在上、底边在下。</summary>
        public static void Tri(Color32[] px, int aw, int ox, int oy, int cellW, int cellH,
            float cx, float apexY, float halfW, Color32 c)
        {
            float baseY = apexY - halfW * 1.2f;
            for (int y = Mathf.FloorToInt(baseY); y <= Mathf.CeilToInt(apexY); y++)
            {
                float t = (apexY - y) / Mathf.Max(1f, apexY - baseY);
                float w = halfW * t;
                for (int x = Mathf.RoundToInt(cx - w); x <= Mathf.RoundToInt(cx + w); x++)
                    Put(px, aw, ox, oy, cellW, cellH, x, y, c);
            }
        }

        public static void Poly(Color32[] px, int aw, int ox, int oy, int cellW, int cellH,
            (int x, int y)[] pts, Color32 c)
        {
            int minY = int.MaxValue, maxY = int.MinValue;
            foreach (var p in pts) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
            for (int y = minY; y <= maxY; y++)
            {
                float minX = float.MaxValue, maxX = float.MinValue;
                for (int i = 0; i < pts.Length; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Length];
                    if ((a.y <= y && b.y > y) || (b.y <= y && a.y > y))
                    {
                        float t = (y - a.y) / (float)(b.y - a.y);
                        float xi = a.x + t * (b.x - a.x);
                        if (xi < minX) minX = xi;
                        if (xi > maxX) maxX = xi;
                    }
                }
                for (int x = Mathf.RoundToInt(minX); x <= Mathf.RoundToInt(maxX); x++)
                    Put(px, aw, ox, oy, cellW, cellH, x, y, c);
            }
        }

        public static void Line(Color32[] px, int aw, int ox, int oy, int cellW, int cellH,
            int x0, int y0, int x1, int y1, Color32 c, int thick)
        {
            int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) + 1;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
                int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
                for (int dy = 0; dy < thick; dy++)
                    for (int dx = 0; dx < thick; dx++)
                        Put(px, aw, ox, oy, cellW, cellH, x + dx, y + dy, c);
            }
        }

        public static Color32 Jit(Color32 c, Core.Overworld.DeterministicRng32 rng, float amt)
        {
            float m = 1f + rng.Range(-amt, amt);
            return new Color32((byte)Mathf.Clamp(c.r * m, 0, 255),
                (byte)Mathf.Clamp(c.g * m, 0, 255), (byte)Mathf.Clamp(c.b * m, 0, 255), 255);
        }

        public static Color32 Lighter(Color32 c, float t) =>
            new Color32((byte)(c.r + (255 - c.r) * t), (byte)(c.g + (255 - c.g) * t),
                (byte)(c.b + (255 - c.b) * t), 255);

        public static Color32 Darker(Color32 c, float t) =>
            new Color32((byte)(c.r * (1 - t)), (byte)(c.g * (1 - t)), (byte)(c.b * (1 - t)), 255);
    }
}
