using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ChessSage.Core.Json;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 业力/天道状态快照（E2 由 <c>CheatController.SyncAssessor</c> 真源同步；
    /// E1 测试注入固定值）。get_karma_state 工具与 KarmaFormula 投射共用。
    /// </summary>
    public sealed class AgentKarmaState
    {
        public int Karma;
        public int KarmaMax;
        public int SingleMax;
        public double Detection;
        public string Realm;
        public JObject SkillModifiers;
        public IReadOnlyList<string> AllowedTools;
        // ── 减费技能开关（KarmaFormula.DiscountFactor 的输入；E2 由 SkillModifiers 派生） ──
        public bool EfficiencyFraud;
        public bool HellHungryDiscount;
        public bool HeavenAsuraDiscount;
    }

    /// <summary>
    /// 工具执行上下文（沙箱 + 门控 + 可注入校验/类型表）。
    /// 【E1 边界】ConfigValidator / ObjectiveTypesProvider 是抽象接口编程——
    /// E2 分别接 <c>SchemaValidator.HardValidateConfig</c> 与 objective_types.json；
    /// E1 测试注入 fake。SandboxTransaction 是真实沙箱（E0/E1 已实现）。
    /// </summary>
    public sealed class AgentToolContext
    {
        /// <summary>已解锁工具名集合（Samsara 派生；门控换轴 ADR-6）。</summary>
        public IReadOnlyList<string> AllowedTools;
        /// <summary>
        /// apply_config_patch 的已解锁作用域（顶层配置键；ADR-6 第三条：t1b 解锁 ui_config / board）。
        /// null = 不限制（E1 兼容形态与黄金夹具）；空集 = 全禁。
        /// </summary>
        public IReadOnlyList<string> AllowedPatchTargets;
        public Func<AgentKarmaState> KarmaStateProvider;
        /// <summary>(configName, newSlice) ⇒ (ok, error)；E2 接 SchemaValidator.HardValidateConfig。</summary>
        public Func<string, JObject, (bool ok, string error)> ConfigValidator;
        /// <summary>合法关卡目标类型表（E2 接 objective_types.json）。</summary>
        public Func<IReadOnlyList<string>> ObjectiveTypesProvider;
    }

    /// <summary>
    /// 工具路由器（§4.3 五步派发 + 8 工具实现）。
    ///
    /// 【铁律】
    /// 1. 读工具零突变（只读 sandbox.Configs 视图）；
    /// 2. 工具永不抛异常——一切失败以 {ok:false, errorCode, error, hint} 结构化返回交循环回喂自愈（§5.3）；
    /// 3. 特权写（apply_config_patch/set_objective）过 AgentPathPolicy 白名单 + inputSchema 硬校验
    ///    + ctx.ConfigValidator + 沙箱原子事务（先全部校验，成功才整体生效）；
    /// 4. 写工具调用计数（含失败调用）受 <see cref="AgentLimits.MaxWriteCallsPerSubmit"/> 限额。
    /// </summary>
    public sealed class ToolRouter
    {
        /// <summary>get_board_state 单次返回活子上限（分页保护，§4.2 工具 1）。</summary>
        public const int MaxPiecesPerResponse = 64;

        readonly AgentToolContext _ctx;
        readonly HashSet<string> _allowed;

        public SandboxTransaction Sandbox { get; }
        public AgentToolContext Context => _ctx;

        /// <summary>写工具调用计数（本次提交累计；含业务失败的调用——防失控循环烧 token）。</summary>
        public int WriteCalls { get; private set; }

        /// <summary>commit_preview 已置位（循环据此终止）。</summary>
        public bool CommitRequested { get; private set; }
        public string CommitSummary { get; private set; }
        public string CommitPartialNote { get; private set; }

        public ToolRouter(SandboxTransaction sandbox, AgentToolContext ctx)
        {
            Sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _allowed = new HashSet<string>(StringComparer.Ordinal);
            if (ctx.AllowedTools != null)
                foreach (var name in ctx.AllowedTools)
                    if (!string.IsNullOrEmpty(name)) _allowed.Add(name);
        }

        // ── 五步派发（§4.3） ─────────────────────────────────────────

        /// <summary>
        /// 派发一次工具调用。顺序：①工具名存在性 → ②写调用限额 → ③权限级 →
        /// ④arguments JSON 解析 + inputSchema 校验 → ⑤业务执行（try/catch 兜底）。
        /// 任何一步失败都返回结构化 {ok:false,...}，绝不抛异常。
        /// </summary>
        public JObject Dispatch(LlmToolCall call)
        {
            var name = call?.Name;
            var def = AgentToolSchemas.Find(name);

            // ① 存在性（AGT-04 幻觉工具名自愈）。
            if (def == null)
                return Fail("UNKNOWN_TOOL", $"未知工具: {name ?? "(null)"}",
                    "合法工具: " + string.Join(", ", AgentToolSchemas.All.Select(t => t.Name).ToArray()));

            // ② 写调用限额（§4.2 全局拒绝规则 4；含业务失败调用）。
            if (AgentLimits.IsWriteTool(def.Name))
            {
                WriteCalls++;
                if (!AgentLimits.IsWriteCallsWithinLimit(WriteCalls))
                    return Fail("WRITE_LIMIT_EXCEEDED",
                        $"写工具调用超过单次提交上限 {AgentLimits.MaxWriteCallsPerSubmit}",
                        "拆成多次提交，或先 commit_preview 收尾");
            }

            // ③ 权限级（L0/L1 恒可用；L2 需解锁，AGT-05 越权拒绝）。
            if (def.Permission == AgentPermission.L2 && !_allowed.Contains(def.Name))
                return Fail("TOOL_LOCKED", "天道未允：该能力需在技能树解锁",
                    "先用 get_karma_state 查看已解锁能力，或换用只读/基础工具了解局势");

            // ④ 参数解析 + inputSchema 硬校验（AGT-06 参数损坏自愈）。
            JObject args;
            try
            {
                args = string.IsNullOrEmpty(call.ArgumentsJson)
                    ? new JObject()
                    : JObject.Parse(call.ArgumentsJson);
            }
            catch (Exception)
            {
                return Fail("BAD_ARGS_JSON", "工具参数不是合法 JSON", "按 inputSchema 重新输出完整 JSON 对象");
            }
            if (args == null)
                return Fail("BAD_ARGS_JSON", "工具参数不是合法 JSON", "按 inputSchema 重新输出完整 JSON 对象");

            var schemaErrors = JsonSchemaLite.Validate(def.Schema, args);
            if (schemaErrors.Count > 0)
                return Fail("SCHEMA_INVALID", "参数不符合 inputSchema: " + string.Join("; ", schemaErrors.ToArray()),
                    "对照工具描述的 inputSchema 修正后重试");

            // ⑤ 业务执行（永不抛异常）。
            try
            {
                switch (def.Name)
                {
                    case "get_board_state": return ToolGetBoardState(args);
                    case "get_config_slice": return ToolGetConfigSlice(args);
                    case "get_karma_state": return ToolGetKarmaState();
                    case "move_piece": return ToolMovePiece(args);
                    case "apply_config_patch": return ToolApplyConfigPatch(args);
                    case "set_objective": return ToolSetObjective(args);
                    case "undo_last_change": return ToolUndoLastChange();
                    case "commit_preview": return ToolCommitPreview(args);
                    default:
                        return Fail("UNKNOWN_TOOL", $"工具 {def.Name} 未实现", "这是工程缺陷，请反馈");
                }
            }
            catch (Exception ex)
            {
                return Fail("INTERNAL_ERROR", $"工具内部错误: {ex.Message}", "换种说法或拆成两步重试");
            }
        }

        // ── 工具 1：get_board_state（L0 只读） ───────────────────────

        JObject ToolGetBoardState(JObject args)
        {
            var boardState = Sandbox.Configs["board_state"] as JObject ?? new JObject();
            var include = ReadStringArray(args, "include");
            var wantAll = include.Count == 0;
            var side = args["side"]?.Type == JTokenType.String ? args.Value<string>("side") : "all";
            var recent = args["recent_moves"]?.Type == JTokenType.Integer
                ? args.Value<int>("recent_moves")
                : 5;
            if (recent < 0) recent = 0;
            if (recent > 20) recent = 20;

            var result = new JObject();
            var truncated = false;

            if (wantAll || include.Contains("pieces"))
            {
                var pieces = boardState["pieces"] as JArray ?? new JArray();
                var selected = new JArray();
                var alive = 0;
                foreach (var token in pieces)
                {
                    if (!(token is JObject p)) continue;
                    if (!JsonHelpers.GetBool(p, "is_alive", true)) continue;
                    alive++;
                    if (alive > MaxPiecesPerResponse) { truncated = true; break; }
                    if (side == "all" || JsonHelpers.GetString(p, "side") == side)
                        selected.Add(p.DeepClone());
                }
                result["pieces"] = selected;
                if (truncated)
                    result["note"] = $"活子超过 {MaxPiecesPerResponse}，已截断——用 side/include 收敛查询范围";
            }
            if (wantAll || include.Contains("turn"))
                result["current_turn"] = boardState["current_turn"]?.DeepClone() ?? JValue.CreateString("red");
            if (wantAll || include.Contains("mechanisms"))
                result["mechanisms"] = boardState["mechanisms"]?.DeepClone() ?? new JObject();
            if (wantAll || include.Contains("game_status"))
                result["game_status"] = boardState["game_status"]?.DeepClone() ?? new JObject();
            if (wantAll || include.Contains("recent_moves"))
            {
                var history = boardState["move_history"] as JArray ?? new JArray();
                var from = Math.Max(0, history.Count - recent);
                var tail = new JArray();
                for (int i = from; i < history.Count; i++) tail.Add(history[i].DeepClone());
                result["recent_moves"] = tail;
            }
            if (wantAll || include.Contains("board"))
            {
                var board = Sandbox.Configs["board"] as JObject;
                if (board != null)
                    result["board"] = new JObject
                    {
                        ["width"] = BoardGeometry.Width(board),
                        ["height"] = BoardGeometry.Height(board),
                    };
            }

            return new JObject
            {
                ["ok"] = true,
                ["board_state"] = result,
                ["truncated"] = truncated,
            };
        }

        // ── 工具 2：get_config_slice（L0 只读） ──────────────────────

        static readonly string[] ConfigSliceTargets =
        {
            "board", "rules", "ui_config", "pieces_red", "pieces_black", "pieces_white",
        };

        JObject ToolGetConfigSlice(JObject args)
        {
            var target = args.Value<string>("target");
            if (target == "board_state")
                return Fail("SLICE_NOT_FOUND", "board_state 不是配置切片",
                    "棋盘状态请用 get_board_state；合法值: " + string.Join("/", ConfigSliceTargets));
            if (target == "objective")
                return Fail("SLICE_NOT_FOUND", "objective 不是可读配置切片",
                    "关卡目标只能经 set_objective 覆写；合法值: " + string.Join("/", ConfigSliceTargets));
            if (Array.IndexOf(ConfigSliceTargets, target) < 0)
                return Fail("SLICE_NOT_FOUND", $"配置切片不存在: {target}",
                    "合法值: " + string.Join("/", ConfigSliceTargets));

            var slice = Sandbox.Configs[target];
            if (slice == null)
                return Fail("SLICE_NOT_FOUND", $"配置切片不存在: {target}",
                    "合法值: " + string.Join("/", ConfigSliceTargets));

            var path = args["path"]?.Type == JTokenType.String ? args.Value<string>("path") : string.Empty;
            // JSON Pointer 语法自愈（E3-S2）：模型常把 path 写成 "horse" / "pieces.horse"。
            // 明确回喂「须以 / 开头」的可操作 hint，而不是让 ResolvePointer 抛成 INTERNAL_ERROR。
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("/", StringComparison.Ordinal))
                return ReadFallback(Fail("BAD_POINTER",
                    $"path 必须是 JSON Pointer（以 / 开头），收到 \"{path}\"",
                    "改用 /pieces/horse 这类路径；不确定时省略 path，直接读整片"), slice, target);
            var maxDepth = args["max_depth"]?.Type == JTokenType.Integer ? args.Value<int>("max_depth") : 8;

            JToken value = slice;
            if (!string.IsNullOrEmpty(path))
            {
                value = ResolvePointer(slice, path);
                if (value == null)
                {
                    var hint = "先读整个切片确认结构；可用顶层键: " + TopLevelKeys(slice);
                    if (target != null && target.StartsWith("pieces", StringComparison.Ordinal))
                        hint += "；棋子规则在 /pieces/<type>，如 /pieces/horse；一次改规建议省略 path 读整片";
                    return ReadFallback(Fail("PATH_NOT_FOUND", $"路径不存在: {target}{path}", hint), slice, target);
                }
            }

            var truncated = false;
            if (value is JObject || value is JArray)
            {
                var limited = TruncateDepth(value, maxDepth, ref truncated);
                if (limited != null) value = limited;
            }

            return new JObject
            {
                ["ok"] = true,
                ["target"] = target,
                ["path"] = path ?? string.Empty,
                ["value"] = value?.DeepClone() ?? JValue.CreateNull(),
                ["truncated"] = truncated,
            };
        }

        // ── 工具 3：get_karma_state（L0 只读） ───────────────────────

        JObject ToolGetKarmaState()
        {
            var state = _ctx.KarmaStateProvider?.Invoke() ?? new AgentKarmaState();
            var allowed = new JArray();
            // 门控集合以「本次提交的 ctx」为准（E2：Samsara 派生的 allowed_tools）；
            // state.AllowedTools 仅作 E1 单测注入的回退口径。
            var allowedNames = _ctx.AllowedTools ?? state.AllowedTools;
            if (allowedNames != null)
                foreach (var t in allowedNames) allowed.Add(t);
            return new JObject
            {
                ["ok"] = true,
                ["karma"] = new JObject
                {
                    ["current"] = state.Karma,
                    ["max"] = state.KarmaMax,
                    ["single_max"] = state.SingleMax,
                },
                ["detection"] = state.Detection,
                ["realm"] = state.Realm ?? string.Empty,
                ["skill_modifiers"] = state.SkillModifiers?.DeepClone() ?? new JObject(),
                ["allowed_tools"] = allowed,
            };
        }

        // ── 工具 4：move_piece（L1 写；结构化操作 → 内部补丁） ────────

        JObject ToolMovePiece(JObject args)
        {
            var action = args.Value<string>("action");
            var boardState = Sandbox.Configs["board_state"] as JObject;
            if (boardState == null)
                return Fail("SLICE_NOT_FOUND", "沙箱中没有 board_state", "这是工程缺陷，请反馈");
            var pieces = boardState["pieces"] as JArray;
            if (pieces == null)
                return Fail("SLICE_NOT_FOUND", "board_state 缺少 pieces 数组", "这是工程缺陷，请反馈");

            var (width, height, hasBounds) = BoardBounds();

            int pieceIndex = -1;
            var pieceId = args["piece_id"]?.Type == JTokenType.String ? args.Value<string>("piece_id") : null;
            if (action == "move" || action == "remove")
            {
                if (string.IsNullOrEmpty(pieceId))
                    return Fail("PIECE_ID_REQUIRED", $"{action} 需要 piece_id",
                        "先用 get_board_state 读取棋子 id 列表");
                for (int i = 0; i < pieces.Count; i++)
                {
                    if (!(pieces[i] is JObject p)) continue;
                    if (!JsonHelpers.GetBool(p, "is_alive", true)) continue;
                    if (JsonHelpers.GetString(p, "id") == pieceId) { pieceIndex = i; break; }
                }
                if (pieceIndex < 0)
                {
                    var ids = new List<string>();
                    foreach (var token in pieces)
                        if (token is JObject p && JsonHelpers.GetBool(p, "is_alive", true))
                            ids.Add(JsonHelpers.GetString(p, "id"));
                    return Fail("PIECE_NOT_FOUND", $"找不到存活棋子: {pieceId}",
                        "可用 id: " + string.Join(", ", ids.ToArray()));
                }
            }

            if (action == "remove")
            {
                return ApplyBoardPatch(new JArray
                {
                    new JObject { ["op"] = "replace", ["path"] = $"/pieces/{pieceIndex}/is_alive", ["value"] = false },
                }, "move_piece", $"移除 {pieceId}", $"/pieces/{pieceIndex}/is_alive");
            }

            // move / place 共用的目标格解析。
            int tx, ty;
            var to = args["to"] as JArray;
            if (to != null && to.Count >= 2 && to[0].Type == JTokenType.Integer && to[1].Type == JTokenType.Integer)
            {
                tx = to[0].Value<int>();
                ty = to[1].Value<int>();
            }
            else if (action == "place" && args["new_piece"]?["position"] is JArray np && np.Count >= 2
                     && np[0].Type == JTokenType.Integer && np[1].Type == JTokenType.Integer)
            {
                tx = np[0].Value<int>();
                ty = np[1].Value<int>();
            }
            else
            {
                return Fail("POSITION_REQUIRED", "缺少目标格 to:[x,y]",
                    "目标格为 [x, y] 二元整数数组，须在棋盘范围内");
            }

            if (hasBounds && (tx < 0 || tx >= width || ty < 0 || ty >= height))
                return Fail("OUT_OF_BOUNDS",
                    $"目标格 ({tx},{ty}) 超出棋盘边界 {width}x{height}",
                    $"合法范围 x∈[0,{width - 1}], y∈[0,{height - 1}]；用 get_board_state 读 board 尺寸");

            // 占位校验（允许目标格就是自己的位置——原地操作无意义但合法）。
            if (action == "move")
            {
                var self = pieces[pieceIndex] as JObject;
                var selfPos = self?["position"] as JArray;
                bool isSelf = selfPos != null && selfPos.Count >= 2
                              && selfPos[0].Type == JTokenType.Integer && selfPos[1].Type == JTokenType.Integer
                              && selfPos[0].Value<int>() == tx && selfPos[1].Value<int>() == ty;
                if (!isSelf)
                {
                    foreach (var token in pieces)
                    {
                        if (!(token is JObject p)) continue;
                        if (!JsonHelpers.GetBool(p, "is_alive", true)) continue;
                        var pos = p["position"] as JArray;
                        if (pos == null || pos.Count < 2) continue;
                        if (pos[0].Type != JTokenType.Integer || pos[1].Type != JTokenType.Integer) continue;
                        if (pos[0].Value<int>() == tx && pos[1].Value<int>() == ty)
                            return Fail("TARGET_OCCUPIED", $"目标格 ({tx},{ty}) 已被存活棋子 {p.Value<string>("id")} 占据",
                                "先 remove 该子或另选空格");
                    }
                }
                return ApplyBoardPatch(new JArray
                {
                    new JObject { ["op"] = "replace", ["path"] = $"/pieces/{pieceIndex}/position", ["value"] = new JArray(tx, ty) },
                }, "move_piece", $"移动 {pieceId} 至 ({tx},{ty})", $"/pieces/{pieceIndex}/position");
            }

            // place
            var newPieceArg = args["new_piece"] as JObject;
            if (newPieceArg == null)
                return Fail("NEW_PIECE_REQUIRED", "place 需要 new_piece（至少 type/side）",
                    "参考 inputSchema 的 new_piece 结构");
            var type = newPieceArg.Value<string>("type");
            var side = newPieceArg.Value<string>("side");
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(side))
                return Fail("NEW_PIECE_REQUIRED", "new_piece 至少需要 type 与 side", "参考 inputSchema 的 new_piece 结构");
            foreach (var token in pieces)
            {
                if (!(token is JObject p)) continue;
                if (!JsonHelpers.GetBool(p, "is_alive", true)) continue;
                var pos = p["position"] as JArray;
                if (pos == null || pos.Count < 2) continue;
                if (pos[0].Type != JTokenType.Integer || pos[1].Type != JTokenType.Integer) continue;
                if (pos[0].Value<int>() == tx && pos[1].Value<int>() == ty)
                    return Fail("TARGET_OCCUPIED", $"目标格 ({tx},{ty}) 已被存活棋子 {p.Value<string>("id")} 占据",
                        "先 remove 该子或另选空格");
            }

            var id = newPieceArg["id"]?.Type == JTokenType.String ? newPieceArg.Value<string>("id") : null;
            if (string.IsNullOrEmpty(id))
                id = GeneratePieceId(pieces, side, type);
            var name = newPieceArg["name"]?.Type == JTokenType.String ? newPieceArg.Value<string>("name") : null;
            if (string.IsNullOrEmpty(name))
                name = BoardSummarizer.CanonicalPieceName(type, null);

            var newPiece = new JObject
            {
                ["id"] = id,
                ["type"] = type,
                ["name"] = name,
                ["side"] = side,
                ["position"] = new JArray(tx, ty),
                ["is_alive"] = true,
            };
            return ApplyBoardPatch(new JArray
            {
                new JObject { ["op"] = "add", ["path"] = "/pieces/-", ["value"] = newPiece },
            }, "move_piece", $"放置 {name}({id}) 于 ({tx},{ty})", "/pieces/-");
        }

        /// <summary>board_state 补丁共用管线：白名单 → IsValid → 克隆试应用 → 沙箱原子生效。</summary>
        JObject ApplyBoardPatch(JArray patch, string toolName, string description, string changedPath)
        {
            foreach (var token in patch)
            {
                var op = token as JObject;
                var path = op?.Value<string>("path") ?? string.Empty;
                var opName = op?.Value<string>("op") ?? string.Empty;
                // place 的结构化追加（/pieces/-）等价于白名单内的 /pieces/*/…；业务规则已校验。
                if (opName == "add" && path == "/pieces/-") continue;
                var reject = AgentPathPolicy.Reject("board_state", path);
                if (reject != null)
                    return Fail("PATH_REJECTED", reject.Error, reject.Hint);
            }
            var (valid, error) = JsonPatch.IsValid(patch);
            if (!valid)
                return Fail("INVALID_PATCH", $"补丁不合法: {error}", "对照 RFC 6902 修正 op/path/value");

            var current = Sandbox.Configs["board_state"] as JObject ?? new JObject();
            JToken applied;
            try
            {
                applied = JsonPatch.Apply(current, patch);
            }
            catch (Exception ex)
            {
                return Fail("PATCH_APPLY_FAILED", $"补丁应用失败: {ex.Message}", "检查 path 指向是否存在");
            }
            if (!(applied is JObject))
                return Fail("PATCH_APPLY_FAILED", "补丁应用后 board_state 不是对象", "这是工程缺陷，请反馈");

            // E3-C：move_piece（落子/移除/放置）是棋盘状态层的机械写入。
            var result = Sandbox.TryApplySlice("board_state", applied, toolName, description,
                new[] { AgentScope.BoardState });
            if (!string.IsNullOrEmpty(result.Error))
                return Fail("SANDBOX_REJECTED", result.Error, "这是工程缺陷，请反馈");
            if (!result.Changed)
                return new JObject { ["ok"] = true, ["changed"] = (JToken)null, ["note"] = "无变化" };

            return new JObject
            {
                ["ok"] = true,
                ["changed"] = changedPath,
                ["inverse_available"] = true,
            };
        }

        // ── 工具 5：apply_config_patch（L2 特权写，核心） ─────────────

        JObject ToolApplyConfigPatch(JObject args)
        {
            var operations = args["operations"] as JArray;
            if (operations == null || operations.Count == 0)
                return Fail("SCHEMA_INVALID", "operations 不得为空", "至少一个 (target, patch) 操作");

            int totalOps = 0;
            foreach (var token in operations)
            {
                var op = token as JObject;
                var patch = op?["patch"] as JArray;
                if (patch == null)
                    return Fail("SCHEMA_INVALID", "每个操作必须含 target 与 patch 数组", "对照 inputSchema");
                totalOps += patch.Count;
            }
            if (!AgentLimits.IsOpCountWithinLimit(totalOps))
                return Fail("OPS_LIMIT_EXCEEDED",
                    $"单次调用 op 数 {totalOps} 超过上限 {AgentLimits.MaxOpsPerCall}",
                    "拆成多次调用，或合并为更少操作");

            // 逐操作：补丁语法 + 白名单 + 作用域门控。
            foreach (var token in operations)
            {
                var op = (JObject)token;
                var target = op.Value<string>("target");
                if (IsPatchTargetLocked(target))
                    return Fail("SCOPE_LOCKED",
                        $"天道未允：{target ?? "(null)"} 这类改动尚未解锁",
                        AllowedPatchTargetsHint());
                var patch = (JArray)op["patch"];
                var (valid, error) = JsonPatch.IsValid(patch);
                if (!valid)
                    return Fail("INVALID_PATCH", $"补丁不合法: {error}", "对照 RFC 6902 修正 op/path/value");
                foreach (var opToken in patch)
                {
                    var opObj = opToken as JObject;
                    var path = opObj?.Value<string>("path") ?? string.Empty;
                    var reject = AgentPathPolicy.Reject(target, path);
                    if (reject != null)
                        return Fail("PATH_REJECTED", reject.Error, reject.Hint);
                }
            }

            // 按 target 分组（保持首次出现序）——克隆上试应用 + 硬校验。
            var staged = new List<KeyValuePair<string, JObject>>();
            var stagedScopes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in operations)
            {
                var op = (JObject)token;
                var target = op.Value<string>("target");
                if (seen.Contains(target)) continue;
                seen.Add(target);

                var current = Sandbox.Configs[target] as JObject ?? new JObject();
                var clone = (JObject)current.DeepClone();
                var scopes = new List<string>();
                foreach (var other in operations)
                {
                    var otherOp = (JObject)other;
                    if (otherOp.Value<string>("target") != target) continue;
                    try
                    {
                        clone = JsonPatch.Apply(clone, (JArray)otherOp["patch"]) as JObject;
                    }
                    catch (Exception ex)
                    {
                        return Fail("PATCH_APPLY_FAILED", $"{target} 补丁应用失败: {ex.Message}", "检查 path 指向是否存在");
                    }
                    if (clone == null)
                        return Fail("PATCH_APPLY_FAILED", $"{target} 补丁应用后不是对象", "这是工程缺陷，请反馈");
                    // E3-C：作用域是补丁的机械属性（target + op + JSON Pointer），不是意图分类。
                    foreach (var opToken in (JArray)otherOp["patch"])
                    {
                        var patchOp = opToken as JObject;
                        var scope = AgentScope.ForPatch(target,
                            patchOp?.Value<string>("op"), patchOp?.Value<string>("path") ?? string.Empty);
                        if (!string.IsNullOrEmpty(scope) && !scopes.Contains(scope))
                            scopes.Add(scope);
                    }
                }

                var validate = _ctx.ConfigValidator;
                if (validate != null)
                {
                    var (ok, error) = validate(target, clone);
                    if (!ok)
                    {
                        var hint = string.IsNullOrEmpty(error)
                            ? "修正后重试，或先 get_config_slice 核对结构"
                            : error;
                        if (target == "board_state")
                            hint += "；机制项示例 {\"side\":\"black\",\"remaining\":1,\"reason\":\"停手\"}（skip_turns/ai_control/random_moves/extra_turns），player_control 用 {side}";
                        else if (target != null && target.StartsWith("pieces", StringComparison.Ordinal))
                            hint += "；走法项必填 kind(jump|ray)/land(empty|enemy|any)/sym(none|rotate4|rotate4_mirror|mirror_x)，to 或 dir 二选一；示例 {\"kind\":\"jump\",\"to\":[1,0],\"block\":[],\"land\":\"any\",\"sym\":\"mirror_x\"}";
                        return Fail("CONFIG_INVALID", $"{target} 验证失败: {error}", hint);
                    }
                }
                staged.Add(new KeyValuePair<string, JObject>(target, clone));
                stagedScopes[target] = scopes;
            }

            // 全部通过 ⇒ 整体生效（原子）。
            var summary = new JArray();
            int applied = 0;
            foreach (var kv in staged)
            {
                var result = Sandbox.TryApplySlice(kv.Key, kv.Value, "apply_config_patch", $"补丁修改 {kv.Key}",
                    stagedScopes.TryGetValue(kv.Key ?? string.Empty, out var scopes) ? scopes : null);
                if (!string.IsNullOrEmpty(result.Error))
                    return Fail("SANDBOX_REJECTED", result.Error, "这是工程缺陷，请反馈");
                if (!result.Changed) continue;
                applied++;
                var opsForTarget = 0;
                foreach (var token in operations)
                    if (((JObject)token).Value<string>("target") == kv.Key)
                        opsForTarget += ((JArray)((JObject)token)["patch"]).Count;
                summary.Add($"{kv.Key}: {opsForTarget.ToString(CultureInfo.InvariantCulture)} ops");
            }

            return new JObject
            {
                ["ok"] = true,
                ["applied"] = applied,
                ["operations"] = summary,
                ["inverse_available"] = true,
            };
        }

        // ── 工具 6：set_objective（L2 特权写，新能力） ────────────────

        JObject ToolSetObjective(JObject args)
        {
            var type = args.Value<string>("type");
            var known = _ctx.ObjectiveTypesProvider?.Invoke() ?? Array.Empty<string>();
            var found = false;
            foreach (var t in known)
                if (string.Equals(t, type, StringComparison.Ordinal)) { found = true; break; }
            if (!found)
                return Fail("UNKNOWN_OBJECTIVE_TYPE", $"未知目标类型: {type ?? "(null)"}",
                    "合法类型: " + string.Join(", ", known));

            var objective = new JObject
            {
                ["type"] = type,
                ["params"] = (args["params"] as JObject)?.DeepClone() ?? new JObject(),
            };
            var result = Sandbox.TryApplySlice("objective", objective, "set_objective", $"设置关卡目标 {type}",
                new[] { AgentScope.Objectives });
            if (!string.IsNullOrEmpty(result.Error))
                return Fail("SANDBOX_REJECTED", result.Error, "这是工程缺陷，请反馈");
            if (!result.Changed)
                return new JObject { ["ok"] = true, ["objective"] = objective, ["note"] = "无变化" };

            return new JObject { ["ok"] = true, ["objective"] = objective };
        }

        // ── 工具 7：undo_last_change（L1 元写） ──────────────────────

        JObject ToolUndoLastChange()
        {
            var change = Sandbox.Undo();
            if (change == null)
                return Fail("NO_UNDO", "没有可回退的变更", "只读工具与失败调用不入栈");

            return new JObject
            {
                ["ok"] = true,
                ["undone"] = new JObject
                {
                    ["tool"] = change.Tool ?? string.Empty,
                    ["description"] = change.Description ?? string.Empty,
                },
                ["remaining_changes"] = Sandbox.UndoDepth,
            };
        }

        // ── 工具 8：commit_preview（L1 元写；终止循环） ──────────────

        JObject ToolCommitPreview(JObject args)
        {
            CommitRequested = true;
            CommitSummary = args.Value<string>("summary") ?? string.Empty;
            CommitPartialNote = args["partial_note"]?.Type == JTokenType.String
                ? args.Value<string>("partial_note")
                : string.Empty;

            return new JObject
            {
                ["ok"] = true,
                ["committed"] = true,
                ["changes"] = Sandbox.UndoDepth,
            };
        }

        // ── 内部工具 ─────────────────────────────────────────────────

        (int width, int height, bool hasBounds) BoardBounds()
        {
            var board = Sandbox.Configs["board"] as JObject;
            if (board == null) return (0, 0, false);
            var w = BoardGeometry.Width(board);
            var h = BoardGeometry.Height(board);
            return w > 0 && h > 0 ? (w, h, true) : (0, 0, false);
        }

        static string GeneratePieceId(JArray pieces, string side, string type)
        {
            var seq = 1;
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in pieces)
                if (token is JObject p)
                    existing.Add(JsonHelpers.GetString(p, "id"));
            string id;
            do
            {
                id = $"{side}_{type}_{seq.ToString(CultureInfo.InvariantCulture)}";
                seq++;
            } while (existing.Contains(id));
            return id;
        }

        static List<string> ReadStringArray(JObject args, string key)
        {
            var list = new List<string>();
            var token = args[key];
            if (token is JArray arr)
                foreach (var t in arr)
                    if (t.Type == JTokenType.String)
                        list.Add(t.Value<string>());
            return list;
        }

        /// <summary>
        /// 读失败回喂整片 fallback（E3-S2 自愈）：read 工具零副作用，路径猜错时把整片附回，
        /// 模型下一轮可直接按顶层键写补丁，避免「错误 → 再读 → 轮次耗尽」。
        /// </summary>
        static JObject ReadFallback(JObject failure, JToken slice, string target)
        {
            failure["fallback_target"] = target ?? string.Empty;
            failure["fallback_value"] = slice?.DeepClone() ?? JValue.CreateNull();
            failure["note"] = "path 不存在；已附整片 fallback，可直接按顶层键写补丁";
            return failure;
        }

        /// <summary>顶层键摘要（PATH_NOT_FOUND 的可操作 hint；固定属性序，确定性）。</summary>
        static string TopLevelKeys(JToken token)
        {
            if (token is JObject obj)
            {
                var keys = new List<string>();
                foreach (var prop in obj.Properties())
                {
                    keys.Add(prop.Name);
                    if (keys.Count >= 12) break;
                }
                return keys.Count == 0 ? "(空对象)" : string.Join(", ", keys.ToArray());
            }
            if (token is JArray arr) return $"数组长度 {arr.Count}";
            return token == null ? "null" : token.Type.ToString();
        }

        /// <summary>JSON Pointer 导航（RFC 6901 段转义 ~1→/ ~0→~；不存在返回 null）。</summary>
        static JToken ResolvePointer(JToken root, string pointer)
        {
            if (string.IsNullOrEmpty(pointer)) return root;
            var segments = JsonPatch.ParsePointer(pointer);
            var current = root;
            foreach (var seg in segments)
            {
                if (current is JObject obj)
                {
                    JToken next;
                    if (!obj.TryGetValue(seg, StringComparison.Ordinal, out next)) return null;
                    current = next;
                }
                else if (current is JArray arr)
                {
                    int index;
                    if (!int.TryParse(seg, out index) || index < 0 || index >= arr.Count) return null;
                    current = arr[index];
                }
                else
                {
                    return null;
                }
            }
            return current;
        }

        /// <summary>深度截断（超出 maxDepth 的子树替换为截断标记；截断发生返回 false 表示被截）。</summary>
        static JToken TruncateDepth(JToken token, int maxDepth, ref bool truncated)
        {
            if (maxDepth <= 0)
            {
                truncated = true;
                return new JObject { ["_truncated"] = true, ["_hint"] = "超过 max_depth，用 path 取子树" };
            }
            if (token is JObject obj)
            {
                var copy = new JObject();
                foreach (var prop in obj.Properties())
                    copy[prop.Name] = TruncateDepth(prop.Value, maxDepth - 1, ref truncated);
                return copy;
            }
            if (token is JArray arr)
            {
                var copy = new JArray();
                foreach (var item in arr)
                    copy.Add(TruncateDepth(item, maxDepth - 1, ref truncated));
                return copy;
            }
            return token.DeepClone();
        }

        static JObject Fail(string errorCode, string error, string hint)
            => new JObject
            {
                ["ok"] = false,
                ["errorCode"] = errorCode,
                ["error"] = error,
                ["hint"] = hint ?? string.Empty,
            };

        /// <summary>
        /// 作用域门控（ADR-6 第三条）：apply_config_patch 的 target 是否已解锁。
        /// null = 不限制（E1 兼容）；未解锁 ⇒ SCOPE_LOCKED + 可操作 hint（Agent 会转达给玩家）。
        /// </summary>
        bool IsPatchTargetLocked(string target)
        {
            var allowed = _ctx.AllowedPatchTargets;
            if (allowed == null) return false;
            if (string.IsNullOrEmpty(target)) return true;
            foreach (var t in allowed)
                if (string.Equals(t, target, StringComparison.Ordinal)) return false;
            return true;
        }

        string AllowedPatchTargetsHint()
        {
            var allowed = _ctx.AllowedPatchTargets;
            if (allowed == null) return "作用域未受限";
            var parts = new List<string>();
            foreach (var t in allowed) parts.Add(t);
            var text = "已解锁作用域: " + string.Join(", ", parts.ToArray());
            foreach (var advanced in new[] { "ui_config", "board" })
                if (parts.IndexOf(advanced) < 0) text += $"（{advanced} 需 cheat_mastery_t1b「前端修改」）";
            return text;
        }
    }
}
