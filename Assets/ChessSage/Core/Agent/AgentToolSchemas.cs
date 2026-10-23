using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>工具权限级（§4.2 / ADR-6）：L0 只读恒可用；L1 写恒可用；L2 特权写需解锁。</summary>
    public enum AgentPermission
    {
        L0 = 0,
        L1 = 1,
        L2 = 2,
    }

    /// <summary>工具定义（强类型注册表条目）。</summary>
    public sealed class AgentToolDef
    {
        public string Name;
        public string Description;
        /// <summary>inputSchema 的紧凑 JSON 常量（§4.2 逐字）；构造时解析校验。</summary>
        public string InputSchemaJson;
        public AgentPermission Permission;
        public bool IsWrite;

        /// <summary>解析后的 Schema（键序 = 常量书写序 = §4.2 序）。</summary>
        public JObject Schema { get; }

        public AgentToolDef(string name, string description, string inputSchemaJson, AgentPermission permission, bool isWrite)
        {
            Name = name;
            Description = description;
            InputSchemaJson = inputSchemaJson;
            Permission = permission;
            IsWrite = isWrite;
            try
            {
                Schema = JObject.Parse(inputSchemaJson);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"工具 {name} 的 inputSchema 不是合法 JSON：{ex.Message}", nameof(inputSchemaJson), ex);
            }
        }
    }

    /// <summary>
    /// 8 工具 Schema 注册表（E0-S3；工程侧 §4.2 逐字合同）。
    ///
    /// 【字节级稳定（前缀缓存地基 R1-R7）】序列化走注册表固定序 + 名称去重，
    /// 与调用方插入序无关——同集合任意排列产出同一串（SHA-256 一致）。
    /// 【线格式】OpenAI 兼容 function calling：[{"type":"function","function":{name,description,parameters}}]
    /// （DeepSeek 同协议；E1-S1 直接嵌入请求体）。
    /// </summary>
    public static class AgentToolSchemas
    {
        // ── inputSchema 常量（§4.2 逐字转录；键序即合同） ──

        public const string GetBoardStateInputSchema = @"{""type"":""object"",""properties"":{""include"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""pieces"",""turn"",""mechanisms"",""game_status"",""recent_moves"",""board""]},""default"":[""pieces"",""turn"",""mechanisms""],""description"":""需要的部分，按需索取以节省上下文""},""side"":{""type"":""string"",""enum"":[""red"",""black"",""white"",""all""],""default"":""all""},""recent_moves"":{""type"":""integer"",""minimum"":0,""maximum"":20,""default"":5}},""required"":[]}";

        public const string GetConfigSliceInputSchema = @"{""type"":""object"",""properties"":{""target"":{""type"":""string"",""enum"":[""board"",""rules"",""ui_config"",""pieces_red"",""pieces_black"",""pieces_white""]},""path"":{""type"":""string"",""description"":""可选 JSON Pointer，如 /ai_difficulty/personality；缺省返回整个切片"",""default"":""""},""max_depth"":{""type"":""integer"",""minimum"":1,""maximum"":12,""default"":8}},""required"":[""target""]}";

        public const string GetKarmaStateInputSchema = @"{""type"":""object"",""properties"":{},""required"":[]}";

        public const string MovePieceInputSchema = @"{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""move"",""place"",""remove""]},""piece_id"":{""type"":""string"",""description"":""move/remove 时必填""},""to"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":2,""maxItems"":2,""description"":""目标格 [x, y]，必须在棋盘范围内""},""new_piece"":{""type"":""object"",""description"":""place 时必填：至少含 type/side；id 缺省自动生成；name 缺省按 type+side 自动补"",""properties"":{""id"":{""type"":""string""},""type"":{""type"":""string""},""side"":{""type"":""string""},""name"":{""type"":""string""},""position"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":2,""maxItems"":2}},""required"":[""type"",""side""]}},""required"":[""action""]}";

        public const string ApplyConfigPatchInputSchema = @"{""type"":""object"",""properties"":{""operations"":{""type"":""array"",""minItems"":1,""items"":{""type"":""object"",""properties"":{""target"":{""type"":""string"",""enum"":[""board"",""rules"",""ui_config"",""pieces_red"",""pieces_black"",""pieces_white"",""board_state""]},""patch"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""op"":{""type"":""string"",""enum"":[""add"",""remove"",""replace"",""copy"",""move""]},""path"":{""type"":""string""},""from"":{""type"":""string""},""value"":{}},""required"":[""op"",""path""]}}},""required"":[""target"",""patch""]}}},""required"":[""operations""]}";

        public const string SetObjectiveInputSchema = @"{""type"":""object"",""properties"":{""type"":{""type"":""string"",""description"":""目标类型，须存在于 objective_types.json（如 checkmate/capture_all/survive_turns/reach_position）""},""params"":{""type"":""object"",""description"":""类型参数，如 {\""piece_type\"":\""general\""} 或 {\""turns\"":10}"",""default"":{}}},""required"":[""type""]}";

        public const string UndoLastChangeInputSchema = @"{""type"":""object"",""properties"":{},""required"":[]}";

        public const string CommitPreviewInputSchema = @"{""type"":""object"",""properties"":{""summary"":{""type"":""string"",""description"":""给玩家的一段话：改了什么、达到什么效果""},""partial_note"":{""type"":""string"",""description"":""可选：部分指令无法实现时的说明（触发 partial 预览）"",""default"":""""}},""required"":[""summary""]}";

        // ── description 常量（§4.2 逐字） ──

        public const string GetBoardStateDescription = "读取当前棋盘状态：棋子列表（id/type/side/name/position/is_alive）、当前回合、机制（mechanisms）、终局状态与最近 N 手棋谱。返回经过裁剪的 JSON，避免一次返回过大。";
        public const string GetConfigSliceDescription = "读取某个配置切片的当前内容。target 为顶层配置键（board/rules/ui_config/pieces_red/pieces_black/pieces_white），可选 path 取子树（JSON Pointer）。修改前必须先读目标切片。";
        public const string GetKarmaStateDescription = "查询当前业力、识破概率、道境、技能修饰符与已解锁的工具能力。修改前可据此判断代价与风险。";
        public const string MovePieceDescription = "移动、放置或移除一枚棋子。这是改棋盘最常用的方式：无需手写补丁。action=move 改 position；action=place 在空位放新棋子（需 new_piece）；action=remove 将 is_alive 置 false（不从数组移除）。不做走法合法性校验（作弊即越权），但做边界与占位校验。";
        public const string ApplyConfigPatchDescription = "对配置切片应用 RFC 6902 JSON Patch。可一次提交多个 (target, patch) 操作，原子生效。路径必须在白名单内；越权路径会被拒绝并给出合法前缀列表。改棋子规则用 pieces_*，改机制用 board_state 的 /mechanisms，改棋盘尺寸用 board，改界面用 ui_config，改 AI 性格/阵营规则用 rules。";
        public const string SetObjectiveDescription = "设置当前关卡目标（胜利条件）。type 必须来自关卡目标类型表；参数随类型变化。这是高危操作，会改变胜负判定。";
        public const string UndoLastChangeDescription = "回退上一步你自己造成的变更（仅预览期有效，玩家确认后不可用）。返回被回退的内容摘要。";
        public const string CommitPreviewDescription = "结束本次改规：冻结当前沙箱全部变更为最终预览，并用一段话向玩家说明你改了什么、为什么。调用后不再接受工具调用。若没有任何实质变更，就不要调用本工具，直接文字回复玩家。";

        // ── 注册表（§4.2 工具 1..8 固定序——字节级稳定的根基） ──

        static readonly AgentToolDef[] Registry =
        {
            new AgentToolDef("get_board_state", GetBoardStateDescription, GetBoardStateInputSchema, AgentPermission.L0, isWrite: false),
            new AgentToolDef("get_config_slice", GetConfigSliceDescription, GetConfigSliceInputSchema, AgentPermission.L0, isWrite: false),
            new AgentToolDef("get_karma_state", GetKarmaStateDescription, GetKarmaStateInputSchema, AgentPermission.L0, isWrite: false),
            new AgentToolDef("move_piece", MovePieceDescription, MovePieceInputSchema, AgentPermission.L1, isWrite: true),
            new AgentToolDef("apply_config_patch", ApplyConfigPatchDescription, ApplyConfigPatchInputSchema, AgentPermission.L2, isWrite: true),
            new AgentToolDef("set_objective", SetObjectiveDescription, SetObjectiveInputSchema, AgentPermission.L2, isWrite: true),
            new AgentToolDef("undo_last_change", UndoLastChangeDescription, UndoLastChangeInputSchema, AgentPermission.L1, isWrite: true),
            new AgentToolDef("commit_preview", CommitPreviewDescription, CommitPreviewInputSchema, AgentPermission.L1, isWrite: true),
        };

        /// <summary>全量注册表（固定序，勿枚举依赖外部可变集合）。</summary>
        public static IReadOnlyList<AgentToolDef> All => Registry;

        /// <summary>按名称查找（Ordinal）；找不到返回 null。</summary>
        public static AgentToolDef Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var t in Registry)
                if (string.Equals(t.Name, name, StringComparison.Ordinal)) return t;
            return null;
        }

        /// <summary>
        /// 按已解锁工具名集合过滤（net48 无 IReadOnlySet，用 IEnumerable + 内部 HashSet，行为等价）。
        /// 返回注册表固定序子集；未知名忽略；null/空集 ⇒ 空。
        /// </summary>
        public static IReadOnlyList<AgentToolDef> ForAllowedTools(IEnumerable<string> allowed)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (allowed != null)
                foreach (var name in allowed)
                    if (!string.IsNullOrEmpty(name)) wanted.Add(name);
            return Registry.Where(t => wanted.Contains(t.Name)).ToList();
        }

        // ── wire 层精简 description（E3-S2 体积优化） ─────────────────
        // 注册表 Description 仍是 §4.2 逐字文档真源（测试锁定）；实际发给模型的 tools 数组
        // 使用下列精简投影——语义保留、字数大幅下降。system 不再重复嵌入 schema，
        // 工具结构只经 tools 数组下发一次（避免 ~1,600 token 的重复前缀）。

        static readonly Dictionary<string, string> WireDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["get_board_state"] = "读棋盘：棋子/回合/机制/状态/近N手；include 按需取。",
            ["get_config_slice"] = "读配置切片：target=board/rules/ui_config/pieces_*；board_state 用 get_board_state；改棋子规则先读整片；path 须 / 开头。",
            ["get_karma_state"] = "查业力/识破/道境/已解锁工具。",
            ["move_piece"] = "移/放/移除棋子：action=move/place/remove；move/remove 需 piece_id；place 需 new_piece(type,side)；to=[x,y]。",
            ["apply_config_patch"] = "RFC6902 补丁，多 target 原子生效；路径须白名单。规则=pieces_*，机制=board_state /mechanisms，尺寸=board，界面=ui_config，AI=rules。",
            ["set_objective"] = "设关卡目标：type 须在目标表；params 随类型。",
            ["undo_last_change"] = "回退上一步变更（仅预览期）。",
            ["commit_preview"] = "冻结预览并总结；无实质变更则直接文字回复，不调用。",
        };

        /// <summary>wire 层描述（未收录则回退注册表原文）。</summary>
        public static string WireDescription(string name)
        {
            if (!string.IsNullOrEmpty(name) && WireDescriptions.TryGetValue(name, out var text))
                return text;
            return Find(name)?.Description ?? string.Empty;
        }

        /// <summary>递归剥离 parameters 内的 description（保留 type/enum/required/default/边界）。</summary>
        static JToken CompactSchema(JToken token)
        {
            if (token is JObject obj)
            {
                var copy = new JObject();
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, "description", StringComparison.Ordinal)) continue;
                    copy[prop.Name] = CompactSchema(prop.Value);
                }
                return copy;
            }
            if (token is JArray arr)
            {
                var copy = new JArray();
                foreach (var item in arr) copy.Add(CompactSchema(item));
                return copy;
            }
            return token.DeepClone();
        }

        /// <summary>
        /// Canonical 序列化：注册表固定序 + 按 name 去重（保留首个）+ 跳过 null。
        /// 与调用方插入序无关——同集合任意排列字节一致（前缀缓存前提 R1-R7）。
        /// wire 层使用精简 description + 无参数描述的紧凑 schema（体积优化，语义不变）。
        /// </summary>
        public static string SerializeTools(IEnumerable<AgentToolDef> tools)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (tools != null)
                foreach (var t in tools)
                    if (t != null && !string.IsNullOrEmpty(t.Name)) wanted.Add(t.Name);

            var sb = new StringBuilder();
            sb.Append('[');
            var first = true;
            foreach (var def in Registry)
            {
                if (!wanted.Contains(def.Name)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"type\":\"function\",\"function\":{\"name\":");
                sb.Append(JsonConvert.ToString(def.Name));
                sb.Append(",\"description\":");
                sb.Append(JsonConvert.ToString(WireDescription(def.Name)));
                sb.Append(",\"parameters\":");
                sb.Append(CompactSchema(def.Schema).ToString(Formatting.None));
                sb.Append("}}");
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
