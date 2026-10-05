using System;
using System.Collections.Generic;

namespace ChessSage.Core.Util
{
    /// <summary>游戏事件日志（环形缓冲），对应原版 /api/logs?count=N 的行为。</summary>
    public sealed class GameLog
    {
        readonly List<Entry> _entries = new List<Entry>();
        readonly int _capacity;

        public struct Entry
        {
            public string Category;
            public string Message;
            public DateTime Time;
            public override string ToString() => $"[{Time:HH:mm:ss}] ({Category}) {Message}";
        }

        public GameLog(int capacity = 200)
        {
            _capacity = Math.Max(1, capacity);
        }

        public void Add(string category, string message)
        {
            _entries.Add(new Entry { Category = category, Message = message, Time = DateTime.Now });
            if (_entries.Count > _capacity)
                _entries.RemoveRange(0, _entries.Count - _capacity);
        }

        public void Info(string message) => Add("info", message);
        public void Warn(string message) => Add("warn", message);
        public void Error(string message) => Add("error", message);

        public IReadOnlyList<Entry> GetRecent(int count)
        {
            if (count <= 0 || count >= _entries.Count) return _entries.ToArray();
            return _entries.GetRange(_entries.Count - count, count);
        }

        public List<string> GetRecentText(int count)
        {
            var result = new List<string>();
            foreach (var entry in GetRecent(count)) result.Add(entry.ToString());
            return result;
        }

        public void Clear() => _entries.Clear();

        public int Count => _entries.Count;
    }
}