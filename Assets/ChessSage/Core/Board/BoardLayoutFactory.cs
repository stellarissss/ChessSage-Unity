using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Board
{
    /// <summary>
    /// 布局工厂：按 board.json 配置选择布局实现。
    ///
    /// 【为什么放 Core】「哪个棋种是什么棋盘形状」是数据契约的一部分，与规则引擎同源；
    /// 且形状判定一旦写进表现层，六角星分支就会散落到 UnityEngine 代码里，
    /// 失去 L1 覆盖。放 Core 后判定逻辑可被单测直接断言。
    ///
    /// 判定规则（数据驱动，不硬编码棋种 id）：
    ///   · geometry.board_type == "hexagonal_star" 或 coord_system == "doubled_column"
    ///     → <see cref="HexStarLayout"/>；
    ///   · 否则 → <see cref="RectLayout"/>（geometry.width/height，缺省 9×10，
    ///     与 <c>VariantRuleBase</c> 的默认几何保持一致）。
    /// </summary>
    public static class BoardLayoutFactory
    {
        /// <summary>RectLayout 的缺省尺寸，与 VariantRuleBase.Width/Height 的缺省值一致。</summary>
        public const int DefaultWidth = 9;
        public const int DefaultHeight = 10;

        public static IBoardLayout FromBoardConfig(JObject board)
        {
            if (board == null) return new RectLayout(DefaultWidth, DefaultHeight);

            var geometry = board["geometry"] as JObject;
            var boardType = geometry?["board_type"]?.Value<string>();
            var coordSystem = geometry?["coord_system"]?.Value<string>();

            bool hexStar = boardType == "hexagonal_star" || coordSystem == "doubled_column";
            if (hexStar) return new HexStarLayout(board);

            int width = geometry?["width"]?.Value<int>() ?? DefaultWidth;
            int height = geometry?["height"]?.Value<int>() ?? DefaultHeight;
            return new RectLayout(width, height);
        }
    }
}
