using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>
    /// 探针环境：定位仓库根目录、读取 StreamingAssets 配置与 Fixtures。
    /// 与 Unity 的 TestEnv 语义一致，但无需 UnityEngine，可在纯 Mono 下运行。
    /// </summary>
    public static class ProbeEnv
    {
        public static readonly string RepoRoot = FindRepoRoot();
        public static string ConfigsRoot => Path.Combine(RepoRoot, "Assets", "StreamingAssets", "Configs");
        public static string FixturesDir => Path.Combine(RepoRoot, "Assets", "ChessSage", "Tests", "Fixtures");

        static string FindRepoRoot()
        {
            // 依次从可执行文件目录、当前工作目录向上查找，兼容 OUT 位于仓库外（如 /tmp）的用法。
            var starts = new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory };
            foreach (var start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "Assets", "ChessSage", "Tests", "Fixtures")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            throw new InvalidOperationException("找不到仓库根目录（缺少 Assets/ChessSage/Tests/Fixtures）");
        }

        public static JObject LoadJson(string path) => JObject.Parse(File.ReadAllText(path));
        public static JObject LoadConfig(string variant, string name)
            => LoadJson(Path.Combine(ConfigsRoot, variant, name + ".json"));
        public static JObject LoadFixture(string name)
            => LoadJson(Path.Combine(FixturesDir, name + ".json"));

        public static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal(object expected, object actual, string label)
        {
            bool ok = expected == null ? actual == null : expected.Equals(actual);
            if (!ok) throw new Exception($"{label}: 期望 {expected ?? "<null>"}，实际 {actual ?? "<null>"}");
        }

        public static void EqualList(List<string> expected, List<string> actual, string label)
        {
            if (expected.Count != actual.Count)
                throw new Exception($"{label}: 数量不符 期望 {expected.Count} 实际 {actual.Count} {Join(expected)} vs {Join(actual)}");
            for (int i = 0; i < expected.Count; i++)
                if (expected[i] != actual[i])
                    throw new Exception($"{label}: 第 {i} 项不符 期望 {expected[i]} 实际 {actual[i]}");
        }

        static string Join(List<string> list) => "[" + string.Join(",", list) + "]";

        public static List<string> Normalize(IEnumerable<int[]> moves)
        {
            var list = new List<string>();
            foreach (var m in moves) list.Add(m[0] + "," + m[1]);
            list.Sort();
            return list;
        }

        public static List<string> Normalize(JArray moves)
        {
            var list = new List<string>();
            if (moves != null)
                foreach (var m in moves)
                    if (m is JArray a) list.Add(a[0].Value<int>() + "," + a[1].Value<int>());
            list.Sort();
            return list;
        }
    }
}