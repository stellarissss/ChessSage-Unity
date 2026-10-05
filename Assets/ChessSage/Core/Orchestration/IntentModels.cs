using System;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>分类规范与业力状态等编排层的轻量模型。</summary>
    public static class Classification
    {
        public const string A = "A";
        public const string B = "B";
        public const string C = "C";
        public const string CPlus = "C+";
        public const string D = "D";
        public const string E = "E";
        public const string F = "F";

        /// <summary>
        /// 将 LLM 输出的分类规整到合法集合；无法识别的输入一律归为 E(闲聊)。
        /// 对齐 ai_orchestrator_base.py 的 _normalize_classification。
        /// </summary>
        public static string Normalize(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return E;
            var c = cls.Trim().ToUpperInvariant();
            if (c == "A" || c == "B" || c == "C" || c == "C+" || c == "D" || c == "E" || c == "F")
                return c;
            if (c.StartsWith("C+", StringComparison.Ordinal)) return CPlus;
            if (c.StartsWith("D", StringComparison.Ordinal)) return D;
            return E;
        }
    }

    /// <summary>业力评估阶段产物：估算成本 + 局势摘要 + 技能修饰器（对齐 RPG 的 karma_state）。</summary>
    public sealed class KarmaState
    {
        public int Cost;
        public string BoardSummary;
        public JObject SkillModifiers;

        public KarmaState() { }

        public KarmaState(int cost, string boardSummary, JObject skillModifiers)
        {
            Cost = cost;
            BoardSummary = boardSummary;
            SkillModifiers = skillModifiers ?? new JObject();
        }
    }

    /// <summary>JSON 取值助手，统一处理缺省值与类型不匹配。</summary>
    public static class JsonHelpers
    {
        public static JObject AsObject(JToken token) => token as JObject;

        public static JArray AsArray(JToken token) => token as JArray;

        public static JObject GetObject(JObject obj, string key)
            => obj != null ? obj[key] as JObject : null;

        public static JArray GetArray(JObject obj, string key)
            => obj != null ? obj[key] as JArray : null;

        public static string GetString(JObject obj, string key, string def = "")
        {
            var v = obj?[key];
            if (v == null || v.Type == JTokenType.Null) return def;
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }

        public static int GetInt(JObject obj, string key, int def = 0)
        {
            var v = obj?[key];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<int>();
            if (v.Type == JTokenType.Float) return (int)v.Value<double>();
            if (v.Type == JTokenType.String && int.TryParse(v.Value<string>(), out var parsed)) return parsed;
            return def;
        }

        public static double GetDouble(JObject obj, string key, double def = 0.0)
        {
            var v = obj?[key];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<long>();
            if (v.Type == JTokenType.Float) return v.Value<double>();
            if (v.Type == JTokenType.String && double.TryParse(v.Value<string>(), out var parsed)) return parsed;
            return def;
        }

        public static bool GetBool(JObject obj, string key, bool def = false)
        {
            var v = obj?[key];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Boolean) return v.Value<bool>();
            if (v.Type == JTokenType.Integer) return v.Value<int>() != 0;
            if (v.Type == JTokenType.String && bool.TryParse(v.Value<string>(), out var parsed)) return parsed;
            return def;
        }
    }
}