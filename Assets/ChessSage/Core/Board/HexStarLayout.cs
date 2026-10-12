using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Board
{
    /// <summary>
    /// 中国跳棋六角星棋盘布局（双倍列坐标，121 位）。
    ///
    /// 【几何真源】一比一移植自 legacy-web：
    ///   · 合法格位集合 = <c>board.json</c> 的 <c>positions</c>（"row,col" → true）；
    ///   · 局部坐标 = <c>pixel_coords</c> 语义：<c>real_x = col / 2</c>、<c>real_y = row</c>
    ///     （见 legacy-web/tiaoqi/generate_board.py 与 static/app.js 的 _calculatePixelCoords，
    ///     「保持横纵等距」）。L1 测试对全部 121 位逐一断言该等式，防止移植漂移。
    ///
    /// 【为什么双构造】配置路径（<see cref="HexStarLayout(JObject)"/>）是运行时唯一真源；
    /// 生成式路径（<see cref="HexStarLayout(int,int)"/>）按标准星形公式展开，
    /// 用于无 positions 的配置兜底与不依赖配置文件的单测。两条路径在标准参数
    /// （rows=17, max_col=24）下必须逐格一致——由测试交叉断言。
    ///
    /// 【坐标提醒】本布局的 (x, y) = (行号, 双倍列号)，与矩形棋种的 (列, 行) 相反，
    /// 这是 Core 既有约定（Piece.X/Y 对应 Python [row, col]），详见 <see cref="IBoardLayout"/>。
    /// </summary>
    public sealed class HexStarLayout : IBoardLayout
    {
        /// <summary>标准中国跳棋：17 行。</summary>
        public const int StandardRows = 17;

        /// <summary>标准中国跳棋：双倍列最大值 24（实列 0..12）。</summary>
        public const int StandardMaxCol = 24;

        /// <summary>标准中国跳棋格位数。</summary>
        public const int StandardCellCount = 121;

        readonly HashSet<long> _valid = new HashSet<long>();
        readonly int _rows;
        readonly int _maxCol;

        double _minX, _minY, _maxX, _maxY;

        /// <summary>行数（x 跨度）。</summary>
        public int Rows => _rows;

        /// <summary>双倍列最大值（y 跨度 = _maxCol + 1 个取值）。</summary>
        public int MaxCol => _maxCol;

        public int CellCount => _valid.Count;

        /// <summary>x 跨度 = 行数。</summary>
        public int CellsX => _rows;

        /// <summary>y 跨度 = 双倍列取值数（0..maxCol）。</summary>
        public int CellsY => _maxCol + 1;

        /// <summary>
        /// 配置路径：从 board.json 读取 positions 作为合法格位真源；
        /// rows/max_col 缺省时回退标准值；positions 缺失时按标准星形生成式展开。
        /// </summary>
        public HexStarLayout(JObject boardConfig)
        {
            var geometry = boardConfig?["geometry"] as JObject;
            var positions = boardConfig?["positions"] as JObject;

            if (positions != null && positions.Count > 0)
            {
                // 数据驱动：跨度也从数据推导，避免配置改写 rows/max_col 后边界失真。
                int maxRow = 0, maxCol = 0;
                foreach (var prop in positions.Properties())
                {
                    if (!TryParseKey(prop.Name, out int r, out int c)) continue;
                    _valid.Add(Key(r, c));
                    if (r > maxRow) maxRow = r;
                    if (c > maxCol) maxCol = c;
                }
                _rows = Math.Max(maxRow + 1, geometry?["rows"]?.Value<int>() ?? 0);
                _maxCol = Math.Max(maxCol, geometry?["max_col"]?.Value<int>() ?? 0);
            }
            else
            {
                _rows = geometry?["rows"]?.Value<int>() ?? StandardRows;
                _maxCol = geometry?["max_col"]?.Value<int>() ?? StandardMaxCol;
                GenerateStandardStar();
            }

            RecomputeBounds();
        }

        /// <summary>
        /// 生成式路径：标准六角星（中国跳棋）。rows=17/max_col=24 即 121 位；
        /// rows=13 即 79 位变体。行数推导：
        ///   臂长 arm = (rows - 9) / 2，中带行数 midCount = (rows + 1) / 2；
        ///   顶部臂 r &lt; arm：该行 count = r + 1；底部臂 r ≥ rows-arm：count = rows - r；
        ///   中带行：count = midCount + |r - rows/2|；
        ///   列从 -(count-1) 起步长 2，偏移 max_col/2 使列非负（与 generate_board.py 一致）。
        /// </summary>
        public HexStarLayout(int rows, int maxCol)
        {
            if (rows < 9 || rows % 2 == 0)
                throw new ArgumentException($"六角星行数须为 ≥9 的奇数，收到 {rows}", nameof(rows));
            if (maxCol < 2 || maxCol % 2 != 0)
                throw new ArgumentException($"六角星 max_col 须为偶数，收到 {maxCol}", nameof(maxCol));

            _rows = rows;
            _maxCol = maxCol;
            GenerateStandardStar();
            RecomputeBounds();
        }

        void GenerateStandardStar()
        {
            int arm = (_rows - 9) / 2;
            int midCount = (_rows + 1) / 2;
            int shift = _maxCol / 2;
            for (int r = 0; r < _rows; r++)
            {
                int count = r < arm ? r + 1
                    : r >= _rows - arm ? _rows - r
                    : midCount + Math.Abs(r - _rows / 2);
                for (int i = 0; i < count; i++)
                    _valid.Add(Key(r, -(count - 1) + i * 2 + shift));
            }
        }

        void RecomputeBounds()
        {
            _minX = double.MaxValue;
            _minY = double.MaxValue;
            _maxX = double.MinValue;
            _maxY = double.MinValue;
            foreach (var key in _valid)
            {
                // 反推 (x, y)：高位 32 位是 x（行），低位是 y（双倍列）。
                int x = (int)(key >> 32);
                int y = (int)(key & 0xFFFFFFFF);
                double lx = y * 0.5, ly = x;
                if (lx < _minX) _minX = lx;
                if (lx > _maxX) _maxX = lx;
                if (ly < _minY) _minY = ly;
                if (ly > _maxY) _maxY = ly;
            }
            if (_valid.Count == 0)
            {
                _minX = _minY = _maxX = _maxY = 0;
            }
        }

        public bool IsValid(int x, int y)
            => x >= 0 && x < _rows && y >= 0 && y <= _maxCol && _valid.Contains(Key(x, y));

        public bool TryCellToLocal(int x, int y, out double localX, out double localY)
        {
            if (!IsValid(x, y))
            {
                localX = 0;
                localY = 0;
                return false;
            }
            // legacy 语义：real_x = 双倍列 / 2，real_y = 行号（横纵等距）。
            localX = y * 0.5;
            localY = x;
            return true;
        }

        public bool TryLocalToCell(double localX, double localY, out int x, out int y)
        {
            x = RectLayout.RoundHalfAwayFromZero(localY);        // 行
            y = RectLayout.RoundHalfAwayFromZero(localX * 2.0);  // 双倍列
            return IsValid(x, y);
        }

        public void GetBounds(out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = _minX;
            minY = _minY;
            maxX = _maxX;
            maxY = _maxY;
        }

        static long Key(int x, int y) => ((long)x << 32) | (uint)y;

        static bool TryParseKey(string key, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrEmpty(key)) return false;
            var parts = key.Split(',');
            if (parts.Length != 2) return false;
            return int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
        }
    }
}
