using System;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 确定性随机源（E0-S4）：种子 = 会话种子 + 请求序号（设计侧 §6.2.4）。
    ///
    /// 【为什么不用 System.Random】
    /// .NET Framework(Mono) 与 .NET 6 的 <see cref="Random"/> 算法不同、同种子不同序列；
    /// 黄金轨迹回放要求 L1(net48 harness) 与 L1(dotnet net6.0) 两个口径字节级一致，
    /// 故在此实现自包含的 SplitMix64——整数算术在一切运行时上逐位确定。
    ///
    /// 【统计质量】SplitMix64 为公认强生成器（通过 PractRand）；本类只提供
    /// NextDouble（高 53 位映射到 [0,1)），不提供额外入口。
    /// </summary>
    public sealed class DeterministicRng : IRandom
    {
        ulong _state;

        /// <summary>按「会话种子 + 请求序号」构造。</summary>
        public DeterministicRng(int sessionSeed, int requestSeq = 0)
            : this(MixSeed(sessionSeed, requestSeq))
        {
        }

        DeterministicRng(ulong state)
        {
            _state = state;
        }

        /// <summary>
        /// 派生种子（SplitMix64 常量线性混合 + finalizer 雪崩）。
        /// 相邻序号/相邻会话种子的输出互不相关；返回值落在 [0, int.MaxValue]。
        /// </summary>
        public static int DeriveSeed(int sessionSeed, int requestSeq)
        {
            return (int)(MixSeed(sessionSeed, requestSeq) & 0x7FFFFFFFUL);
        }

        static ulong MixSeed(int sessionSeed, int requestSeq)
        {
            unchecked
            {
                ulong x = (ulong)(uint)sessionSeed * 6364136223846793005UL
                        + (ulong)(uint)requestSeq * 1442695040888963407UL
                        + 0x9E3779B97F4A7C15UL;
                x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
                x ^= x >> 27; x *= 0x94D049BB133111EBUL;
                return x ^ (x >> 31);
            }
        }

        /// <summary>SplitMix64 单步：推进状态并取高 53 位映射到 [0,1)。</summary>
        public double NextDouble()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                ulong z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z = z ^ (z >> 31);
                return (z >> 11) * (1.0 / 9007199254740992.0); // 2^53
            }
        }
    }
}
