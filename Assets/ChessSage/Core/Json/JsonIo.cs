using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Json
{
    /// <summary>JSON 读写与深拷贝的轻量封装。</summary>
    public static class JsonIo
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
        };

        public static JToken Parse(string text) => JToken.Parse(text);

        public static JObject ParseObject(string text) => JObject.Parse(text);

        public static JToken Clone(JToken token) => token?.DeepClone();

        public static T ToObject<T>(JToken token) => token.ToObject<T>(JsonSerializer.Create(Settings));

        public static JToken FromObject(object value) => JToken.FromObject(value, JsonSerializer.Create(Settings));

        public static string ToJson(JToken token, bool indented = false)
            => token.ToString(indented ? Formatting.Indented : Formatting.None);

        /// <summary>按 "a/b/0" 形式的简单路径读取（用于配置取值）。</summary>
        public static JToken SelectPath(JToken root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var current = root;
            foreach (var segment in path.Split('/'))
            {
                if (string.IsNullOrEmpty(segment)) continue;
                current = current?[segment];
                if (current == null) return null;
            }
            return current;
        }

        /// <summary>把若干 JObject 浅合并（后者覆盖前者），返回新对象。</summary>
        public static JObject Merge(IEnumerable<JObject> layers)
        {
            var result = new JObject();
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                foreach (var prop in layer.Properties())
                    result[prop.Name] = prop.Value.DeepClone();
            }
            return result;
        }
    }
}