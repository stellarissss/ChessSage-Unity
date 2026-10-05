using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>JSON 读写、取值与占位符替换辅助（仅依赖 Newtonsoft.Json）。</summary>
    internal static class SamsaraJson
    {
        /// <summary>读取 JSON 对象文件；文件不存在或解析失败返回 null。</summary>
        public static JObject LoadObject(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return JObject.Parse(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>深拷贝一个 JObject（null 安全）。</summary>
        public static JObject Clone(JObject value)
        {
            return value == null ? null : (JObject)value.DeepClone();
        }

        /// <summary>当前时间戳字符串，格式与 Python datetime.now().isoformat() 对齐。</summary>
        public static string NowIso()
        {
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        }

        public static int GetInt(JObject obj, string key, int fallback)
        {
            var token = obj == null ? null : obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try
            {
                return token.Value<int>();
            }
            catch (Exception)
            {
                int parsed;
                return int.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
                    ? parsed : fallback;
            }
        }

        public static double GetDouble(JObject obj, string key, double fallback)
        {
            var token = obj == null ? null : obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try
            {
                return token.Value<double>();
            }
            catch (Exception)
            {
                double parsed;
                return double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
                    ? parsed : fallback;
            }
        }

        public static bool GetBool(JObject obj, string key, bool fallback)
        {
            var token = obj == null ? null : obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            bool parsed;
            return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        public static string GetString(JObject obj, string key, string fallback)
        {
            var token = obj == null ? null : obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }

        /// <summary>将文本中的 {key} 占位符替换为已知值（endings.py _substitute_placeholders）。</summary>
        public static string SubstitutePlaceholders(string text, IDictionary<string, string> placeholders)
        {
            if (text == null) return null;
            return Regex.Replace(text, @"\{(\w+)\}", delegate (Match m)
            {
                string value;
                return placeholders.TryGetValue(m.Groups[1].Value, out value) ? value : m.Value;
            });
        }
    }

    /// <summary>进程内随机数辅助（识破/迷雾/Boss 概率判定共用，线程安全）。</summary>
    internal static class SamsaraRandom
    {
        private static readonly Random Rng = new Random();
        private static readonly object Gate = new object();

        public static double NextDouble()
        {
            lock (Gate)
            {
                return Rng.NextDouble();
            }
        }

        /// <summary>以给定概率返回 true（等价于 random.random() &lt; probability）。</summary>
        public static bool Chance(double probability)
        {
            return NextDouble() < probability;
        }
    }
}