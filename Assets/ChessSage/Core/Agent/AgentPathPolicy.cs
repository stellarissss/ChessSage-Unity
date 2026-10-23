using System;
using System.Collections.Generic;

namespace ChessSage.Core.Agent
{
    /// <summary>路径白名单拒绝结果（§4.2 输出样例：error 与 hint 是两个独立字段）。</summary>
    public sealed class PathRejection
    {
        public string Error;
        public string Hint;
    }

    /// <summary>
    /// apply_config_patch 路径白名单（工程侧 §4.2 工具 5，纯函数、表驱动）。
    ///
    /// 【前缀语法】JSON Pointer 前缀匹配：<c>*</c> = 恰好一段（数组索引占位）；
    /// <c>**</c> = 零或多段；<c>{a,b,c}</c> = 段内枚举（恰好一段）。
    /// 整模式须全长匹配（非真前缀），<c>/pieces/*/position</c> 不匹配 <c>/pieces/0/position/x</c>。
    ///
    /// 【全局拒绝】（无论 target）：
    ///   1. 根级操作（path 为 "" 或 "/"）——禁止整体替换切片；
    ///   2. board_state 的 /move_history、/board、/current_turn——棋谱完整性与回合切换
    ///      只走 mechanisms 或工具 4（move_piece）；
    ///   3. 不匹配任何白名单前缀——拒绝信息携带该 target 的合法前缀列表；
    ///   4. 限额（op 数 / 写调用数）见 <see cref="AgentLimits"/>。
    /// </summary>
    public static class AgentPathPolicy
    {
        static readonly Dictionary<string, string[]> Patterns = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["board_state"] = new[]
            {
                "/pieces/*/position",
                "/pieces/*/is_alive",
                "/pieces/*/type",
                "/pieces/*/name",
                "/mechanisms/**",
                "/game_status/{state,winner,win_condition,custom_rules_active}",
            },
            ["board"] = new[]
            {
                "/width", "/height",
                // 真实 StreamingAssets/board.json 的尺寸在 geometry 下（E3-S2 兼容；只放行尺寸，
                // palace/regions 仍拒绝——改尺寸不应顺带改宫殿/区域布局）。
                "/geometry/width", "/geometry/height",
            },
            ["rules"] = new[] { "/ai_difficulty/**", "/side_overrides/**" },
            ["ui_config"] = new[] { "/**" },
            ["pieces_red"] = new[] { "/pieces/**" },
            ["pieces_black"] = new[] { "/pieces/**" },
            ["pieces_white"] = new[] { "/pieces/**" },
        };

        // hint 展示文本（board_state 用 §4.2 输出样例的压缩式）。
        static readonly Dictionary<string, string> Display = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["board_state"] = "/pieces/*/{position,is_alive,type,name}, /mechanisms/**, /game_status/{state,winner,win_condition,custom_rules_active}",
            ["board"] = "/width, /height, /geometry/width, /geometry/height",
            ["rules"] = "/ai_difficulty/**, /side_overrides/**",
            ["ui_config"] = "/**",
            ["pieces_red"] = "/pieces/**",
            ["pieces_black"] = "/pieces/**",
            ["pieces_white"] = "/pieces/**",
        };

        /// <summary>
        /// 合法 target 集合（对齐 §4.2 apply_config_patch 的 enum 序：
        /// board/rules/ui_config/pieces_red/pieces_black/pieces_white/board_state）。
        /// 显式数组——不依赖 Dictionary 枚举序（字节级稳定要求）。
        /// </summary>
        static readonly string[] TargetOrder =
        {
            "board", "rules", "ui_config", "pieces_red", "pieces_black", "pieces_white", "board_state",
        };

        public static IReadOnlyList<string> KnownTargets => TargetOrder;

        public static bool IsKnownTarget(string target)
            => target != null && Patterns.ContainsKey(target);

        /// <summary>该 target 的合法前缀展示文本（未知 target 返回 null）。</summary>
        public static string AllowedPrefixesText(string target)
            => target != null && Display.TryGetValue(target, out var d) ? d : null;

        public static bool IsPathAllowed(string target, string pointer)
            => Reject(target, pointer) == null;

        /// <summary>拒绝时返回 {Error, Hint}（形状对齐 §4.2 越权输出样例）；放行返回 null。</summary>
        public static PathRejection Reject(string target, string pointer)
        {
            if (!IsKnownTarget(target))
            {
                return new PathRejection
                {
                    Error = $"未知配置切片: {target ?? "(null)"}",
                    Hint = "合法 target: " + string.Join("/", KnownTargets),
                };
            }

            var hint = $"{target} 允许: {Display[target]}";

            // 全局规则 1：根级操作。
            if (string.IsNullOrEmpty(pointer) || pointer == "/")
                return new PathRejection { Error = "禁止根级 replace/remove/add（path 为 \"\" 或 \"/\"）", Hint = hint };

            // 全局规则 2：board_state 的棋谱/棋盘快照/回合字段（只走 mechanisms 或工具 4）。
            if (target == "board_state")
            {
                var first = FirstSegment(pointer);
                if (first == "move_history" || first == "board" || first == "current_turn")
                    return new PathRejection { Error = $"路径 /{target}{pointer} 不在白名单", Hint = hint };
            }

            // 规则 3：白名单前缀匹配。
            var tokens = SplitPointer(pointer);
            foreach (var pattern in Patterns[target])
                if (Match(SplitPointer(pattern), tokens))
                    return null;

            return new PathRejection { Error = $"路径 /{target}{pointer} 不在白名单", Hint = hint };
        }

        static string FirstSegment(string pointer)
        {
            var s = pointer.Length > 1 ? pointer.Substring(1) : string.Empty;
            var slash = s.IndexOf('/');
            return slash >= 0 ? s.Substring(0, slash) : s;
        }

        /// <summary>RFC 6902 分段（"" → 空数组；"/" → 空数组；"/a/b" → ["a","b"]）。</summary>
        static string[] SplitPointer(string pointer)
        {
            if (string.IsNullOrEmpty(pointer)) return new string[0];
            var body = pointer[0] == '/' ? pointer.Substring(1) : pointer;
            if (body.Length == 0) return new string[0];
            return body.Split('/');
        }

        static bool Match(string[] pattern, string[] path) => MatchFrom(pattern, 0, path, 0);

        static bool MatchFrom(string[] pattern, int pi, string[] path, int ti)
        {
            while (pi < pattern.Length)
            {
                var p = pattern[pi];
                if (p == "**")
                {
                    // 零或多段：枚举一切可能的分割点。
                    for (int skip = ti; skip <= path.Length; skip++)
                        if (MatchFrom(pattern, pi + 1, path, skip)) return true;
                    return false;
                }
                if (ti >= path.Length) return false;
                if (p == "*") { pi++; ti++; continue; }
                if (p.Length >= 2 && p[0] == '{' && p[p.Length - 1] == '}')
                {
                    var options = p.Substring(1, p.Length - 2).Split(',');
                    var hit = false;
                    foreach (var o in options)
                        if (string.Equals(o, path[ti], StringComparison.Ordinal)) { hit = true; break; }
                    if (!hit) return false;
                    pi++; ti++; continue;
                }
                if (!string.Equals(p, path[ti], StringComparison.Ordinal)) return false;
                pi++; ti++;
            }
            return ti == path.Length;
        }
    }

    /// <summary>
    /// 配置作用域分类（E3-C 校准补丁）：把一次**成功写入**映射到
    /// ui_config / board_state / pieces / rules / objectives 五个作用域之一。
    ///
    /// 这不是「意图分类」，是补丁的机械属性：只看 target 与 JSON Pointer，不看自然语言指令。
    /// 分类规则复用 <see cref="AgentPathPolicy"/> 的既有切片/前缀划分：
    ///
    ///   target            op/path                       作用域        语义
    ///   ───────────────────────────────────────────────────────────────────────────
    ///   ui_config         任意                          ui_config     纯展示（主题/配色）
    ///   board             /width /height /geometry/*   ui_config     尺寸/几何（D 档同价带）
    ///   rules             /ai_difficulty/** 等         rules         难度/阵营覆写
    ///   pieces_*          add 到 /pieces/&lt;新棋子键&gt;    pieces        铸子（新增棋子类型）
    ///   pieces_*          其余（改既有棋子 moves 等）  rules         改规（修改既有棋子走法）
    ///   board_state       /mechanisms/**               rules         机制（skip_turns/ai_control…）
    ///   board_state       其余（/pieces/*/…、game_status） board_state 落子/生死/胜负
    ///   objective         —                            objectives    关卡目标覆写
    ///
    /// 未识别（null）= 中性权重 1.0 —— 新 target 上线不会静默涨价。
    /// </summary>
    public static class AgentScope
    {
        public const string UiConfig = ScopeWeightsConfig.UiConfig;
        public const string BoardState = ScopeWeightsConfig.BoardState;
        public const string Pieces = ScopeWeightsConfig.Pieces;
        public const string Rules = ScopeWeightsConfig.Rules;
        public const string Objectives = ScopeWeightsConfig.Objectives;

        /// <summary>
        /// apply_config_patch 的 (target, op, path) → 作用域。
        /// 「铸子 vs 改规」的判据是补丁形状：add 到 /pieces/&lt;key&gt;（恰好两段）= 铸子；
        /// 其余对既有棋子的改动（moves/label/…）= 改规。
        /// </summary>
        public static string ForPatch(string target, string op, string pointer)
        {
            switch (target)
            {
                case "ui_config":
                    return UiConfig;
                case "board":
                    // 棋盘尺寸/几何：D 档「改尺寸」与改配色同价（旧价目表同为微调档）。
                    return UiConfig;
                case "rules":
                    return Rules;
                case "objective":
                    return Objectives;
                case "pieces_red":
                case "pieces_black":
                case "pieces_white":
                    return IsNewPieceType(op, pointer) ? Pieces : Rules;
                case "board_state":
                    return string.Equals(FirstSegment(pointer), "mechanisms", StringComparison.Ordinal)
                        ? Rules
                        : BoardState;
                default:
                    return null;
            }
        }

        /// <summary>沙箱切片键 → 作用域（不能从补丁形状判断时的保守口径）。</summary>
        public static string ForSlice(string slice)
        {
            switch (slice)
            {
                case "ui_config": return UiConfig;
                case "board": return UiConfig;
                case "rules": return Rules;
                case "board_state": return BoardState;
                case "pieces_red":
                case "pieces_black":
                case "pieces_white":
                    return Pieces;
                case "objective": return Objectives;
                default: return null;
            }
        }

        /// <summary>add 到 /pieces/&lt;新键&gt;（恰好两段，且不是数组追加符 "-"）。</summary>
        static bool IsNewPieceType(string op, string pointer)
        {
            if (!string.Equals(op, "add", StringComparison.Ordinal) || string.IsNullOrEmpty(pointer))
                return false;
            if (pointer[0] != '/') return false;
            var segments = pointer.Substring(1).Split('/');
            return segments.Length == 2
                   && string.Equals(segments[0], "pieces", StringComparison.Ordinal)
                   && segments[1].Length > 0
                   && segments[1] != "-";
        }

        static string FirstSegment(string pointer)
        {
            if (string.IsNullOrEmpty(pointer)) return string.Empty;
            var body = pointer[0] == '/' ? pointer.Substring(1) : pointer;
            var slash = body.IndexOf('/');
            return slash >= 0 ? body.Substring(0, slash) : body;
        }
    }
}
