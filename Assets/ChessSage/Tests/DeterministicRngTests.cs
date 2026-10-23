using System;
using System.Collections.Generic;
using ChessSage.Core.Agent;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E0-S4：确定性随机源（波动 σ / 激增 / 折扣抽签的唯一出口）。
    /// 期望值独立推导：不调用被测实现之外的辅助逻辑，序列断言在同运行时内自比较。
    /// </summary>
    [TestFixture]
    public sealed class DeterministicRngTests
    {
        [Test]
        public void SameSeedAndRequest_ProducesIdenticalSequence()
        {
            var a = new DeterministicRng(sessionSeed: 20261023, requestSeq: 7);
            var b = new DeterministicRng(sessionSeed: 20261023, requestSeq: 7);
            for (int i = 0; i < 16; i++)
                Assert.AreEqual(a.NextDouble(), b.NextDouble(), $"第 {i} 次抽取应逐位一致");
        }

        [Test]
        public void DifferentRequestSeq_ProducesDifferentSequence()
        {
            var sa = Draw(new DeterministicRng(20261023, 1), 8);
            var sb = Draw(new DeterministicRng(20261023, 2), 8);
            CollectionAssert.AreNotEqual(sa, sb, "相邻请求序号必须派生出不同序列");
        }

        [Test]
        public void DifferentSessionSeed_ProducesDifferentSequence()
        {
            var sa = Draw(new DeterministicRng(1, 0), 8);
            var sb = Draw(new DeterministicRng(2, 0), 8);
            CollectionAssert.AreNotEqual(sa, sb, "不同会话种子必须派生不同序列");
        }

        [Test]
        public void Instances_AreIsolated_NoSharedStaticState()
        {
            var a = new DeterministicRng(1, 0);
            var b = new DeterministicRng(1, 0);
            var first = a.NextDouble();
            // b 是独立实例：a 的消耗不得影响 b；新建实例重新开始同一序列。
            Assert.AreEqual(first, b.NextDouble(), "实例应相互隔离");
            Assert.AreEqual(first, new DeterministicRng(1, 0).NextDouble(), "新实例应重放同序列");
        }

        [Test]
        public void DerivedSeeds_ArePairwiseDistinct_AcrossSessionsAndRequests()
        {
            var seen = new HashSet<int>();
            for (int session = 0; session < 8; session++)
                for (int seq = 0; seq < 256; seq++)
                    Assert.IsTrue(seen.Add(DeterministicRng.DeriveSeed(session, seq)),
                        $"种子碰撞：session={session} requestSeq={seq}");
        }

        [Test]
        public void NextDouble_StaysInUnitInterval_AndRoughlyUniform()
        {
            var rng = new DeterministicRng(42, 0);
            const int N = 20000;
            double sum = 0;
            var buckets = new int[5];
            for (int i = 0; i < N; i++)
            {
                var v = rng.NextDouble();
                Assert.GreaterOrEqual(v, 0.0, "下界 0（含）");
                Assert.Less(v, 1.0, "上界 1（不含）");
                sum += v;
                buckets[Math.Min(4, (int)(v * 5))]++;
            }
            Assert.That(sum / N, Is.InRange(0.48, 0.52), "均值应接近 0.5");
            foreach (var c in buckets)
                Assert.Greater(c, 0, "5 个直方图桶均应非空（防'只返回端点两值'的假随机）");
        }

        static double[] Draw(IRandom rng, int n)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = rng.NextDouble();
            return a;
        }
    }
}
