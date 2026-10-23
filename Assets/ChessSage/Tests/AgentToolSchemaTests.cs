using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ChessSage.Core.Agent;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E0-S3：工具 Schema 契约（§4.2 逐字）+ 路径白名单 + 限额 + canonical 序列化。
    /// 黄金文本独立转录自工程文档 §4.2（缩进格式与实现的紧凑常量刻意不同，防复制对称）。
    /// </summary>
    [TestFixture]
    public sealed class AgentToolSchemaTests
    {
        // ───────────────────────────────────────────────
        // §4.2 逐字合同：description 原文
        // ───────────────────────────────────────────────

        static readonly Dictionary<string, string> VerbatimDescriptions = new Dictionary<string, string>
        {
            ["get_board_state"] = "读取当前棋盘状态：棋子列表（id/type/side/name/position/is_alive）、当前回合、机制（mechanisms）、终局状态与最近 N 手棋谱。返回经过裁剪的 JSON，避免一次返回过大。",
            ["get_config_slice"] = "读取某个配置切片的当前内容。target 为顶层配置键（board/rules/ui_config/pieces_red/pieces_black/pieces_white），可选 path 取子树（JSON Pointer）。修改前必须先读目标切片。",
            ["get_karma_state"] = "查询当前业力、识破概率、道境、技能修饰符与已解锁的工具能力。修改前可据此判断代价与风险。",
            ["move_piece"] = "移动、放置或移除一枚棋子。这是改棋盘最常用的方式：无需手写补丁。action=move 改 position；action=place 在空位放新棋子（需 new_piece）；action=remove 将 is_alive 置 false（不从数组移除）。不做走法合法性校验（作弊即越权），但做边界与占位校验。",
            ["apply_config_patch"] = "对配置切片应用 RFC 6902 JSON Patch。可一次提交多个 (target, patch) 操作，原子生效。路径必须在白名单内；越权路径会被拒绝并给出合法前缀列表。改棋子规则用 pieces_*，改机制用 board_state 的 /mechanisms，改棋盘尺寸用 board，改界面用 ui_config，改 AI 性格/阵营规则用 rules。",
            ["set_objective"] = "设置当前关卡目标（胜利条件）。type 必须来自关卡目标类型表；参数随类型变化。这是高危操作，会改变胜负判定。",
            ["undo_last_change"] = "回退上一步你自己造成的变更（仅预览期有效，玩家确认后不可用）。返回被回退的内容摘要。",
            ["commit_preview"] = "结束本次改规：冻结当前沙箱全部变更为最终预览，并用一段话向玩家说明你改了什么、为什么。调用后不再接受工具调用。若没有任何实质变更，就不要调用本工具，直接文字回复玩家。",
        };

        [TestCase("get_board_state")]
        [TestCase("get_config_slice")]
        [TestCase("get_karma_state")]
        [TestCase("move_piece")]
        [TestCase("apply_config_patch")]
        [TestCase("set_objective")]
        [TestCase("undo_last_change")]
        [TestCase("commit_preview")]
        public void Description_MatchesVerbatimContract(string toolName)
        {
            var def = AgentToolSchemas.Find(toolName);
            Assert.IsNotNull(def, $"工具 {toolName} 应在注册表中");
            Assert.AreEqual(VerbatimDescriptions[toolName], def.Description, $"{toolName} 的 description 必须与 §4.2 逐字一致");
        }

        // ───────────────────────────────────────────────
        // §4.2 逐字合同：inputSchema 黄金 JSON（DeepEquals 结构等价）
        // ───────────────────────────────────────────────

        [Test]
        public void InputSchema_GetBoardState_MatchesGolden()
        {
            var golden = JObject.Parse(@"{
                'type': 'object',
                'properties': {
                    'include': {
                        'type': 'array',
                        'items': { 'type': 'string', 'enum': ['pieces', 'turn', 'mechanisms', 'game_status', 'recent_moves', 'board'] },
                        'default': ['pieces', 'turn', 'mechanisms'],
                        'description': '需要的部分，按需索取以节省上下文'
                    },
                    'side': { 'type': 'string', 'enum': ['red', 'black', 'white', 'all'], 'default': 'all' },
                    'recent_moves': { 'type': 'integer', 'minimum': 0, 'maximum': 20, 'default': 5 }
                },
                'required': []
            }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("get_board_state").Schema),
                "get_board_state 的 inputSchema 必须与 §4.2 黄金一致");
        }

        [Test]
        public void InputSchema_GetConfigSlice_MatchesGolden()
        {
            var golden = JObject.Parse(@"{
                'type': 'object',
                'properties': {
                    'target': { 'type': 'string', 'enum': ['board', 'rules', 'ui_config', 'pieces_red', 'pieces_black', 'pieces_white'] },
                    'path': { 'type': 'string', 'description': '可选 JSON Pointer，如 /ai_difficulty/personality；缺省返回整个切片', 'default': '' },
                    'max_depth': { 'type': 'integer', 'minimum': 1, 'maximum': 12, 'default': 8 }
                },
                'required': ['target']
            }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("get_config_slice").Schema));
        }

        [Test]
        public void InputSchema_GetKarmaState_MatchesGolden()
        {
            var golden = JObject.Parse(@"{ 'type': 'object', 'properties': {}, 'required': [] }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("get_karma_state").Schema));
        }

        [Test]
        public void InputSchema_MovePiece_MatchesGolden()
        {
            var golden = JObject.Parse(@"{
                'type': 'object',
                'properties': {
                    'action': { 'type': 'string', 'enum': ['move', 'place', 'remove'] },
                    'piece_id': { 'type': 'string', 'description': 'move/remove 时必填' },
                    'to': {
                        'type': 'array', 'items': { 'type': 'integer' }, 'minItems': 2, 'maxItems': 2,
                        'description': '目标格 [x, y]，必须在棋盘范围内'
                    },
                    'new_piece': {
                        'type': 'object',
                        'description': 'place 时必填：至少含 type/side；id 缺省自动生成；name 缺省按 type+side 自动补',
                        'properties': {
                            'id': { 'type': 'string' },
                            'type': { 'type': 'string' },
                            'side': { 'type': 'string' },
                            'name': { 'type': 'string' },
                            'position': { 'type': 'array', 'items': { 'type': 'integer' }, 'minItems': 2, 'maxItems': 2 }
                        },
                        'required': ['type', 'side']
                    }
                },
                'required': ['action']
            }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("move_piece").Schema));
        }

        [Test]
        public void InputSchema_ApplyConfigPatch_MatchesGolden()
        {
            var golden = JObject.Parse(@"{
                'type': 'object',
                'properties': {
                    'operations': {
                        'type': 'array',
                        'minItems': 1,
                        'items': {
                            'type': 'object',
                            'properties': {
                                'target': { 'type': 'string', 'enum': ['board', 'rules', 'ui_config', 'pieces_red', 'pieces_black', 'pieces_white', 'board_state'] },
                                'patch': {
                                    'type': 'array',
                                    'items': {
                                        'type': 'object',
                                        'properties': {
                                            'op': { 'type': 'string', 'enum': ['add', 'remove', 'replace', 'copy', 'move'] },
                                            'path': { 'type': 'string' },
                                            'from': { 'type': 'string' },
                                            'value': {}
                                        },
                                        'required': ['op', 'path']
                                    }
                                }
                            },
                            'required': ['target', 'patch']
                        }
                    }
                },
                'required': ['operations']
            }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("apply_config_patch").Schema));
        }

        [Test]
        public void InputSchema_SetObjective_MatchesGolden()
        {
            var golden = JObject.Parse(@"{
                'type': 'object',
                'properties': {
                    'type': { 'type': 'string', 'description': '目标类型，须存在于 objective_types.json（如 checkmate/capture_all/survive_turns/reach_position）' },
                    'params': {
                        'type': 'object',
                        'description': '类型参数，如 {\""piece_type\"":\""general\""} 或 {\""turns\"":10}',
                        'default': {}
                    }
                },
                'required': ['type']
            }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("set_objective").Schema));
        }

        [Test]
        public void InputSchema_UndoLastChange_MatchesGolden()
        {
            var golden = JObject.Parse(@"{ 'type': 'object', 'properties': {}, 'required': [] }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("undo_last_change").Schema));
        }

        [Test]
        public void InputSchema_CommitPreview_MatchesGolden()
        {
            var golden = JObject.Parse(@"{
                'type': 'object',
                'properties': {
                    'summary': { 'type': 'string', 'description': '给玩家的一段话：改了什么、达到什么效果' },
                    'partial_note': { 'type': 'string', 'description': '可选：部分指令无法实现时的说明（触发 partial 预览）', 'default': '' }
                },
                'required': ['summary']
            }".Replace('\'', '"'));
            Assert.IsTrue(JToken.DeepEquals(golden, AgentToolSchemas.Find("commit_preview").Schema));
        }

        // ───────────────────────────────────────────────
        // 注册表与权限分层
        // ───────────────────────────────────────────────

        [Test]
        public void Registry_HasEightTools_InDocOrder()
        {
            var expected = new[]
            {
                "get_board_state", "get_config_slice", "get_karma_state", "move_piece",
                "apply_config_patch", "set_objective", "undo_last_change", "commit_preview",
            };
            CollectionAssert.AreEqual(expected, AgentToolSchemas.All.Select(t => t.Name).ToArray(),
                "注册表必须按 §4.2 工具 1..8 固定序");
        }

        [TestCase("get_board_state", AgentPermission.L0)]
        [TestCase("get_config_slice", AgentPermission.L0)]
        [TestCase("get_karma_state", AgentPermission.L0)]
        [TestCase("move_piece", AgentPermission.L1)]
        [TestCase("undo_last_change", AgentPermission.L1)]
        [TestCase("commit_preview", AgentPermission.L1)]
        [TestCase("apply_config_patch", AgentPermission.L2)]
        [TestCase("set_objective", AgentPermission.L2)]
        public void Permission_Levels_MatchSpec(string toolName, AgentPermission expected)
        {
            Assert.AreEqual(expected, AgentToolSchemas.Find(toolName).Permission);
        }

        [TestCase("move_piece", true)]
        [TestCase("apply_config_patch", true)]
        [TestCase("set_objective", true)]
        [TestCase("undo_last_change", true)]
        [TestCase("commit_preview", true)]
        [TestCase("get_board_state", false)]
        [TestCase("get_config_slice", false)]
        [TestCase("get_karma_state", false)]
        [TestCase("no_such_tool", false)]
        public void IsWriteTool_MatchesSpec(string toolName, bool expected)
        {
            Assert.AreEqual(expected, AgentLimits.IsWriteTool(toolName));
        }

        // ───────────────────────────────────────────────
        // canonical 序列化：固定序 + 插入序无关（字节 SHA-256 一致）
        // ───────────────────────────────────────────────

        [Test]
        public void Serialize_IsInsertionOrderIndependent_SameSha256()
        {
            var forward = AgentToolSchemas.All.ToList();
            var shuffled = new List<AgentToolDef>(forward);
            shuffled.Reverse();
            var random = new List<AgentToolDef> { forward[3], forward[0], forward[7], forward[2], forward[5], forward[1], forward[6], forward[4] };

            var a = AgentToolSchemas.SerializeTools(forward);
            var b = AgentToolSchemas.SerializeTools(shuffled);
            var c = AgentToolSchemas.SerializeTools(random);

            Assert.AreEqual(a, b, "倒序插入必须字节一致");
            Assert.AreEqual(a, c, "乱序插入必须字节一致");
            Assert.AreEqual(Sha256(a), Sha256(b));
            Assert.AreEqual(Sha256(a), Sha256(c));
        }

        [Test]
        public void Serialize_DedupesByName_AndSkipsNull()
        {
            var withDup = new List<AgentToolDef>
            {
                AgentToolSchemas.Find("commit_preview"),
                AgentToolSchemas.Find("get_board_state"),
                AgentToolSchemas.Find("commit_preview"),  // 重复
                null,
            };
            var json = AgentToolSchemas.SerializeTools(withDup);
            var arr = JArray.Parse(json);
            Assert.AreEqual(2, arr.Count, "按 name 去重");
            Assert.AreEqual("get_board_state", arr[0]["function"]["name"].Value<string>(), "注册表固定序");
            Assert.AreEqual("commit_preview", arr[1]["function"]["name"].Value<string>());
        }

        [Test]
        public void Serialize_EmptySet_ProducesEmptyArray()
        {
            Assert.AreEqual("[]", AgentToolSchemas.SerializeTools(new List<AgentToolDef>()));
            Assert.AreEqual("[]", AgentToolSchemas.SerializeTools(null));
        }

        [Test]
        public void Serialize_WireShape_IsOpenAIFunctionCalling()
        {
            var json = AgentToolSchemas.SerializeTools(new[] { AgentToolSchemas.Find("get_karma_state") });
            Assert.AreEqual(
                "[{\"type\":\"function\",\"function\":{\"name\":\"get_karma_state\",\"description\":\"查业力/识破/道境/已解锁工具。\",\"parameters\":{\"type\":\"object\",\"properties\":{},\"required\":[]}}}]",
                json);
        }

        [Test]
        public void Serialize_UsesCompactWireDescription_AndStripsParameterDescriptions()
        {
            var json = AgentToolSchemas.SerializeTools(AgentToolSchemas.All);
            var arr = JArray.Parse(json);
            foreach (var item in arr)
            {
                var name = item["function"]["name"].Value<string>();
                Assert.AreEqual(AgentToolSchemas.WireDescription(name), item["function"]["description"].Value<string>(),
                    $"{name} wire description 应为精简投影");
                Assert.LessOrEqual(AgentToolSchemas.WireDescription(name).Length, 110,
                    $"{name} wire description 过长（体积优化回退）");
                Assert.IsFalse(item["function"]["parameters"].ToString(Newtonsoft.Json.Formatting.None)
                        .Contains("\"description\""),
                    $"{name} parameters 内不得再带 description（体积优化回退）");
            }
            // 注册表原文仍是 §4.2 文档真源（不受 wire 压缩影响）。
            Assert.AreEqual(VerbatimDescriptions["get_board_state"], AgentToolSchemas.Find("get_board_state").Description);
        }

        [Test]
        public void Serialize_FullRegistry_HasPinnedSha256()
        {
            // 回归指纹：任何 schema 文本/键序/注册表次序的意外变动都会打破它；
            // 有意的合同变更（设计签署）才允许连同黄金测试一并更新。
            var sha = Sha256(AgentToolSchemas.SerializeTools(AgentToolSchemas.All));
            Assert.AreEqual("8a1afe9f6c76ed9433929afbb6b1a6e6c74dd372199aa0b12bd5cb82640f0fe4", sha);
        }

        [Test]
        public void ForAllowedTools_FiltersInRegistryOrder()
        {
            var allowed = new[] { "commit_preview", "get_board_state", "no_such_tool", "move_piece" };
            var tools = AgentToolSchemas.ForAllowedTools(allowed);
            CollectionAssert.AreEqual(
                new[] { "get_board_state", "move_piece", "commit_preview" },
                tools.Select(t => t.Name).ToArray(),
                "按注册表固定序过滤；未知名忽略");
        }

        [Test]
        public void ForAllowedTools_Empty_ReturnsEmpty()
        {
            Assert.AreEqual(0, AgentToolSchemas.ForAllowedTools(new string[0]).Count);
            Assert.AreEqual(0, AgentToolSchemas.ForAllowedTools(null).Count);
        }

        [Test]
        public void ForAllowedTools_ThenSerialize_MatchesDirectSerialize()
        {
            var allowed = new[] { "get_config_slice", "commit_preview", "get_board_state" };
            var viaFilter = AgentToolSchemas.SerializeTools(AgentToolSchemas.ForAllowedTools(allowed));
            var direct = AgentToolSchemas.SerializeTools(new[]
            {
                AgentToolSchemas.Find("get_board_state"),
                AgentToolSchemas.Find("get_config_slice"),
                AgentToolSchemas.Find("commit_preview"),
            });
            Assert.AreEqual(direct, viaFilter);
        }

        // ───────────────────────────────────────────────
        // 路径白名单（apply_config_patch 工具 5）
        // ───────────────────────────────────────────────

        [TestCase("board_state", "/pieces/0/position")]
        [TestCase("board_state", "/pieces/12/is_alive")]
        [TestCase("board_state", "/pieces/3/type")]
        [TestCase("board_state", "/pieces/3/name")]
        [TestCase("board_state", "/mechanisms")]
        [TestCase("board_state", "/mechanisms/frozen_ai_rounds")]
        [TestCase("board_state", "/mechanisms/a/b/c")]
        [TestCase("board_state", "/game_status/state")]
        [TestCase("board_state", "/game_status/winner")]
        [TestCase("board_state", "/game_status/win_condition")]
        [TestCase("board_state", "/game_status/custom_rules_active")]
        [TestCase("board", "/width")]
        [TestCase("board", "/height")]
        [TestCase("board", "/geometry/width")]
        [TestCase("board", "/geometry/height")]
        [TestCase("rules", "/ai_difficulty")]
        [TestCase("rules", "/ai_difficulty/personality")]
        [TestCase("rules", "/ai_difficulty/skill/depth")]
        [TestCase("rules", "/side_overrides/red/aggression")]
        [TestCase("ui_config", "/theme/color")]
        [TestCase("ui_config", "/anything/deep/nested")]
        [TestCase("pieces_red", "/pieces")]
        [TestCase("pieces_red", "/pieces/0/moves")]
        [TestCase("pieces_black", "/pieces/x/y")]
        [TestCase("pieces_white", "/pieces/2")]
        public void Whitelist_AllowsContractedPaths(string target, string pointer)
        {
            Assert.IsTrue(AgentPathPolicy.IsPathAllowed(target, pointer), $"{target}:{pointer} 应在白名单内");
            Assert.IsNull(AgentPathPolicy.Reject(target, pointer));
        }

        [TestCase("board_state", "")]
        [TestCase("board_state", "/")]
        [TestCase("board", "")]
        [TestCase("ui_config", "")]
        [TestCase("ui_config", "/")]
        [TestCase("board_state", "/move_history")]
        [TestCase("board_state", "/move_history/0")]
        [TestCase("board_state", "/board")]
        [TestCase("board_state", "/current_turn")]
        [TestCase("board_state", "/pieces")]
        [TestCase("board_state", "/pieces/0")]
        [TestCase("board_state", "/pieces/0/position/x")]
        [TestCase("board_state", "/game_status/foo")]
        [TestCase("board_state", "/game_status/state/x")]
        [TestCase("board", "/width/x")]
        [TestCase("board", "/depth")]
        [TestCase("rules", "/pieces/0")]
        [TestCase("rules", "/ai_difficulty_x")]
        [TestCase("pieces_red", "/rules")]
        [TestCase("unknown_target", "/width")]
        [TestCase("", "/width")]
        public void Whitelist_RejectsContractedPaths(string target, string pointer)
        {
            Assert.IsFalse(AgentPathPolicy.IsPathAllowed(target, pointer), $"{target}:{pointer} 应被拒绝");
            Assert.IsNotNull(AgentPathPolicy.Reject(target, pointer));
        }

        [Test]
        public void Reject_MatchesGoldenErrorShape()
        {
            // §4.2 越权输出样例逐字：error 与 hint 两个独立字段。
            var r = AgentPathPolicy.Reject("board_state", "/move_history");
            StringAssert.Contains("路径 /board_state/move_history 不在白名单", r.Error);
            StringAssert.Contains(
                "board_state 允许: /pieces/*/{position,is_alive,type,name}, /mechanisms/**, /game_status/{state,winner,win_condition,custom_rules_active}",
                r.Hint);
        }

        [Test]
        public void Reject_UnknownTarget_ListsLegalTargets()
        {
            var r = AgentPathPolicy.Reject("boss_config", "/x");
            StringAssert.Contains("未知配置切片", r.Error);
            StringAssert.Contains("board/rules/ui_config/pieces_red/pieces_black/pieces_white/board_state", r.Hint);
        }

        [Test]
        public void AllowedPrefixesText_MatchesGolden()
        {
            Assert.AreEqual(
                "/pieces/*/{position,is_alive,type,name}, /mechanisms/**, /game_status/{state,winner,win_condition,custom_rules_active}",
                AgentPathPolicy.AllowedPrefixesText("board_state"));
            Assert.AreEqual("/width, /height, /geometry/width, /geometry/height",
                AgentPathPolicy.AllowedPrefixesText("board"));
            Assert.AreEqual("/ai_difficulty/**, /side_overrides/**", AgentPathPolicy.AllowedPrefixesText("rules"));
            Assert.AreEqual("/**", AgentPathPolicy.AllowedPrefixesText("ui_config"));
            Assert.AreEqual("/pieces/**", AgentPathPolicy.AllowedPrefixesText("pieces_red"));
            Assert.AreEqual("/pieces/**", AgentPathPolicy.AllowedPrefixesText("pieces_black"));
            Assert.AreEqual("/pieces/**", AgentPathPolicy.AllowedPrefixesText("pieces_white"));
        }

        // ───────────────────────────────────────────────
        // 限额（§4.2 全局拒绝规则 4）
        // ───────────────────────────────────────────────

        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(32, true)]
        [TestCase(33, false)]
        [TestCase(100, false)]
        public void OpCountLimit_Is32(int ops, bool expected)
        {
            Assert.AreEqual(expected, AgentLimits.IsOpCountWithinLimit(ops));
        }

        [TestCase(0, true)]
        [TestCase(24, true)]
        [TestCase(25, false)]
        public void WriteCallLimit_Is24(int calls, bool expected)
        {
            Assert.AreEqual(expected, AgentLimits.IsWriteCallsWithinLimit(calls));
        }

        [Test]
        public void Limits_RejectNegativeCounts()
        {
            Assert.IsFalse(AgentLimits.IsOpCountWithinLimit(-1));
            Assert.IsFalse(AgentLimits.IsWriteCallsWithinLimit(-1));
        }

        static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
