using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ChessSage.Tests
{
    /// <summary>
    /// 测试环境路径与 JSON 读取辅助。
    ///
    /// 【设计要点】本文件刻意不依赖 UnityEngine，使同一套测试既能在 Unity Test Runner 内运行，
    /// 也能在纯 .NET（dotnet test）下运行 —— 从而在不具备 Unity 编辑器的环境中也能对
    /// ChessSage.Core 做真实的编译与回归验证（见 tools/verify/dotnet/）。
    /// </summary>
    public static class TestEnv
    {
        /// <summary>
        /// Assets 根目录。解析优先级：
        ///   1. 环境变量 CHESSSAGE_ASSETS_ROOT（显式指定，供 CI 使用）；
        ///   2. Unity 运行时为 Application.dataPath；
        ///   3. 独立运行时从程序集所在目录向上探测含 Assets/ChessSage/Tests/Fixtures 的工程根。
        /// </summary>
        public static string AssetsRoot { get; } = ResolveAssetsRoot();

        public static string StreamingRoot => Path.Combine(AssetsRoot, "StreamingAssets");
        public static string ConfigsRoot => Path.Combine(StreamingRoot, "Configs");
        public static string SchemasRoot => Path.Combine(StreamingRoot, "Schemas");
        public static string FixturesDir => Path.Combine(AssetsRoot, "ChessSage", "Tests", "Fixtures");

        static string ResolveAssetsRoot()
        {
            var fromEnv = Environment.GetEnvironmentVariable("CHESSSAGE_ASSETS_ROOT");
            if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
                return Path.GetFullPath(fromEnv);

#if UNITY_2018_1_OR_NEWER
            return UnityEngine.Application.dataPath;
#else
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Assets");
                if (Directory.Exists(Path.Combine(candidate, "ChessSage", "Tests", "Fixtures")))
                    return candidate;
            }
            throw new DirectoryNotFoundException(
                "无法定位 Assets 目录。请通过环境变量 CHESSSAGE_ASSETS_ROOT 指定工程 Assets 目录。");
#endif
        }

        /// <summary>
        /// 同步等待异步测试体。Unity 2022 内置 NUnit 3.5 的 EditMode 运行器不支持 async Task 测试
        /// （报 "Method has non-void return value"），而 dotnet test（NUnit 3.14）支持。
        /// 统一改写为「同步方法 + 在此处等待」——两边跑同一份断言，行为等价。
        /// </summary>
        public static void RunSync(System.Func<System.Threading.Tasks.Task> body)
            => body().GetAwaiter().GetResult();

        public static JObject LoadJson(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到 JSON 文件", path);
            return JObject.Parse(File.ReadAllText(path));
        }

        public static JObject LoadConfig(string variant, string name)
            => LoadJson(Path.Combine(ConfigsRoot, variant, name + ".json"));

        public static JObject LoadGlobalConfig(string name)
            => LoadJson(Path.Combine(ConfigsRoot, "global", name + ".json"));

        public static JObject LoadSchema(string name)
            => LoadJson(Path.Combine(SchemasRoot, name + ".schema.json"));

        public static JObject LoadFixture(string name)
            => LoadJson(Path.Combine(FixturesDir, name + ".json"));
    }
}