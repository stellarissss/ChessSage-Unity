using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Cheat
{
    /// <summary>
    /// 指令历史（UX §3.4）：本地环形缓存，保留最近 N 条，点击回填输入框（不自动提交）。
    /// </summary>
    public sealed class CheatHistory
    {
        readonly List<string> _items = new List<string>();
        public int Capacity = 5;

        /// <summary>最近在前。</summary>
        public IReadOnlyList<string> Items => _items;

        public void Add(string command)
        {
            var text = (command ?? "").Trim();
            if (text.Length == 0) return;

            _items.Remove(text);           // 去重：重复施展只保留最新一次
            _items.Insert(0, text);
            while (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
        }

        public void Clear() => _items.Clear();
    }

    /// <summary>
    /// 改规撤销栈（UX §6.2）——**独立于棋局悔棋栈**。
    ///
    /// 存的是"改规配置切片"（pieces_* / rules / ui_config / board），**不含 board_state**：
    /// 撤回改规只回退规则，**绝不回退棋谱**（这正是规格反复强调的两栈分离）。
    /// 同时记录入栈时的棋谱步数，用于"改规后已走子"的一致性提示（UX §6.2.3）。
    /// </summary>
    public sealed class CheatRuleStack
    {
        public sealed class Entry
        {
            /// <summary>改规配置切片（不含 board_state）。</summary>
            public JObject Slice;
            /// <summary>入栈时棋谱步数，用于判断"改规后是否已走子"。</summary>
            public int MoveCount;
        }

        readonly List<Entry> _stack = new List<Entry>();
        /// <summary>建议上限 10 层（UX §6.2.2），超出丢最旧。</summary>
        public int MaxDepth = 10;

        public int Count => _stack.Count;
        public bool CanUndo => _stack.Count > 0;

        public void Push(JObject slice, int moveCount)
        {
            if (slice == null) return;
            _stack.Add(new Entry { Slice = (JObject)slice.DeepClone(), MoveCount = moveCount });
            while (_stack.Count > MaxDepth) _stack.RemoveAt(0);
        }

        public Entry Peek() => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        public Entry Pop()
        {
            if (_stack.Count == 0) return null;
            var e = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            return e;
        }

        public void Clear() => _stack.Clear();
    }
}
