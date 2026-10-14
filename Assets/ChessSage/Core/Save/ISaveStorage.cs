using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ChessSage.Core.Save
{
    /// <summary>
    /// 存档读写抽象（依赖倒置）。
    ///
    /// 真实发布走 <see cref="FileSaveStorage"/>（落盘），测试走 <see cref="InMemorySaveStorage"/>
    /// （纯内存、零副作用、可重复跑）。这延续了本工程既有约定：
    /// 测试绝不写真实配置/存档目录（参见 tools/verify/gen-samsara-fixtures.py 的做法），
    /// 从而让 ChessSage.Core 的存档逻辑能被 L1（dotnet）无副作用地覆盖。
    /// </summary>
    public interface ISaveStorage
    {
        /// <summary>键是否已存在。</summary>
        bool Exists(string key);

        /// <summary>读取键对应文本；键不存在时抛 <see cref="SaveNotFoundException"/>。</summary>
        string Read(string key);

        /// <summary>写入（覆盖）键对应文本。</summary>
        void Write(string key, string contents);

        /// <summary>删除键（不存在则忽略）。</summary>
        void Delete(string key);

        /// <summary>列出全部已存在的键。</summary>
        IReadOnlyList<string> ListKeys();
    }

    /// <summary>
    /// 内存实现：测试专用，绝不触碰真实磁盘。线程安全（内部加锁）。
    /// </summary>
    public sealed class InMemorySaveStorage : ISaveStorage
    {
        readonly Dictionary<string, string> _entries = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool Exists(string key)
        {
            lock (_entries) return _entries.ContainsKey(key);
        }

        public string Read(string key)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(key, out var value)) return value;
            }
            throw new SaveNotFoundException($"存档键不存在: {key}");
        }

        public void Write(string key, string contents)
        {
            lock (_entries) _entries[key] = contents;
        }

        public void Delete(string key)
        {
            lock (_entries) _entries.Remove(key);
        }

        public IReadOnlyList<string> ListKeys()
        {
            lock (_entries) return _entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// 真实文件系统实现：每个键对应 root 目录下一个 <c>{key}.json</c> 文件。
    /// 键中的路径分隔符会被替换，避免越出 root（防目录穿越）。
    /// </summary>
    public sealed class FileSaveStorage : ISaveStorage
    {
        readonly string _root;

        public FileSaveStorage(string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir)) throw new ArgumentException("rootDir 不能为空", nameof(rootDir));
            _root = rootDir;
        }

        public string RootDir => _root;

        string PathFor(string key)
        {
            var safe = Sanitize(key);
            return Path.Combine(_root, safe + ".json");
        }

        static string Sanitize(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("存档键不能为空", nameof(key));
            var invalid = Path.GetInvalidFileNameChars();
            var chars = key.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
            return new string(chars);
        }

        public bool Exists(string key) => File.Exists(PathFor(key));

        public string Read(string key)
        {
            var path = PathFor(key);
            if (!File.Exists(path)) throw new SaveNotFoundException($"存档文件不存在: {path}");
            return File.ReadAllText(path);
        }

        public void Write(string key, string contents)
        {
            if (!Directory.Exists(_root)) Directory.CreateDirectory(_root);
            File.WriteAllText(PathFor(key), contents ?? string.Empty);
        }

        public void Delete(string key)
        {
            var path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
        }

        public IReadOnlyList<string> ListKeys()
        {
            if (!Directory.Exists(_root)) return new List<string>();
            return Directory.GetFiles(_root, "*.json")
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
        }
    }
}
