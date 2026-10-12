using System.Collections.Generic;
using ChessSage.Core.Board;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 棋盘布局数学的引擎外验证（L1）。
    ///
    /// 布局是「点哪落哪」的唯一真源：正/逆变换不对称的症状是「点格子 A、棋子落到 B」，
    /// 而这种缺陷在规则引擎的黄金测试里完全不可见（规则只认坐标，不认投影）。
    /// 因此这里对往返变换、越界/空洞拒绝、六角星邻接一致性、
    /// 以及与 legacy 配置（pixel_coords / adjacency）的一比一一致性做全量断言。
    /// </summary>
    public sealed class BoardLayoutTests
    {
        static JObject TiaoqiBoard() => TestEnv.LoadConfig("tiaoqi", "board");

        // ── RectLayout：既有矩形行为的契约固化 ──────────────────────

        [Test]
        public void RectLayout_CountAndBounds()
        {
            var r = new RectLayout(9, 10);
            Assert.AreEqual(90, r.CellCount);
            Assert.AreEqual(9, r.CellsX);
            Assert.AreEqual(10, r.CellsY);
            r.GetBounds(out double minX, out double minY, out double maxX, out double maxY);
            Assert.AreEqual(0d, minX);
            Assert.AreEqual(0d, minY);
            Assert.AreEqual(8d, maxX);
            Assert.AreEqual(9d, maxY);
        }

        [Test]
        public void RectLayout_ClampsDegenerateSizes()
        {
            var r = new RectLayout(0, -5);
            Assert.AreEqual(1, r.CellsX);
            Assert.AreEqual(1, r.CellsY);
            Assert.IsTrue(r.IsValid(0, 0));
        }

        [Test]
        public void RectLayout_RoundTripEveryCell()
        {
            var r = new RectLayout(9, 10);
            for (int x = 0; x < 9; x++)
            {
                for (int y = 0; y < 10; y++)
                {
                    Assert.IsTrue(r.TryCellToLocal(x, y, out double lx, out double ly), $"({x},{y}) 正变换");
                    Assert.AreEqual((double)x, lx, 1e-9);
                    Assert.AreEqual((double)y, ly, 1e-9);
                    Assert.IsTrue(r.TryLocalToCell(lx, ly, out int bx, out int by), $"({x},{y}) 逆变换");
                    Assert.AreEqual(x, bx, $"({x},{y}) 往返 x 失配");
                    Assert.AreEqual(y, by, $"({x},{y}) 往返 y 失配");
                }
            }
        }

        [Test]
        public void RectLayout_RejectsOutOfBoard()
        {
            var r = new RectLayout(9, 10);
            Assert.IsFalse(r.IsValid(-1, 0));
            Assert.IsFalse(r.IsValid(9, 0));
            Assert.IsFalse(r.IsValid(0, 10));
            Assert.IsFalse(r.TryCellToLocal(9, 0, out _, out _));

            // 半格之外拒绝；半格之内吸附到最近格心
            Assert.IsFalse(r.TryLocalToCell(-0.6, 0, out _, out _), "左外半格应拒绝");
            Assert.IsFalse(r.TryLocalToCell(8.6, 0, out _, out _), "右外半格应拒绝");
            Assert.IsFalse(r.TryLocalToCell(0, 9.6, out _, out _), "上外半格应拒绝");
            Assert.IsTrue(r.TryLocalToCell(8.4, 9.4, out int ex, out int ey));
            Assert.AreEqual(8, ex);
            Assert.AreEqual(9, ey);
        }

        // ── HexStarLayout：六角星几何 ───────────────────────────────

        [Test]
        public void HexStar_ConfigDriven_MatchesGoldenGeometry()
        {
            var layout = new HexStarLayout(TiaoqiBoard());
            Assert.AreEqual(121, layout.CellCount, "六角星标准 121 位");
            Assert.AreEqual(17, layout.CellsX, "x 跨度 = 行数");
            Assert.AreEqual(25, layout.CellsY, "y 跨度 = 双倍列取值数（0..24）");

            var positions = (JObject)TiaoqiBoard()["positions"];
            Assert.AreEqual(positions.Count, layout.CellCount, "合法格位数应等于配置 positions 数");
            foreach (var prop in positions.Properties())
            {
                var parts = prop.Name.Split(',');
                int row = int.Parse(parts[0]);
                int col = int.Parse(parts[1]);
                Assert.IsTrue(layout.IsValid(row, col), $"配置位置 ({row},{col}) 应合法");
            }
        }

        [Test]
        public void HexStar_RejectsStarHoles()
        {
            var layout = new HexStarLayout(TiaoqiBoard());
            Assert.IsFalse(layout.IsValid(0, 0), "顶臂只有 (0,12) 一位");
            Assert.IsFalse(layout.IsValid(0, 11));
            Assert.IsFalse(layout.IsValid(1, 12), "奇数行只允许奇数列");
            Assert.IsFalse(layout.IsValid(4, 1), "中带偶数行只允许偶数列");
            Assert.IsFalse(layout.IsValid(8, 0), "第 8 行列范围 4..20");
            Assert.IsFalse(layout.IsValid(16, 0));
            Assert.IsFalse(layout.IsValid(17, 12), "行越界");
            Assert.IsFalse(layout.IsValid(0, 26), "列越界");
            Assert.IsFalse(layout.IsValid(-1, 12), "负行");
            Assert.IsTrue(layout.IsValid(8, 12), "星心");
            Assert.IsTrue(layout.IsValid(0, 12), "顶臂尖");
            Assert.IsTrue(layout.IsValid(16, 12), "底臂尖");
            Assert.IsTrue(layout.IsValid(4, 0), "中带最左");
            Assert.IsTrue(layout.IsValid(4, 24), "中带最右");
        }

        [Test]
        public void HexStar_RoundTripEveryCell()
        {
            var layout = new HexStarLayout(TiaoqiBoard());
            var positions = (JObject)TiaoqiBoard()["positions"];
            foreach (var prop in positions.Properties())
            {
                var parts = prop.Name.Split(',');
                int row = int.Parse(parts[0]);
                int col = int.Parse(parts[1]);
                Assert.IsTrue(layout.TryCellToLocal(row, col, out double lx, out double ly), $"{prop.Name} 正变换");
                Assert.IsTrue(layout.TryLocalToCell(lx, ly, out int bx, out int by), $"{prop.Name} 逆变换");
                Assert.AreEqual(row, bx, $"{prop.Name} 行往返失配");
                Assert.AreEqual(col, by, $"{prop.Name} 列往返失配");
            }
        }

        [Test]
        public void HexStar_LocalCoordsMatchLegacyPixelCoords()
        {
            // 一比一移植的硬证据：布局局部坐标必须等于 legacy pixel_coords（real_x=col/2, real_y=row）。
            var board = TiaoqiBoard();
            var layout = new HexStarLayout(board);
            var pixel = (JObject)board["pixel_coords"];
            Assert.AreEqual(121, pixel.Count, "pixel_coords 应覆盖全部格位");
            foreach (var prop in pixel.Properties())
            {
                var parts = prop.Name.Split(',');
                int row = int.Parse(parts[0]);
                int col = int.Parse(parts[1]);
                Assert.IsTrue(layout.TryCellToLocal(row, col, out double lx, out double ly));
                var p = (JObject)prop.Value;
                Assert.AreEqual(p["x"].Value<double>(), lx, 1e-9, $"{prop.Name} 的 x 与 legacy 不一致");
                Assert.AreEqual(p["y"].Value<double>(), ly, 1e-9, $"{prop.Name} 的 y 与 legacy 不一致");
            }
        }

        [Test]
        public void HexStar_GenerativeMatchesConfigDriven()
        {
            // 生成式路径与配置路径必须逐格一致——否则"无 positions 兜底"会静默改变棋盘形状。
            var fromConfig = new HexStarLayout(TiaoqiBoard());
            var generative = new HexStarLayout(HexStarLayout.StandardRows, HexStarLayout.StandardMaxCol);
            Assert.AreEqual(fromConfig.CellCount, generative.CellCount);
            for (int r = 0; r < HexStarLayout.StandardRows; r++)
            {
                for (int c = 0; c <= HexStarLayout.StandardMaxCol; c++)
                {
                    Assert.AreEqual(fromConfig.IsValid(r, c), generative.IsValid(r, c),
                        $"({r},{c}) 生成式与配置路径不一致");
                }
            }
        }

        [Test]
        public void HexStar_GenerativeRejectsUnsupportedSizes()
        {
            Assert.Throws<System.ArgumentException>(() => new HexStarLayout(16, 24), "偶数行应拒绝");
            Assert.Throws<System.ArgumentException>(() => new HexStarLayout(17, 25), "奇数列应拒绝");
        }

        [Test]
        public void HexStar_AdjacencyAgreesWithConfig()
        {
            // 邻接一致性：用「布局合法集 + 六向偏移」推出的邻接必须与 board.json adjacency 一致。
            // 跳棋 step/hop 全部基于邻接表，几何与规则一旦漂移，症状是"能跳到洞外"。
            var board = TiaoqiBoard();
            var layout = new HexStarLayout(board);
            var adjacency = (JObject)board["adjacency"];
            var offsets = new (int dr, int dc)[]
            {
                (0, -2), (0, 2), (-1, -1), (-1, 1), (1, -1), (1, 1),
            };

            Assert.AreEqual(adjacency.Count, layout.CellCount, "邻接表应覆盖全部格位");
            foreach (var prop in adjacency.Properties())
            {
                var parts = prop.Name.Split(',');
                int row = int.Parse(parts[0]);
                int col = int.Parse(parts[1]);

                var expected = new HashSet<string>();
                foreach (JArray n in (JArray)prop.Value)
                    expected.Add(((int)n[0]) + "," + ((int)n[1]));

                var actual = new HashSet<string>();
                foreach (var (dr, dc) in offsets)
                {
                    int nr = row + dr;
                    int nc = col + dc;
                    if (layout.IsValid(nr, nc)) actual.Add(nr + "," + nc);
                }

                // 注意：必须用无序等价比较。NUnit 的 Assert.AreEqual 对集合是
                // <b>有序</b>逐元素比较，而 HashSet 的枚举顺序取决于哈希分布——
                // 两个内容完全相同的 HashSet 会因枚举序不同而误报失败。
                // 本测试只关心邻接集合的语义等价，故用 Is.EquivalentTo。
                Assert.That(actual, Is.EquivalentTo(expected), $"{prop.Name} 的邻接与配置不一致");
            }
        }

        [Test]
        public void HexStar_Bounds()
        {
            var layout = new HexStarLayout(TiaoqiBoard());
            layout.GetBounds(out double minX, out double minY, out double maxX, out double maxY);
            Assert.AreEqual(0d, minX, 1e-9);
            Assert.AreEqual(0d, minY, 1e-9);
            Assert.AreEqual(12d, maxX, 1e-9);
            Assert.AreEqual(16d, maxY, 1e-9);
        }

        [Test]
        public void HexStar_InverseRejectsHolesAndOutside()
        {
            var layout = new HexStarLayout(TiaoqiBoard());
            // 两格之间的缝隙：中带 (4,0) 与 (4,2) 的中点 local x=0.5 → 列四舍五入为 1（奇列）→ 拒绝
            Assert.IsFalse(layout.TryLocalToCell(0.5, 4, out _, out _), "列缝隙必须拒绝");
            // 顶臂上方空白
            Assert.IsFalse(layout.TryLocalToCell(6.0, -0.6, out _, out _), "星外必须拒绝");
            // 格心附近吸附：(1,11) 的局部坐标 (5.5, 1)
            Assert.IsTrue(layout.TryLocalToCell(5.7, 0.9, out int x, out int y));
            Assert.AreEqual(1, x);
            Assert.AreEqual(11, y);
        }

        // ── 工厂：形状判定 ──────────────────────────────────────────

        [Test]
        public void Factory_TiaoqiIsHexStar()
        {
            var layout = BoardLayoutFactory.FromBoardConfig(TiaoqiBoard());
            Assert.IsInstanceOf<HexStarLayout>(layout);
            Assert.AreEqual(121, layout.CellCount);
        }

        [Test]
        public void Factory_RectVariantsAreRect()
        {
            foreach (var id in new[] { "xiangqi", "dongwuqi", "weiqi", "wuziqi", "heibaiqi" })
            {
                var layout = BoardLayoutFactory.FromBoardConfig(TestEnv.LoadConfig(id, "board"));
                Assert.IsInstanceOf<RectLayout>(layout, id + " 应为矩形布局");
            }
        }

        [Test]
        public void Factory_NullBoardFallsBackToDefaultRect()
        {
            var layout = BoardLayoutFactory.FromBoardConfig(null);
            Assert.IsInstanceOf<RectLayout>(layout);
            Assert.AreEqual(9, layout.CellsX);
            Assert.AreEqual(10, layout.CellsY);
        }
    }
}
