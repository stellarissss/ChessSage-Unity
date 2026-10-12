namespace ChessSage.Core.Board
{
    /// <summary>
    /// 棋盘布局抽象：把「格坐标 ⇄ 棋盘平面局部坐标」的纯数学从表现层下沉到 Core。
    ///
    /// 【为什么需要它】M5 之前 <c>BoardView.CellToLocal/LocalToCell</c> 硬编码矩形假设，
    /// 跳棋的六角星棋盘（双倍列坐标）在表现层根本无法表达；而坐标变换一旦算错，
    /// 症状是「点 A 落 B」——这类缺陷在规则引擎的黄金测试里完全不可见。
    /// 下沉到 Core 后，布局数学可被 L1（dotnet test）全量覆盖：往返变换、越界拒绝、
    /// 六角星邻接与 legacy 配置的一致性都能在无 Unity 环境下断言。
    ///
    /// 【坐标约定】(x, y) 即规则引擎使用的格坐标（<c>Piece.X</c>/<c>Piece.Y</c>）：
    ///   · 矩形棋种（象棋/动物棋/围棋/五子棋/黑白棋）：x 为列、y 为行；
    ///   · 跳棋（六角星）：x 为<b>行号</b>、y 为<b>双倍列号</b>——这是 Core 既有约定
    ///     （<c>TiaoqiRuleEngine</c> 注释：Piece.X/Y 对应 Python 的 [row, col]），
    ///     布局层只负责投影，不改变引擎语义。
    ///
    /// 【单位】局部坐标以「格」为单位（矩形 1 格 = 1 单位；六角星横距 1、纵距 1），
    /// 不含任何缩放与锚点——世界缩放（CellSize）与居中由表现层叠加。
    /// 这样布局对象是不可变纯数学，运行时改格宽无需重建布局。
    /// </summary>
    public interface IBoardLayout
    {
        /// <summary>合法格位数（矩形=宽×高；六角星=121）。</summary>
        int CellCount { get; }

        /// <summary>x 方向跨度（矩形=宽；六角星=行数）。</summary>
        int CellsX { get; }

        /// <summary>y 方向跨度（矩形=高；六角星=双倍列取值数）。</summary>
        int CellsY { get; }

        /// <summary>该格坐标是否为合法格位（矩形看边界，六角星还要排除星形空洞）。</summary>
        bool IsValid(int x, int y);

        /// <summary>
        /// 格坐标 → 局部坐标（格单位）。非法格位返回 false 且输出 (0,0)。
        /// </summary>
        bool TryCellToLocal(int x, int y, out double localX, out double localY);

        /// <summary>
        /// 局部坐标 → 格坐标（含越界/空洞拒绝）。
        /// 吸附半径与既有矩形实现一致：四舍五入到最近格心，落在两格缝隙的点会被拒绝，
        /// 宁可漏点击也绝不误判到相邻格。
        /// </summary>
        bool TryLocalToCell(double localX, double localY, out int x, out int y);

        /// <summary>合法格位的局部坐标包络（minX, minY, maxX, maxY），供表现层做居中。</summary>
        void GetBounds(out double minX, out double minY, out double maxX, out double maxY);
    }
}
