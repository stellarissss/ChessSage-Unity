using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 棋盘尺寸读取（兼容两种真实布局）：
    ///   - 扁平：<c>{ "width": 9, "height": 10 }</c>（E0 夹具/旧契约）
    ///   - 嵌套：<c>{ "geometry": { "width": 9, "height": 10 } }</c>（StreamingAssets 真实 board.json）
    ///
    /// 【缺陷背景 E3-S2】路径白名单、move_piece 边界校验、BoardSummarizer 原先只读顶层，
    /// 真实棋盘的尺寸在 geometry 下 ⇒ 改尺寸必被 PATH_REJECTED、移子越界校验静默失效。
    /// 本类为唯一读取口，避免三处各写一份 fallback。
    /// </summary>
    public static class BoardGeometry
    {
        public static int Width(JObject board) => Read(board, "width");

        public static int Height(JObject board) => Read(board, "height");

        static int Read(JObject board, string key)
        {
            if (board == null) return 0;
            var flat = board[key];
            if (flat != null && flat.Type == JTokenType.Integer)
                return flat.Value<int>();
            var geometry = board["geometry"] as JObject;
            var nested = geometry?[key];
            return nested != null && nested.Type == JTokenType.Integer ? nested.Value<int>() : 0;
        }
    }
}
