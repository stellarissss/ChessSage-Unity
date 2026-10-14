using ChessSage.Core.Overworld;
using UnityEngine;

namespace ChessSage.Presentation.Overworld
{
    /// <summary>
    /// 分块加载窗口的统一计算。
    ///
    /// 地形层与装饰层必须用<b>同一套</b>窗口规则，否则会出现"地形有、装饰没有"
    /// 的错位（动画上表现为装饰在地块边缘突然出现/消失）。集中一处避免两处漂移。
    ///
    /// 规则：把"玩家 ±margin 格"的可见矩形投影到块坐标。相较对称的"块半径 N"，
    /// 这样能在保证屏幕无空洞的前提下，少建约 1/3 的角落块。
    /// </summary>
    internal static class OverworldWindow
    {
        public static void Compute(OverworldMap map, int chunksX, int chunksY,
            int cellX, int cellY, int margin,
            out int minCx, out int minCy, out int maxCx, out int maxCy)
        {
            minCx = Mathf.Max(0, ChunkManager.ChunkOf(Mathf.Max(0, cellX - margin)));
            maxCx = Mathf.Min(chunksX - 1, ChunkManager.ChunkOf(Mathf.Min(map.Width - 1, cellX + margin)));
            minCy = Mathf.Max(0, ChunkManager.ChunkOf(Mathf.Max(0, cellY - margin)));
            maxCy = Mathf.Min(chunksY - 1, ChunkManager.ChunkOf(Mathf.Min(map.Height - 1, cellY + margin)));
        }
    }
}
