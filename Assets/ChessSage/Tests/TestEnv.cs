using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ChessSage.Tests
{
    /// <summary>测试环境路径与 JSON 读取辅助。</summary>
    public static class TestEnv
    {
        public static string StreamingRoot => Path.Combine(Application.dataPath, "StreamingAssets");
        public static string ConfigsRoot => Path.Combine(StreamingRoot, "Configs");
        public static string SchemasRoot => Path.Combine(StreamingRoot, "Schemas");
        public static string FixturesDir => Path.Combine(Application.dataPath, "ChessSage", "Tests", "Fixtures");

        public static JObject LoadJson(string path)
            => JObject.Parse(File.ReadAllText(path));

        public static JObject LoadConfig(string variant, string name)
            => LoadJson(Path.Combine(ConfigsRoot, variant, name + ".json"));

        public static JObject LoadFixture(string name)
            => LoadJson(Path.Combine(FixturesDir, name + ".json"));
    }
}