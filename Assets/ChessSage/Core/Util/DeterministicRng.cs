using System;
using System.Collections.Generic;

namespace ChessSage.Core.Util
{
    /// <summary>可复现随机源（种子可控，便于测试与回放）。</summary>
    public sealed class DeterministicRng
    {
        readonly Random _random;

        public DeterministicRng() : this(Environment.TickCount) { }

        public DeterministicRng(int seed)
        {
            _random = new Random(seed);
        }

        public double NextDouble() => _random.NextDouble();

        public int Next(int maxExclusive) => _random.Next(maxExclusive);

        public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

        public T Choice<T>(IList<T> items)
        {
            if (items == null || items.Count == 0) throw new InvalidOperationException("Choice called on empty collection");
            return items[_random.Next(items.Count)];
        }

        public void Shuffle<T>(IList<T> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }
    }
}