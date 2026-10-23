namespace ChessSage.Core.Agent
{
    /// <summary>
    /// Agent 工具调用限额（工程侧 §4.2 全局拒绝规则 4；§13.1 由 20s 预算反推的保险丝）。
    /// 纯谓词——实际计数状态在 E1 的 ToolRouter，此处只放可单测的判定与常量。
    /// </summary>
    public static class AgentLimits
    {
        /// <summary>单次 apply_config_patch 调用的 op 数上限。</summary>
        public const int MaxOpsPerCall = 32;

        /// <summary>单次提交累计写工具调用上限（防失控循环烧 token；元写 undo/commit 亦计入）。</summary>
        public const int MaxWriteCallsPerSubmit = 24;

        public static bool IsOpCountWithinLimit(int ops) => ops >= 0 && ops <= MaxOpsPerCall;

        public static bool IsWriteCallsWithinLimit(int writeCalls) => writeCalls >= 0 && writeCalls <= MaxWriteCallsPerSubmit;

        /// <summary>是否写类工具（入写调用计数）：L1/L2 全部五件；只读三件与未知名不计。</summary>
        public static bool IsWriteTool(string toolName)
        {
            switch (toolName)
            {
                case "move_piece":
                case "apply_config_patch":
                case "set_objective":
                case "undo_last_change":
                case "commit_preview":
                    return true;
                default:
                    return false;
            }
        }
    }
}
