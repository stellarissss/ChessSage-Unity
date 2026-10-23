using System;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    // 【E2-S7 下线记录】本文件原有「作弊分类规范」段（A/B/C/C+/D/E/F taxonomy），随旧双层级编排器
    // 一并删除：token→业力计价（KarmaFormula/TokenLedger）与工具权限门控（GetAllowedTools）已完全
    // 取代「LLM 分类 + 价目表」轴。保留的两段是仍被 RPG 层与工具层消费的通用设施：
    //   · `KarmaState`：Samsara 的 KarmaAssessor 快照（M5-B 遗留契约，RPG 层在用）；
    //   · `JsonHelpers`：全 Core 的 JSON 取值助手（缺省值/类型不匹配的统一处理）。
    // 旧符号守卫见 QA §4.4.1-P4（LegacyFreeProbe，E3-S4 落地；本期以 grep + 反射断言先行把关）。

    /// <summary>业力评估产物：估算成本 + 局势摘要 + 技能修饰器（对齐 RPG 的 karma_state；Samsara 侧在用）。</summary>
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