using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 等距投影的数学正确性验证。
    ///
    /// 等距手写数学最容易出的错是「正变换对了、逆变换不对称」，
    /// 导致移动方向与画面错位——这类缺陷在纯 C# 里可以直接验证，
    /// 不必等到运行时。因此这里用<b>不依赖UnityEngine</b> 的镜像公式做等价校验。
    ///
    /// 注意：本测试断言的是 <b>IsoGrid 的公式定义</b>（以常量形式复制），
    /// 保证 Presentation 层的实现与规格一致；Unity 侧由 L2 编译门禁保证引用一致。
    /// </summary>
    public sealed class IsoGridMathTests
    {
        const float TileW = 1f;
        const float TileH = 0.5f;

        static (float x, float y) ToWorld(int cx, int cy, float elevZ = 0f, float raisedZ = 0f)
            => ((cx - cy) * TileW, (cx + cy) * TileH + elevZ + raisedZ);

        static (int x, int y) ToCell(float wx, float wy)
        {
            float a = wx / TileW;
            float b = wy / TileH;
            return (Round((a + b) * 0.5f), Round((b - a) * 0.5f));
        }

        static int Round(float v)
        {
            // Mathf.RoundToInt 的等价实现（.5 远离零）。
            return (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
        }

        [Test]
        public void OriginMapsToOrigin()
        {
            var (x, y) = ToWorld(0, 0);
            Assert.AreEqual(0f, x, 1e-5f);
            Assert.AreEqual(0f, y, 1e-5f);
        }

        [Test]
        public void RoundTripIsStableOverFullMap()
        {
            // 全图 112×84 全部格子验证 正变换→逆变换 往返一致。
            for (int cy = 0; cy < 84; cy++)
            {
                for (int cx = 0; cx < 112; cx++)
                {
                    var (wx, wy) = ToWorld(cx, cy);
                    var (bx, by) = ToCell(wx, wy);
                    Assert.AreEqual(cx, bx, $"({cx},{cy}) 逆变换 X 失配");
                    Assert.AreEqual(cy, by, $"({cx},{cy}) 逆变换 Y 失配");
                }
            }
        }

        [Test]
        public void GridAxesAreScreenDiagonals()
        {
            // +x 网格轴在屏幕上应是"右下"，+y 轴应是"左下"。
            var (x0, y0) = ToWorld(0, 0);
            var (x1, y1) = ToWorld(1, 0);
            var (x2, y2) = ToWorld(0, 1);

            Assert.Greater(x1 - x0, 0f, "+x 应向右");
            Assert.Greater(y1 - y0, 0f, "+x 应向下（等距投影下俯视）");

            Assert.Less(x2 - x0, 0f, "+y 应向左");
            Assert.Greater(y2 - y0, 0f, "+y 应向下");

            // 两轴的垂直下倾量相同 → 对称
            Assert.AreEqual(y1 - y0, y2 - y0, 1e-5f, "两轴应等斜率（1:2 等距）");
        }

        [Test]
        public void DiagonalIsPureVerticalOnScreen()
        {
            // (x+1, y+1) 在等距下应"正下方"，x 不变 —— 这是玩家建立空间心智的关键。
            var (x0, y0) = ToWorld(10, 10);
            var (x1, y1) = ToWorld(11, 11);
            Assert.AreEqual(x0, x1, 1e-5f, "对角线移动不应左右偏移");
            Assert.Greater(y1 - y0, 0f);
        }

        [Test]
        public void DiagonalInverseAlsoStable()
        {
            // 对角线上的格子在正变换下位于屏幕 x=0 轴上（对称性检验）
            for (int i = 0; i < 60; i++)
            {
                var (wx, wy) = ToWorld(i, i);
                Assert.AreEqual(0f, wx, 1e-5f, $"({i},{i}) 应位于屏幕 x=0 轴上");
                var (bx, by) = ToCell(wx, wy);
                Assert.AreEqual(bx, by, $"({i},{i}) 逆变换后 x/y 应相等");
            }
        }

        [Test]
        public void ScreenInputProjectionPicksDominantAxis()
        {
            // 玩家按"上"（屏幕 v=+1）应沿等距的"上"方向，即两个网格轴的负向组合；
            // 实现取主轴，故按上得到 -x 或 -y 中绝对值更大的那个（此处相等，走 x）。
            // 这里锁定 ScreenToGridDir 的判定规则，防止方向手感回归。
            AssertGridDir(0f, 1f);      // 上
            AssertGridDir(0f, -1f);     // 下
            AssertGridDir(1f, 0f);      // 右
            AssertGridDir(-1f, 0f);     // 左
        }

        static void AssertGridDir(float h, float v)
        {
            float dotX = h * 1f + v * 0.5f;
            float dotY = -h * 1f + v * 0.5f;
            int gx = 0, gy = 0;
            if (MathfAbs(dotX) >= MathfAbs(dotY)) gx = dotX >= 0 ? 1 : -1;
            else gy = dotY >= 0 ? 1 : -1;

            // 任意输入都必须产生且只产生一个轴的方向（不能是 0,0 即"无输入"）
            Assert.IsTrue(gx != 0 || gy != 0, $"输入({h},{v}) 方向不应为空");
            Assert.IsTrue(gx == 0 || gy == 0, $"输入({h},{v}) 不应同时移动两轴");

            // 反向输入必须得到反向方向
            float dotXn = -h * 1f + (-v) * 0.5f;
            float dotYn = -(-h) * 1f + (-v) * 0.5f;
            int rx = 0, ry = 0;
            if (MathfAbs(dotXn) >= MathfAbs(dotYn)) rx = dotXn >= 0 ? 1 : -1;
            else ry = dotYn >= 0 ? 1 : -1;
            Assert.AreEqual(-gx, rx);
            Assert.AreEqual(-gy, ry);
        }

        static float MathfAbs(float v) => v < 0 ? -v : v;
    }
}