using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// REG-ENG-C：为目标系统新增的 <c>most_discs</c>（黑白棋·终局子多者胜）补回归测试。
    ///
    /// 用 Roslyn 可编译的纯逻辑方式（SamsaraFixture 隔离引擎 + 手搓最小 heibaiqi
    /// 风格棋盘状态）构造用例，断言玩家侧棋子数**严格大于**对方时目标达成，否则不达成。
    /// 不改动任何既有测试文件，亦不依赖 UnityEngine 上下文（fixture 内部用临时状态
    /// 文件加载引擎，无需 MonoBehaviour）。
    /// </summary>
    public sealed class ObjectivesTests
    {
        private static JObject MostDiscsObjective() => JObject.Parse("{\"type\":\"most_discs\"}");

        /// <summary>最小 heibaiqi 风格棋盘：pieces 仅关心 side（忽略 is_alive/position）。</summary>
        private static JObject Board(int white, int black)
        {
            var pieces = new JArray();
            for (var i = 0; i < white; i++) pieces.Add(Disc("white", i));
            for (var i = 0; i < black; i++) pieces.Add(Disc("black", white + i));
            return new JObject { ["pieces"] = pieces };
        }

        private static JObject Disc(string side, int id)
        {
            return new JObject
            {
                ["id"] = "disc_" + id,
                ["type"] = "disc",
                ["side"] = side,
                ["is_alive"] = true,
                ["position"] = new JArray { 0, 0 },
            };
        }

        [Test]
        public void MostDiscs_PlayerHasMoreDiscs_CompletedTrue()
        {
            using var f = new SamsaraFixture();
            // 玩家(white)=3 > 对方(black)=1 → 达成。
            var res = f.Engine.Objectives.Check(MostDiscsObjective(), Board(3, 1));
            Assert.IsTrue(res["completed"].Value<bool>(), "玩家子多应判定达成");
        }

        [Test]
        public void MostDiscs_PlayerHasFewerDiscs_CompletedFalse()
        {
            using var f = new SamsaraFixture();
            // 玩家(white)=1 < 对方(black)=3 → 不达成。
            var res = f.Engine.Objectives.Check(MostDiscsObjective(), Board(1, 3));
            Assert.IsFalse(res["completed"].Value<bool>(), "玩家子少应判定未达成");
        }

        [Test]
        public void MostDiscs_EqualDiscs_CompletedFalse()
        {
            using var f = new SamsaraFixture();
            // 玩家(white)=2 == 对方(black)=2 → 严格大于才达成，平子判 false。
            var res = f.Engine.Objectives.Check(MostDiscsObjective(), Board(2, 2));
            Assert.IsFalse(res["completed"].Value<bool>(), "平子应判定未达成（严格大于）");
        }

        [Test]
        public void MostDiscs_EmptyBoard_CompletedFalse()
        {
            using var f = new SamsaraFixture();
            // 0 vs 0 → 玩家不严格大于对方，判 false（不抛异常、不误判达成）。
            var res = f.Engine.Objectives.Check(MostDiscsObjective(), Board(0, 0));
            Assert.IsFalse(res["completed"].Value<bool>(), "空盘应判定未达成");
        }
    }
}
