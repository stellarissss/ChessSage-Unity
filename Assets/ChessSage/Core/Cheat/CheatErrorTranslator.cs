using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Cheat
{
    /// <summary>
    /// 代价透明计算（UX §6 / §8）：施展前把业力代价、溢出量、识破增量与风险档位一次算清。
    ///
    /// 公式与 <c>KarmaEstimator.CalculateDetectionDelta</c> 完全一致（Δ = C_coef × O^α），
    /// 因此"预览里看到的识破风险"与"确认后真正结算的风险"同源。
    /// </summary>
    public static class CheatCostCalculator
    {
        /// <summary>溢出量达到该值即视为"危险"档（UX §1.4：O≥30）。</summary>
        public const int DangerOvershootThreshold = 30;

        public static CostPreview Compute(
            int cost, int karma, int karmaMax, JObject skillModifiers,
            double currentDetection, bool detectionLocked)
        {
            skillModifiers = skillModifiers ?? new JObject();
            var overshoot = Math.Max(0, karma + cost - karmaMax);
            var c = GetDouble(skillModifiers, "detection_coefficient", 0.1);
            var alpha = GetDouble(skillModifiers, "detection_alpha", 1.5);
            var delta = overshoot > 0 ? c * Math.Pow(overshoot, alpha) : 0.0;

            var freeCheat = GetInt(skillModifiers, "free_cheat_count", 0) > 0;
            var tier = overshoot <= 0
                ? CheatTier.Safe
                : overshoot >= DangerOvershootThreshold ? CheatTier.Danger : CheatTier.Critical;

            return new CostPreview
            {
                Cost = cost,
                Karma = karma,
                KarmaMax = karmaMax,
                Overshoot = overshoot,
                DetectionDelta = delta,
                DetectionAfter = Math.Min(100.0, currentDetection + delta),
                DetectionLocked = detectionLocked,
                Tier = tier,
                FreeCheat = freeCheat,
                SkillNote = BuildSkillNote(skillModifiers),
            };
        }

        /// <summary>技能/道加成透明标注（UX §8.3）：显式告诉玩家"省在哪"。</summary>
        public static string BuildSkillNote(JObject m)
        {
            if (m == null) return "";
            var notes = new List<string>();
            var coefficient = GetDouble(m, "detection_coefficient", 0.1);
            var alpha = GetDouble(m, "detection_alpha", 1.5);

            if (GetInt(m, "free_cheat_count", 0) > 0)
                notes.Add($"『白嫖』剩余 {GetInt(m, "free_cheat_count", 0)} 次");
            if (GetBool(m, "efficiency_fraud", false)) notes.Add("『效率欺诈』已生效（有概率减费）");
            if (GetBool(m, "hell_hungry_discount", false)) notes.Add("地狱/饿鬼道折扣 -25%");
            if (GetBool(m, "heaven_asura_discount", false)) notes.Add("天道/阿修罗道折扣 -25%");
            if (Math.Abs(coefficient - 0.1) > 1e-9) notes.Add($"『藏锋』识破系数 {coefficient:0.##}");
            if (Math.Abs(alpha - 1.5) > 1e-9) notes.Add($"识破指数 α={alpha:0.##}");
            if (GetBool(m, "first_overdraft_skip", false)) notes.Add("首次透支豁免已就绪");
            if (GetInt(m, "karma_max_bonus", 0) != 0) notes.Add($"业力安全线 +{GetInt(m, "karma_max_bonus", 0)}");
            return string.Join("；", notes);
        }

        static int GetInt(JObject o, string k, int def)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<int>();
            if (v.Type == JTokenType.Float) return (int)v.Value<double>();
            if (v.Type == JTokenType.Boolean) return v.Value<bool>() ? 1 : 0;
            return def;
        }

        static double GetDouble(JObject o, string k, double def)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Integer) return v.Value<long>();
            if (v.Type == JTokenType.Float) return v.Value<double>();
            return def;
        }

        static bool GetBool(JObject o, string k, bool def)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Boolean) return v.Value<bool>();
            return def;
        }
    }

    /// <summary>
    /// 失败反馈翻译器（UX §7）：把引擎的技术性 message / SchemaValidator 的错误串
    /// 包装成「为什么不行 + 怎么改能行」。**严禁**只回"非法指令"四个字。
    /// </summary>
    public static class CheatErrorTranslator
    {
        static readonly Regex BracketOob =
            new Regex(@"\[pieces\.(\d+)\.position\.(\d+)\]\s*(-?\d+)\s*超出棋盘范围\s*\(0-(\d+)\)", RegexOptions.Compiled);

        static readonly Regex LostPiece = new Regex("棋子丢失[:：]\\s*([^;；]+)", RegexOptions.Compiled);
        static readonly Regex TamperedAttr = new Regex("棋子属性被篡改[:：]\\s*([^;；]+)", RegexOptions.Compiled);

        /// <summary>成功类结果返回 null；否则返回结构化失败反馈（UX §7.1 映射表）。</summary>
        public static CheatFailure Present(JObject result)
        {
            if (result == null) return new CheatFailure { Title = "⚠️ 天道没有回应", Reason = "结果为空。" };

            var type = GetStr(result, "type");
            var msg = GetStr(result, "message");
            var cls = GetStr(result, "classification");
            var blocked = GetBool(result, "karma_blocked", false);

            if (type == "fun")
                return new CheatFailure { IsFun = true, Title = "天道回应", Reason = msg };

            if (type == "applied" || type == "partial" || type == "success") return null;

            if (type == "rejected")
            {
                if (blocked)
                {
                    var f = new CheatFailure
                    {
                        Title = "🚫 业障过重，天道不容",
                        Reason = string.IsNullOrEmpty(msg) ? "这一手业力过重，超出单次上限。" : msg,
                    };
                    f.Actions.Add(new CheatAction("拆成两小步", CheatActionKind.SplitSteps,
                        "拆成两小步：先把业力降到 60 以内再改"));
                    f.Actions.Add(new CheatAction("取消", CheatActionKind.None));
                    return f;
                }
                if (cls == "C+" || cls == "D" || msg.Contains("技能树") || msg.Contains("未解锁"))
                {
                    var f = new CheatFailure
                    {
                        Title = "🔒 此术尚未解锁",
                        Reason = string.IsNullOrEmpty(msg) ? "该分类需在技能树中解锁。" : msg,
                    };
                    f.Actions.Add(new CheatAction("前往技能树", CheatActionKind.GoSkillTree));
                    f.Actions.Add(new CheatAction("换个指令", CheatActionKind.ChangeWording));
                    return f;
                }
                // 意图判定不可行
                {
                    var f = new CheatFailure
                    {
                        Title = "⚠️ 这一步天道改不动",
                        Reason = string.IsNullOrEmpty(msg) ? "这一手在规则上无法实现。" : msg,
                    };
                    f.Actions.Add(new CheatAction("采用建议", CheatActionKind.ApplySuggestion, ExtractSuggestion(msg)));
                    f.Actions.Add(new CheatAction("手动修改", CheatActionKind.ManualEdit));
                    return f;
                }
            }

            if (type == "error")
            {
                if (msg.Contains("未设置API密钥") || msg.Contains("未设置") && msg.Contains("密钥"))
                {
                    var f = new CheatFailure
                    {
                        Offline = true,
                        Title = "🔌 离线模式：无法连接天道",
                        Reason = "你只能使用预置的改规。",
                    };
                    f.Actions.Add(new CheatAction("打开预置指令", CheatActionKind.OpenPresets));
                    return f;
                }
                if (msg.Contains("超时") || msg.Contains("网络") || msg.Contains("被取消") || msg.Contains("HTTP"))
                {
                    var f = new CheatFailure
                    {
                        Title = "⚠️ 天道迟迟没有回应",
                        Reason = string.IsNullOrEmpty(msg) ? "已等待超时，或网络不通。" : msg,
                    };
                    f.Actions.Add(new CheatAction("重新施展", CheatActionKind.Retry));
                    f.Actions.Add(new CheatAction("改用预置指令", CheatActionKind.UsePreset));
                    return f;
                }
                if (msg.Contains("意图解析失败") || msg.Contains("无法解析"))
                {
                    var f = new CheatFailure
                    {
                        Title = "🤔 天道没听懂这句话",
                        Reason = "无法解析这句话的意图。",
                    };
                    f.Actions.Add(new CheatAction("换个说法", CheatActionKind.ChangeWording));
                    f.Actions.Add(new CheatAction("打开预置指令", CheatActionKind.OpenPresets));
                    return f;
                }
                // Schema 校验拒绝
                {
                    var f = new CheatFailure
                    {
                        Title = "⚠️ 这规则自相矛盾",
                        Reason = TranslateValidationMessage(msg),
                    };
                    f.Actions.Add(new CheatAction("看看建议", CheatActionKind.ApplySuggestion, ExtractSuggestion(msg)));
                    f.Actions.Add(new CheatAction("手动修改", CheatActionKind.ManualEdit));
                    return f;
                }
            }

            var generic = new CheatFailure
            {
                Title = "⚠️ 天道没有回应",
                Reason = string.IsNullOrEmpty(msg) ? "发生了未知错误。" : TranslateValidationMessage(msg),
            };
            generic.Actions.Add(new CheatAction("重新施展", CheatActionKind.Retry));
            return generic;
        }

        /// <summary>Schema 校验错误串的人读翻译（UX §7.2）。</summary>
        public static string TranslateValidationMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "发生了未知校验错误。";

            var text = raw;
            foreach (var prefix in new[]
            {
                "Schema校验失败，重试2次后仍有错误:", "Schema校验失败:", "规则未发生实质性变化，重试2次后仍未修改",
                "重试2次后仍失败:", "AI生成失败:", "应用diff失败:",
            })
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    text = text.Substring(prefix.Length).Trim(' ', '：', ':');
            }

            var segments = text.Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            var outLines = new List<string>();
            foreach (var segRaw in segments)
            {
                var seg = segRaw.Trim();
                if (seg.Length == 0) continue;
                outLines.Add(TranslateSegment(seg));
                if (outLines.Count >= 3) break;
            }

            if (outLines.Count == 0) outLines.Add("发生了未知校验错误。");
            var tail = segments.Length > 3 ? $"（还有 {segments.Length - 3} 条…）" : "";
            return string.Join("；", outLines) + tail;
        }

        static string TranslateSegment(string seg)
        {
            var m = BracketOob.Match(seg);
            if (m.Success)
            {
                var axis = m.Groups[2].Value;
                var range = m.Groups[4].Value;
                return axis == "0"
                    ? $"有棋子的横坐标超出了棋盘（范围 0–{range}）"
                    : $"有棋子的纵坐标超出了棋盘（范围 0–{range}）";
            }

            var lost = LostPiece.Match(seg);
            if (lost.Success)
                return $"改动会让「{lost.Groups[1].Value.Trim()}」意外消失，这不合理";

            var tamper = TamperedAttr.Match(seg);
            if (tamper.Success)
            {
                var detail = tamper.Groups[1].Value.Trim();
                return detail.Contains("side")
                    ? "改动会把棋子变成敌方阵营，天道不允许颠倒阵营"
                    : $"改动会篡改棋子的核心属性（{detail}），天道不允许";
            }

            if (seg.Contains("side_overrides"))
                return "规则改动缺少必要结构（side_overrides），无法生效";

            if (seg.Contains("超出棋盘范围"))
                return "有棋子被放到了棋盘之外";

            return seg;
        }

        static string ExtractSuggestion(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return null;
            var l = msg.IndexOf('「');
            var r = msg.IndexOf('」');
            if (l >= 0 && r > l) return msg.Substring(l + 1, r - l - 1);
            return null;
        }

        static string GetStr(JObject o, string k)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return "";
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }

        static bool GetBool(JObject o, string k, bool def)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return def;
            return v.Type == JTokenType.Boolean && v.Value<bool>();
        }
    }
}
