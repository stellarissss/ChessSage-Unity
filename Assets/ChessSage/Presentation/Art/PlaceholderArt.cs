using UnityEngine;

namespace ChessSage.Presentation.Art
{
    /// <summary>
    /// 占位美术的程序化生成器。
    ///
    /// 【为什么需要它】M1 阶段工程尚无导入的 Sprite 资源，若不提供占位图，
    /// 棋盘视图将完全不可见、无法做「可玩闭环」验证。此处用代码生成最小可辨识图形，
    /// 使垂直切片自包含、可运行；后续里程碑由 ArtImporters 导入真实美术后整体替换。
    ///
    /// 【性能】所有 Sprite 静态缓存，避免运行时重复生成纹理。
    /// </summary>
    public static class PlaceholderArt
    {
        const int Size = 64;
        const float PixelsPerUnit = 64f;
        static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        static Sprite _cell, _cellAlt, _dotMove, _dotCapture, _selectRing;
        static readonly System.Collections.Generic.Dictionary<string, Sprite> PieceCache =
            new System.Collections.Generic.Dictionary<string, Sprite>();

        public static Sprite Cell => _cell ?? (_cell = RoundedRect("cell", new Color(0.88f, 0.82f, 0.70f)));
        public static Sprite CellAlt => _cellAlt ?? (_cellAlt = RoundedRect("cellAlt", new Color(0.81f, 0.73f, 0.59f)));
        public static Sprite DotMove => _dotMove ?? (_dotMove = Disc("dotMove", new Color(0.25f, 0.80f, 0.35f, 0.85f)));
        public static Sprite DotCapture => _dotCapture ?? (_dotCapture = Disc("dotCapture", new Color(0.90f, 0.25f, 0.22f, 0.90f)));
        public static Sprite SelectRing => _selectRing ?? (_selectRing = Ring("selectRing", new Color(1f, 0.85f, 0.25f, 0.95f)));

        /// <summary>按「阵营 + 棋子类型」取得棋子占位图（带缓存）。</summary>
        public static Sprite Piece(string side, string type)
        {
            var key = side + "|" + (type ?? "");
            if (PieceCache.TryGetValue(key, out var cached) && cached != null) return cached;
            var sprite = BuildPiece(side, type);
            PieceCache[key] = sprite;
            return sprite;
        }

        // ── 棋子：形状区分类型，配色区分阵营 ───────────────────

        enum Shape { Circle, Square, Diamond, Hexagon, Pentagon, Elephant, Cross }

        static Sprite BuildPiece(string side, string type)
        {
            Color body, rim;
            switch (side)
            {
                case "red": body = new Color(0.85f, 0.26f, 0.21f); rim = new Color(0.44f, 0.08f, 0.06f); break;
                case "white": body = new Color(0.95f, 0.95f, 0.93f); rim = new Color(0.24f, 0.24f, 0.27f); break;
                default: body = new Color(0.23f, 0.25f, 0.31f); rim = new Color(0.06f, 0.07f, 0.10f); break;
            }

            var shape = ShapeFor(NormalizeType(type));
            var tex = NewTexture();
            var px = new Color[Size * Size];
            var c = Size / 2f;

            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var fx = x + 0.5f;
                var fy = y + 0.5f;
                var i = y * Size + x;
                if (InsideShape(shape, fx, fy, c, 0f)) px[i] = body;
                else if (InsideShape(shape, fx, fy, c, 2f)) px[i] = rim;   // 内缩一圈作为描边
            }
            return Finish(tex, side + "_" + NormalizeType(type));
        }

        /// <summary>各棋类内部命名不统一（soldier/pawn、chariot/rook…），统一归一化后再映射形状。</summary>
        static string NormalizeType(string type)
        {
            switch (type)
            {
                case "pawn": case "soldier": return "soldier";
                case "rook": case "chariot": return "chariot";
                case "knight": case "horse": return "horse";
                case "bishop": case "elephant": return "elephant";
                case "queen": case "advisor": return "advisor";
                case "king": case "general": return "general";
                default: return string.IsNullOrEmpty(type) ? "unknown" : type;
            }
        }

        static Shape ShapeFor(string t)
        {
            switch (t)
            {
                case "general": return Shape.Diamond;
                case "advisor": return Shape.Hexagon;
                case "elephant": return Shape.Elephant;
                case "horse": return Shape.Pentagon;
                case "chariot": return Shape.Square;
                case "cannon": return Shape.Circle;
                case "soldier": return Shape.Cross;
                case "stone": return Shape.Diamond;
                default: return Shape.Circle;
            }
        }

        /// <summary>shrink 为内缩量（像素），用于生成描边。</summary>
        static bool InsideShape(Shape shape, float x, float y, float c, float shrink)
        {
            switch (shape)
            {
                case Shape.Circle:
                {
                    var r = c - 4f - shrink;
                    var dx = x - c;
                    var dy = y - c;
                    return dx * dx + dy * dy <= r * r;
                }
                case Shape.Square:
                {
                    var lo = 4f + shrink;
                    var hi = Size - 4f - shrink;
                    return x >= lo && x <= hi && y >= lo && y <= hi;
                }
                case Shape.Diamond:
                    return Mathf.Abs(x - c) + Mathf.Abs(y - c) <= c - 3f - shrink;
                case Shape.Hexagon:
                    return InPolygon(x, y, c, c, c - 4f - shrink, 6, Mathf.PI / 6f);
                case Shape.Pentagon:
                    return InPolygon(x, y, c, c, c - 4f - shrink, 5, -Mathf.PI / 2f);
                case Shape.Elephant:
                {
                    var r = c - 5f - shrink;
                    var dx = x - c;
                    var dy = y - c;
                    return dx * dx + dy * dy <= r * r ||
                           (y <= 4f + shrink && y >= 2f && x >= 4f + shrink && x <= Size - 4f - shrink);
                }
                case Shape.Cross:
                {
                    var t = 7f + shrink * 0.5f;
                    var arm = 6f + shrink * 0.5f;
                    var v = Mathf.Abs(x - c) <= t && Mathf.Abs(y - c) <= c - arm;
                    var h = Mathf.Abs(y - c) <= t && Mathf.Abs(x - c) <= c - arm;
                    return v || h;
                }
                default: return false;
            }
        }

        static bool InPolygon(float px, float py, float cx, float cy, float r, int sides, float rot)
        {
            var ang = Mathf.Atan2(py - cy, px - cx) - rot;
            var seg = 2f * Mathf.PI / sides;
            ang -= seg * Mathf.Floor(ang / seg + 0.5f);          // 折叠到单个扇区
            var rr = r * Mathf.Cos(Mathf.PI / sides) / Mathf.Cos(ang);
            var dx = px - cx;
            var dy = py - cy;
            return dx * dx + dy * dy <= rr * rr;
        }

        // ── 基础图形 ────────────────────────────────────────────

        static Sprite RoundedRect(string key, Color body)
        {
            var tex = NewTexture();
            var px = new Color[Size * Size];
            var c = Size / 2f;
            var r = 7f;
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var fx = x + 0.5f - c;
                var fy = y + 0.5f - c;
                var dx = Mathf.Max(Mathf.Abs(fx) - (c - r), 0f);
                var dy = Mathf.Max(Mathf.Abs(fy) - (c - r), 0f);
                if (dx * dx + dy * dy <= r * r) px[y * Size + x] = body;
            }
            return Finish(tex, key);
        }

        static Sprite Disc(string key, Color body)
        {
            var tex = NewTexture();
            var px = new Color[Size * Size];
            var c = Size / 2f;
            var r = c - 7f;
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var dx = x + 0.5f - c;
                var dy = y + 0.5f - c;
                if (dx * dx + dy * dy <= r * r) px[y * Size + x] = body;
            }
            return Finish(tex, key);
        }

        static Sprite Ring(string key, Color body)
        {
            var tex = NewTexture();
            var px = new Color[Size * Size];
            var c = Size / 2f;
            var outer = c - 4f;
            var inner = c - 9f;
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var dx = x + 0.5f - c;
                var dy = y + 0.5f - c;
                var d2 = dx * dx + dy * dy;
                if (d2 <= outer * outer && d2 >= inner * inner) px[y * Size + x] = body;
            }
            return Finish(tex, key);
        }

        // ── 纹理工具 ────────────────────────────────────────────

        static Texture2D NewTexture() => new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };

        static Sprite Finish(Texture2D tex, string key)
        {
            tex.Apply(false, false);
            var s = Sprite.Create(tex, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f), PixelsPerUnit);
            s.name = "ph_" + key;
            s.hideFlags = HideFlags.HideAndDontSave;
            return s;
        }
    }
}