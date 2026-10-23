using System;
using System.Collections.Generic;
using System.Text;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 系统提示 + 工具 schema 组装器（E1-S2；§13.2 R1-R7 前缀字节级稳定是硬约束）。
    ///
    /// 【稳定前缀构造（R1 顺序，勿调）】
    ///   1. 天道人设与职责（静态常量）
    ///   2. 工作纪律（目标 2-4 轮、先读后写、合并操作、不重复验证——20s 预算反推）
    ///   3. 边界规则（只用清单内工具 / 读零副作用 / 白名单 / 错误自愈 / 无变更不 commit）
    ///   4. 业力口径（静态：token 实耗计账、触顶不拦截、玩家可随时收手）
    ///   5. 标准流程（静态：改规则/移子/机制三条最短路径）
    ///   6. 工具名清单（canonical 注册表序；**不**内嵌 schema——schema 只经 tools 数组下发一次）
    ///
    /// 【禁令】禁时间戳、禁请求变内容、禁 HashSet 枚举序依赖进前缀（R1/R2/R5）——
    /// 任何字节变化都会使 DeepSeek 前缀缓存全废（§13.2 R5/R6）。
    /// </summary>
    public static class AgentPrompts
    {
        // ── 1. 人设（静态） ──────────────────────────────────────────

        const string Persona =
            "你是「天道」：玩家一句话，你借工具改棋局。业价按累计 token（含思考）计，想得越久越贵。";

        // ── 2. 工作纪律（静态；20s 延迟预算反推 §13.1） ──────────────

        const string Discipline =
            "【纪律】2-4 轮完成：先读后写，一次读全，合并多步"
            + "（多切片一次补丁，多步移子同轮并行）；只读目标切片/棋子，勿遍历无关项；"
            + "信息够就写，写完即收尾，勿重复验证；每轮计费。";

        // ── 3. 边界规则（静态；工具名保持中立，门控只经工具清单） ──────

        const string Boundaries =
            "【边界】只用工具清单中的工具，清单外即不存在。读工具零副作用；写工具只改预览沙箱，"
            + "确认前可 undo。越权会被拒并附合法前缀，按提示修正，勿原样重试。"
            + "指令部分无法实现：先完成可行部分，再用 partial_note 说明；"
            + "没有实质变更时不要提交预览，直接文字回复。";

        // ── 4. 业力口径（静态；裁决 #3 顶格不拦截） ──────────────────

        const string KarmaNote =
            "【业力】投射触顶不拦截，确认时按上限放行；玩家收手时已有变更按半成品计价，无变更不收费。";

        // ── 5. 标准流程（按 allowed 动态裁剪；不得泄漏未解锁工具名） ───

        static string BuildWorkflow(IReadOnlyList<AgentToolDef> defs)
        {
            var has = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in defs) has.Add(d.Name);

            var parts = new List<string>();
            if (has.Contains("get_config_slice") && has.Contains("apply_config_patch") && has.Contains("commit_preview"))
                parts.Add("改规则：get_config_slice(target) → apply_config_patch → commit_preview（棋子路径 /pieces/<type>/moves）");
            if (has.Contains("get_board_state") && has.Contains("move_piece"))
                parts.Add("移子：get_board_state(include=[pieces,board]) → move_piece");
            if (has.Contains("get_board_state") && has.Contains("apply_config_patch"))
                parts.Add("机制：get_board_state(include=[mechanisms]) → apply_config_patch(board_state,/mechanisms/*)，机制项字段 side/remaining/reason");
            if (parts.Count == 0) return "【流程】先读后写；path 须以 / 开头，不确定时省略先读整片。";
            return "【流程】" + string.Join("；", parts.ToArray()) + "。path 须以 / 开头，不确定时省略先读整片。";
        }

        const string ToolsHeader = "【工具】";

        /// <summary>
        /// 组装系统提示（提交前一次；allowedTools 决定清单过滤——门控换轴 ADR-6）。
        /// 输出对 allowedTools 的集合内容敏感、对排列序不敏感（canonical 序列化）。
        ///
        /// 【E3-S2 体积优化】工具 schema 只经请求体 tools 数组下发一次；system 仅列工具名 +
        /// 标准流程。修复前 system 内嵌一份 canonical schema、请求体再发一份，重复 ≈1,600 token。
        /// </summary>
        public static string BuildSystem(IReadOnlyList<string> allowedTools)
        {
            var defs = AgentToolSchemas.ForAllowedTools(allowedTools);
            var sb = new StringBuilder();
            sb.Append(Persona).Append("\n\n");
            sb.Append(Discipline).Append("\n\n");
            sb.Append(Boundaries).Append("\n\n");
            sb.Append(KarmaNote).Append("\n\n");
            sb.Append(BuildWorkflow(defs)).Append("\n\n");
            sb.Append(ToolsHeader);
            for (int i = 0; i < defs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(defs[i].Name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 组装首条 user 消息：玩家指令 + <see cref="BoardSummarizer"/> 紧凑摘要（R4：位置 2，指令在前）。
        /// 不内联整份 board_state/pieces config——读数走工具 1/2（§5.6，token 大幅下降的主要来源）。
        /// </summary>
        public static string BuildUser(string command, string boardSummary)
        {
            var cmd = command ?? string.Empty;
            var summary = boardSummary ?? string.Empty;
            var sb = new StringBuilder();
            sb.Append("【玩家指令】\n").Append(cmd);
            if (summary.Length > 0)
                sb.Append("\n\n【当前局势】\n").Append(summary);
            return sb.ToString();
        }

        /// <summary>token 粗估（UTF8 字节 / 4；中文会被低估约 1/3，仅作预算断言用）。</summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Encoding.UTF8.GetByteCount(text) / 4;
        }
    }
}
