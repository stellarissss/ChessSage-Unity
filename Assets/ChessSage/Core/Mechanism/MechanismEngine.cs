using System;
using System.Collections.Generic;
using ChessSage.Core.Ai;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Mechanism
{
    public struct PreTurnInfo
    {
        public bool Skipped;
        public string SkipReason;
        public bool AiControlled;
        public string AiControlReason;
    }

    public struct PostMoveInfo
    {
        public bool SwitchTurn;
        public bool ConsumedRandomMove;
        public bool ExtraTurnGranted;
        public int MovesRemaining;
    }

    /// <summary>
    /// 机制引擎 —— 负责执行 5 种机制原语（skip_turns / ai_control / random_moves /
    /// extra_turns / move_limits）+ player_control，并支持 AI 性格系统。
    /// 逐语义移植自 legacy-web/shared/mechanism_engine_base.py。
    /// 阵营相关差异通过类属性参数化（象棋/动物棋 = red/black 且红方默认玩家控制）。
    /// </summary>
    public class MechanismEngine
    {
        // ── 棋类参数（象棋口径）──
        public Dictionary<string, string> SideLabels = new Dictionary<string, string>
        {
            ["red"] = "红方", ["black"] = "黑方", ["both"] = "双方",
        };
        public string DefaultPlayerSide = "red";
        public string PcReasonDefault = "玩家控制";
        public string PcIcon = "🎮";
        public bool PcShowRemaining = false;

        public JObject RulesConfig;

        public MechanismEngine(JObject rulesConfig = null)
        {
            RulesConfig = rulesConfig ?? new JObject();
        }

        public void UpdateRules(JObject rulesConfig) => RulesConfig = rulesConfig ?? new JObject();

        public string SideLabel(string side) => SideLabels.TryGetValue(side, out var v) ? v : side;
        public string PlayerControlLabel(string side) => SideLabels.TryGetValue(side, out var v) ? v : side;

        static JObject GetMechanisms(BoardState boardState)
        {
            if (boardState.Mechanisms == null) boardState.Mechanisms = new JObject();
            BoardState.EnsureMechanismKeys(boardState.Mechanisms);
            return boardState.Mechanisms;
        }

        // ══════════════════════════════════════════════════════════════
        // 回合开始前
        // ══════════════════════════════════════════════════════════════

        public PreTurnInfo ApplyPreTurnMechanisms(BoardState boardState, string side)
        {
            var mech = GetMechanisms(boardState);
            var info = new PreTurnInfo();

            int? skipIdx = FindActive(mech["skip_turns"], side);
            if (skipIdx.HasValue)
            {
                var item = (JObject)((JArray)mech["skip_turns"])[skipIdx.Value];
                info.Skipped = true;
                info.SkipReason = item["reason"]?.Value<string>() ?? "冻结效果";
                int remaining = item["remaining"]?.Value<int>() ?? 0;
                if (remaining > 0)
                {
                    item["remaining"] = remaining - 1;
                    if (item["remaining"].Value<int>() <= 0) ((JArray)mech["skip_turns"]).RemoveAt(skipIdx.Value);
                }
            }

            if (!info.Skipped)
            {
                int? aiIdx = FindActive(mech["ai_control"], side);
                if (aiIdx.HasValue)
                {
                    var item = (JObject)((JArray)mech["ai_control"])[aiIdx.Value];
                    info.AiControlled = true;
                    info.AiControlReason = item["reason"]?.Value<string>() ?? "AI接管中";
                }
            }

            int? limitIdx = FindActive(mech["move_limits"], side);
            if (limitIdx.HasValue)
            {
                var item = (JObject)((JArray)mech["move_limits"])[limitIdx.Value];
                item["remaining_moves"] = item["limit"];
            }
            return info;
        }

        // ══════════════════════════════════════════════════════════════
        // 走棋后
        // ══════════════════════════════════════════════════════════════

        public PostMoveInfo ApplyPostMoveMechanisms(BoardState boardState, string side)
        {
            var mech = GetMechanisms(boardState);
            var info = new PostMoveInfo { SwitchTurn = true };

            int? randIdx = FindActive(mech["random_moves"], side);
            if (randIdx.HasValue)
            {
                var item = (JObject)((JArray)mech["random_moves"])[randIdx.Value];
                int remaining = item["remaining"]?.Value<int>() ?? 0;
                if (remaining > 0)
                {
                    item["remaining"] = remaining - 1;
                    if (item["remaining"].Value<int>() <= 0) ((JArray)mech["random_moves"]).RemoveAt(randIdx.Value);
                }
                info.ConsumedRandomMove = true;
            }

            int? limitIdx = FindActive(mech["move_limits"], side);
            if (limitIdx.HasValue)
            {
                var item = (JObject)((JArray)mech["move_limits"])[limitIdx.Value];
                if (item["remaining_moves"] != null)
                {
                    int remaining = item["remaining_moves"].Value<int>() - 1;
                    item["remaining_moves"] = remaining;
                    info.MovesRemaining = remaining;
                    if (remaining > 0) info.SwitchTurn = false;
                    else ((JArray)mech["move_limits"]).RemoveAt(limitIdx.Value);
                }
            }

            if (info.SwitchTurn)
            {
                int? extraIdx = FindActive(mech["extra_turns"], side);
                if (extraIdx.HasValue)
                {
                    var item = (JObject)((JArray)mech["extra_turns"])[extraIdx.Value];
                    int remaining = item["remaining"]?.Value<int>() ?? 0;
                    if (remaining > 0)
                    {
                        item["remaining"] = remaining - 1;
                        if (item["remaining"].Value<int>() <= 0) ((JArray)mech["extra_turns"]).RemoveAt(extraIdx.Value);
                    }
                    info.ExtraTurnGranted = true;
                    info.SwitchTurn = false;
                }
            }

            if (info.SwitchTurn)
            {
                int? aiIdx = FindActive(mech["ai_control"], side);
                if (aiIdx.HasValue)
                {
                    var item = (JObject)((JArray)mech["ai_control"])[aiIdx.Value];
                    int remaining = item["remaining"]?.Value<int>() ?? 0;
                    if (remaining > 0)
                    {
                        item["remaining"] = remaining - 1;
                        if (item["remaining"].Value<int>() <= 0) ((JArray)mech["ai_control"]).RemoveAt(aiIdx.Value);
                    }
                }
            }
            return info;
        }

        // ══════════════════════════════════════════════════════════════
        // 状态查询
        // ══════════════════════════════════════════════════════════════

        public bool IsAiControlled(BoardState boardState, string side)
            => FindActive(GetMechanisms(boardState)["ai_control"], side).HasValue;

        public bool IsPlayerControlled(BoardState boardState, string side)
        {
            var mech = GetMechanisms(boardState);
            var pcList = mech["player_control"] as JArray;
            if (pcList == null || pcList.Count == 0) return side == DefaultPlayerSide;

            foreach (var it in pcList)
            {
                if (!(it is JObject item)) continue;
                var controlled = item["side"]?.Value<string>() ?? DefaultPlayerSide;
                if (controlled == "both") return true;
                if (controlled == side) return true;
            }
            return false;
        }

        public bool IsRandomMoveRequired(BoardState boardState, string side)
            => FindActive(GetMechanisms(boardState)["random_moves"], side).HasValue;

        public bool ShouldSkipTurn(BoardState boardState, string side)
            => FindActive(GetMechanisms(boardState)["skip_turns"], side).HasValue;

        public List<string> GetActiveMechanismsSummary(BoardState boardState)
        {
            var mech = GetMechanisms(boardState);
            var summary = new List<string>();

            foreach (var it in (JArray)mech["skip_turns"])
            {
                var item = (JObject)it;
                summary.Add($"⏸️ {SideLabel(item["side"]?.Value<string>())}{item["reason"]?.Value<string>() ?? "冻结"}（{RemainingStr(item, "回合")}）");
            }
            foreach (var it in (JArray)mech["ai_control"])
            {
                var item = (JObject)it;
                summary.Add($"🤖 {SideLabel(item["side"]?.Value<string>())}{item["reason"]?.Value<string>() ?? "AI接管"}（{RemainingStr(item, "回合")}）");
            }
            foreach (var it in (JArray)mech["player_control"])
            {
                var item = (JObject)it;
                var label = PlayerControlLabel(item["side"]?.Value<string>() ?? DefaultPlayerSide);
                var reason = item["reason"]?.Value<string>() ?? PcReasonDefault;
                summary.Add(PcShowRemaining ? $"{PcIcon} {label}{reason}（{RemainingStr(item, "回合")}）" : $"{PcIcon} {label}{reason}");
            }
            foreach (var it in (JArray)mech["random_moves"])
            {
                var item = (JObject)it;
                summary.Add($"🎲 {SideLabel(item["side"]?.Value<string>())}{item["reason"]?.Value<string>() ?? "随机走棋"}（{RemainingStr(item, "步")}）");
            }
            foreach (var it in (JArray)mech["extra_turns"])
            {
                var item = (JObject)it;
                summary.Add($"⚡ {SideLabel(item["side"]?.Value<string>())}{item["reason"]?.Value<string>() ?? "额外回合"}（{RemainingStr(item, "回合")}）");
            }
            foreach (var it in (JArray)mech["move_limits"])
            {
                var item = (JObject)it;
                summary.Add($"🚶 {SideLabel(item["side"]?.Value<string>())}每回合{item["limit"]?.Value<int>()}步");
            }
            return summary;
        }

        static string RemainingStr(JObject item, string unit)
        {
            int remaining = item["remaining"]?.Value<int>() ?? 0;
            return remaining < 0 ? "无限" : $"剩{remaining}{unit}";
        }

        // ══════════════════════════════════════════════════════════════
        // AI 性格系统
        // ══════════════════════════════════════════════════════════════

        public JObject GetPersonalityConfig()
        {
            var aiDiff = RulesConfig["ai_difficulty"] as JObject;
            var personality = aiDiff?["personality"] as JObject;
            if (personality == null || personality.Count == 0)
                personality = new JObject
                {
                    ["type"] = "normal",
                    ["aggressiveness"] = 0.5,
                    ["conservatism"] = 0.5,
                    ["randomness_override"] = JValue.CreateNull(),
                    ["depth_override"] = JValue.CreateNull(),
                    ["value_biases"] = new JObject(),
                    ["custom_prompt"] = JValue.CreateNull(),
                };
            return personality;
        }

        public void ApplyPersonalityToAi(ChessAi ai)
        {
            var personality = GetPersonalityConfig();
            var pType = personality["type"]?.Value<string>() ?? "normal";

            var presets = new Dictionary<string, (double depthMult, double randomnessMult, double agg, double cons)>
            {
                ["normal"] = (1.0, 1.0, 0.5, 0.5),
                ["aggressive"] = (1.0, 0.8, 0.9, 0.2),
                ["defensive"] = (1.2, 0.5, 0.2, 0.9),
                ["random"] = (0.5, 3.0, 0.5, 0.5),
            };
            if (!presets.TryGetValue(pType, out var preset)) preset = presets["normal"];

            double agg, cons;
            if (pType == "custom")
            {
                agg = personality["aggressiveness"]?.Value<double>() ?? 0.5;
                cons = personality["conservatism"]?.Value<double>() ?? 0.5;
            }
            else { agg = preset.agg; cons = preset.cons; }

            var depthOverride = personality["depth_override"]?.Value<int?>();
            if (depthOverride.HasValue) ai.Depth = Math.Max(1, depthOverride.Value);
            else ai.Depth = Math.Max(1, (int)(ai.Depth * preset.depthMult));

            var randomnessOverride = personality["randomness_override"]?.Value<double?>();
            if (randomnessOverride.HasValue) ai.Randomness = Clamp01(randomnessOverride.Value);
            else ai.Randomness = Clamp01(ai.Randomness * preset.randomnessMult);

            ai.PersonalityAggressiveness = agg;
            ai.PersonalityConservatism = cons;
            ai.PersonalityValueBiases = personality["value_biases"] as JObject ?? new JObject();
        }

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ══════════════════════════════════════════════════════════════
        // 辅助
        // ══════════════════════════════════════════════════════════════

        static int? FindActive(JToken listToken, string side)
        {
            if (!(listToken is JArray list)) return null;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is JObject item)) continue;
                if (item["side"]?.Value<string>() == side && (item["remaining"]?.Value<int>() ?? 0) != 0) return i;
            }
            return null;
        }

        public BoardState AddMechanism(BoardState boardState, string mechanismType, string side, int remaining, string reason = "")
        {
            var mech = GetMechanisms(boardState);
            if (!(mech[mechanismType] is JArray arr))
            {
                arr = new JArray();
                mech[mechanismType] = arr;
            }
            arr.Add(new JObject { ["side"] = side, ["remaining"] = remaining, ["reason"] = reason });
            return boardState;
        }
    }
}