using System;
using System.Collections.Generic;
using ChessSage.Core.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 一次工具调用的轨迹条目（有序；UX 推演摘要与 QA 黄金断言共用，§10.3）。
    /// </summary>
    public sealed class AgentTrajectoryEntry
    {
        /// <summary>发生轮次（1 起）。</summary>
        public int Round;
        public string ToolName;
        /// <summary>原始 arguments JSON 字符串（原样保留，回放用）。</summary>
        public string ArgumentsJson;
        /// <summary>结构化结果 JSON（工具返回的 {ok,...} 序列化）。</summary>
        public string ResultJson;
        public bool Ok;
        /// <summary>失败时的机器可读错误码（ok=false 时非空）。</summary>
        public string ErrorCode;
    }

    /// <summary>
    /// 实时进度载荷（§7.5；每轮一次，P0 轮级粒度）。
    /// UI 渲染：「天道推演中… 已耗 {TokensSoFar} token · 业力 {ProjectedKarma} 且仍在增长」；
    /// CapWarning=true 时计数器转琥珀→红并追加顶格警示行（文案归设计侧）。
    /// </summary>
    public sealed class AgentProgress
    {
        /// <summary>当前轮次（1 起）。</summary>
        public int Round;
        /// <summary>本次提交累计 T_eff = Σ(prompt+completion)（thinking 全额含在 completion）。</summary>
        public long TokensSoFar;
        /// <summary>其中「改数」= Σ prompt（设计 §1.7 计数器拆分展示用）。</summary>
        public long PromptTokens;
        /// <summary>其中「思索」= Σ completion（含 thinking，DeepSeek 计费口径）。</summary>
        public long CompletionTokens;
        /// <summary>投射业力 = round(f(T_eff))，σ=1「预估·未含天意浮动」（不 clamp——超 120 即顶格判据）。</summary>
        public int ProjectedKarma;
        /// <summary>是否仍在增长（尚未收尾）。循环内显式置 true。</summary>
        public bool Growing;
        /// <summary>"thinking" | "tool" | "wrap-up"。</summary>
        public string CurrentPhase = "thinking";
        /// <summary>最近一次完成的工具调用名（E2-S4 轨迹行文案；未知工具名原样回传）。</summary>
        public string LastToolName;
        /// <summary>该工具是否成功（失败行文案「天道蹙眉」）。</summary>
        public bool LastToolOk = true;
        /// <summary>投射触顶警示（§5.5 裁决 #3：只警示不拦截，循环继续）。</summary>
        public bool CapWarning;
        /// <summary>20s 到点（§13.6 收尾策略 A）。</summary>
        public bool TimedOut;
        /// <summary>P1 流式增量 token（P0 恒 0）。</summary>
        public long ChunkTokens;
    }

    /// <summary>
    /// Agent 循环结果对象（引擎内部契约；<see cref="ToResultJson"/> 转
    /// <c>CheatController.ConvertResult</c> 兼容的 JObject——该文件不改逻辑）。
    /// 暴露 <see cref="TokenEff"/> / <see cref="Rounds"/> / <see cref="Trajectory"/> /
    /// <see cref="Karma"/>（全分解）供 UX 与测试共用。
    /// </summary>
    public sealed class AgentResult
    {
        /// <summary>applied | partial | fun | rejected | error。</summary>
        public string Type = "error";
        public string Message = "";
        /// <summary>仅差异切片、值为全量（applied/partial 时非空）。</summary>
        public JObject ModifiedConfigs;
        /// <summary>业力终值（失败 0 / 闲聊 1 / 其余按公式）。</summary>
        public int EstimatedKarmaCost;
        /// <summary>partial 预览的未完成说明（触发 partial 语义）。</summary>
        public string PartialNote = "";
        /// <summary>
        /// 失败/半成品的机器可读错误码（E2-S5 翻译输入）：TIMEOUT / TOOL_LOCKED / SCOPE_LOCKED /
        /// BAD_ARGS_JSON / SCHEMA_INVALID / CONFIG_INVALID / TOOL_ERRORS / REPEAT_LOOP / ROUNDS_EXHAUSTED …
        /// </summary>
        public string ErrorCode = "";
        /// <summary>可操作提示（工具层 hint 原样透传；失败卡文案的素材，非玩家可见原文）。</summary>
        public string Hint = "";
        public bool Success => Type == "applied" || Type == "partial" || Type == "fun";

        // ── 遥测与回放（UX 与测试共用） ──
        public long TokenEff;
        public int Rounds;
        /// <summary>本次提交实际产生变更的成功写入次数（sandbox.Writes）。</summary>
        public int Writes;
        public List<AgentTrajectoryEntry> Trajectory = new List<AgentTrajectoryEntry>();
        /// <summary>业力结算全分解（纯失败路径 Failed=true / 零写入 ZeroWrite=true）。</summary>
        public KarmaResult Karma;
        /// <summary>
        /// 本次提交的主导作用域（权重最大者；零写入/失败为 null）。E3-C 校准审计字段。
        /// </summary>
        public string Scope;
        /// <summary>生效的作用域深度权重（无作用域 1.0）。</summary>
        public double ScopeWeight = 1.0;
        /// <summary>触及的全部成功写入作用域（按发生序，去重；审计/回放用）。</summary>
        public IReadOnlyList<string> ScopeTags = new string[0];
        public bool TimedOut;
        public bool CapWarned;
        /// <summary>非致命警告（finish_reason=length 截断、循环内压缩等）。</summary>
        public List<string> Warnings = new List<string>();

        /// <summary>
        /// 结果契约 JObject（与旧编排器的 ProcessCommandAsync 输出同构（E2-S7 已下线该实现，契约保留），
        /// <c>CheatController.ConvertResult</c> 零改消费）：
        /// {success, type, message, modified_configs?, estimated_karma_cost, partial_note?, karma_blocked:false}。
        /// E2 追加（可选键，旧消费方忽略即兼容）：error_code / hint / cap_warned —— 供失败卡翻译与顶格警示。
        /// </summary>
        public JObject ToResultJson()
        {
            var json = new JObject
            {
                ["success"] = Success,
                ["type"] = Type,
                ["message"] = Message ?? string.Empty,
                ["estimated_karma_cost"] = EstimatedKarmaCost,
                ["karma_blocked"] = false, // 裁决 #3：无「业力拦截」路径，字段保留供 UI 兼容
            };
            if (ModifiedConfigs != null && ModifiedConfigs.HasValues)
                json["modified_configs"] = ModifiedConfigs;
            if (!string.IsNullOrEmpty(PartialNote))
                json["partial_note"] = PartialNote;
            if (!string.IsNullOrEmpty(ErrorCode))
                json["error_code"] = ErrorCode;
            if (!string.IsNullOrEmpty(Hint))
                json["hint"] = Hint;
            if (CapWarned)
                json["cap_warned"] = true;
            // E3-C 校准审计：只在有作用域时追加（旧消费方忽略未知键即兼容）。
            if (!string.IsNullOrEmpty(Scope))
            {
                json["scope"] = Scope;
                json["scope_weight"] = ScopeWeight;
            }
            return json;
        }

        /// <summary>失败卡（0 点，不收费；§13.6 无写入收尾）。</summary>
        public static AgentResult Failure(string message, string reason, long tokenEff, int rounds,
            bool timedOut = false, List<AgentTrajectoryEntry> trajectory = null, List<string> warnings = null,
            string errorCode = null, string hint = null)
        {
            return new AgentResult
            {
                Type = "error",
                Message = string.IsNullOrEmpty(reason) ? message : $"{message}：{reason}",
                TokenEff = tokenEff,
                Rounds = rounds,
                Trajectory = trajectory ?? new List<AgentTrajectoryEntry>(),
                TimedOut = timedOut,
                Warnings = warnings ?? new List<string>(),
                ErrorCode = errorCode ?? (timedOut ? "TIMEOUT" : ""),
                Hint = hint ?? "",
                Karma = new KarmaResult { Failed = true, ZeroWrite = true, Karma = 0 },
            };
        }

        /// <summary>闲聊等价（零写入成功，恒 1 点；旧 E 类同口径）。</summary>
        public static AgentResult Fun(string message, long tokenEff, int rounds,
            List<AgentTrajectoryEntry> trajectory = null, List<string> warnings = null)
        {
            return new AgentResult
            {
                Type = "fun",
                Message = message ?? string.Empty,
                EstimatedKarmaCost = 1,
                TokenEff = tokenEff,
                Rounds = rounds,
                Trajectory = trajectory ?? new List<AgentTrajectoryEntry>(),
                Warnings = warnings ?? new List<string>(),
                Karma = new KarmaResult { ZeroWrite = true, Karma = 1 },
            };
        }
    }

    /// <summary>
    /// 沙箱事务（ADR-5）：① 提交时 <c>BuildConfigs()</c> 深拷贝快照；
    /// ② 变更栈（每次成功写入压入 (target, 逆补丁, 描述)）。
    /// 全部 L1/L2 工具只改沙箱；确认后才经既有链路生效（E2 组合根接线）。
    ///
    /// 【原子性】TryApplySlice 整体替换目标切片——任一失败整体拒绝，不留半套补丁。
    /// 【可回放】逆补丁 = JsonPatch.GenerateDiff(改后, 改前)，LIFO 弹栈应用。
    /// </summary>
    public sealed class SandboxTransaction
    {
        /// <summary>合法切片键（固定序——BuildModifiedConfigs 与渲染消费方的输出序稳定）。</summary>
        public static readonly string[] SliceOrder =
        {
            "board_state", "board", "rules", "ui_config", "pieces_red", "pieces_black", "pieces_white", "objective",
        };

        /// <summary>提交时快照（深拷贝，永不改变）。</summary>
        public JObject Snapshot { get; }

        /// <summary>当前沙箱配置（写工具只改这里）。</summary>
        public JObject Configs { get; private set; }

        /// <summary>实际产生变更的成功写入次数（KarmaFormula 的 writes&gt;0 判据）。</summary>
        public int Writes { get; private set; }

        readonly List<SandboxChange> _undoStack = new List<SandboxChange>();
        readonly List<string> _changedScopes = new List<string>();

        /// <summary>
        /// 成功写入触及的配置作用域（E3-C，按发生序去重；读工具/无变化/失败调用不入列）。
        /// 与 <see cref="Writes"/> 同源递增——不会出现「有写入但无作用域」的分叉。
        /// </summary>
        public IReadOnlyList<string> ChangedScopes => _changedScopes;

        public SandboxTransaction(JObject configs)
        {
            Snapshot = configs == null ? new JObject() : (JObject)configs.DeepClone();
            Configs = (JObject)Snapshot.DeepClone();
        }

        /// <summary>可回退变更数。</summary>
        public int UndoDepth => _undoStack.Count;

        public bool HasChanges => BuildModifiedConfigs().HasValues;

        /// <summary>栈顶变更（不弹出）；空栈返回 null。</summary>
        public SandboxChange Peek() => _undoStack.Count > 0 ? _undoStack[_undoStack.Count - 1] : null;

        /// <summary>
        /// 回退栈顶变更（应用逆补丁）；空栈返回 null。
        /// Writes 为历史计数不递减——「本次提交是否有过成功写入」是语义，不是当前 diff。
        /// </summary>
        public SandboxChange Undo()
        {
            if (_undoStack.Count == 0) return null;
            var change = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);

            if (change.RemovedOnUndo)
            {
                // 快照外键新增（如 objective）——回退即移除整个键，逆补丁无应用对象。
                Configs.Remove(change.Target);
                return change;
            }

            var target = Configs[change.Target] as JObject;
            if (target != null)
            {
                var restored = JsonPatch.Apply(target, change.InversePatch) as JObject;
                if (restored != null)
                    Configs[change.Target] = restored;
            }
            return change;
        }

        /// <summary>
        /// 尝试以新值整体替换目标切片（调用方已完成业务校验与补丁应用）。
        /// 无变化 ⇒ Changed=false（不计数、不压栈）；未知 target ⇒ 拒绝。
        /// </summary>
        /// <param name="scopes">
        /// 本次写入触及的作用域（E3-C；由 ToolRouter 按补丁形状解析，null/空 = 中性）。
        /// 只在真实产生变更时记入 <see cref="ChangedScopes"/>。
        /// </param>
        public SandboxApplyResult TryApplySlice(string target, JToken newValue, string toolName, string description,
            IEnumerable<string> scopes = null)
        {
            if (target == null || Array.IndexOf(SliceOrder, target) < 0)
                return SandboxApplyResult.Rejected($"未知配置切片: {target ?? "(null)"}");

            var current = Configs[target];
            if (current != null && JToken.DeepEquals(current, newValue))
                return SandboxApplyResult.NoChange();

            var inverse = JsonPatch.GenerateDiff(newValue, current ?? new JObject());
            var changeScopes = new List<string>();
            if (scopes != null)
                foreach (var scope in scopes)
                    if (!string.IsNullOrEmpty(scope) && !changeScopes.Contains(scope))
                        changeScopes.Add(scope);
            _undoStack.Add(new SandboxChange
            {
                Tool = toolName,
                Target = target,
                Description = description ?? string.Empty,
                InversePatch = inverse,
                RemovedOnUndo = current == null,
                Scopes = changeScopes.ToArray(),
            });
            if (newValue == null || newValue.Type == JTokenType.Null)
                Configs.Remove(target);
            else
                Configs[target] = newValue.DeepClone();
            Writes++;
            foreach (var scope in changeScopes)
                if (!_changedScopes.Contains(scope))
                    _changedScopes.Add(scope);
            return SandboxApplyResult.Applied();
        }

        /// <summary>
        /// 仅差异切片、值为全量（<c>CheatDiffRenderer.Render</c> 兼容契约：
        /// 渲染器对每个键做 GenerateDiff(旧, 新)——要的是新值全量，不是补丁数组）。
        /// 键序固定（<see cref="SliceOrder"/>）——输出字节级稳定。
        /// </summary>
        public JObject BuildModifiedConfigs()
        {
            var modified = new JObject();
            foreach (var key in SliceOrder)
            {
                var current = Configs[key];
                var original = Snapshot[key];
                if (current == null && original == null) continue;
                if (current != null && original != null && JToken.DeepEquals(current, original)) continue;
                if (current != null)
                    modified[key] = current.DeepClone();
            }
            return modified;
        }

        /// <summary>全量 diff（快照 → 当前；RFC6902 补丁数组，遥测/测试用）。</summary>
        public JArray ComputeDiff() => JsonPatch.GenerateDiff(Snapshot, Configs);
    }

    /// <summary>沙箱写入结果（成功/无变化/拒绝）。</summary>
    public sealed class SandboxApplyResult
    {
        public bool Changed;
        public string Error;

        public static SandboxApplyResult Applied() => new SandboxApplyResult { Changed = true };
        public static SandboxApplyResult NoChange() => new SandboxApplyResult { Changed = false };
        public static SandboxApplyResult Rejected(string error) => new SandboxApplyResult { Changed = false, Error = error };
    }

    /// <summary>可回退变更（逆补丁 = GenerateDiff(改后, 改前)）。</summary>
    public sealed class SandboxChange
    {
        public string Tool;
        public string Target;
        public string Description;
        public JArray InversePatch;
        /// <summary>该切片在快照中不存在（回退 = 移除整个键）。</summary>
        public bool RemovedOnUndo;
        /// <summary>本次写入触及的作用域（E3-C；审计用）。</summary>
        public string[] Scopes = new string[0];
    }
}
