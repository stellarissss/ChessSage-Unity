using System;
using System.Collections.Generic;
using System.Linq;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E1-S4：AgentTools / ToolRouter（8 工具 + 五步派发）。
    /// 期望值独立推导：铁律=读工具零突变、工具永不抛异常（错误以 {ok:false,errorCode,hint} 结构化回喂）、
    /// 特权写过白名单 + Schema 硬校验 + 沙箱原子事务。场景对齐 QA S1/S2/S5/S6/S9/S12 引擎侧。
    /// </summary>
    [TestFixture]
    public sealed class AgentToolsTests
    {
        static readonly string[] AllAllowed =
        {
            "get_board_state", "get_config_slice", "get_karma_state",
            "move_piece", "apply_config_patch", "set_objective",
            "undo_last_change", "commit_preview",
        };
        // 无 L2 的门控集合（t1a/t1b 均未解锁）。
        static readonly string[] NoL2 =
        {
            "get_board_state", "get_config_slice", "get_karma_state",
            "move_piece", "undo_last_change", "commit_preview",
        };

        static JObject SampleConfigs()
        {
            return JObject.Parse(@"{
              ""board_state"": {
                ""pieces"": [
                  {""id"": ""r_chariot_1"", ""type"": ""chariot"", ""name"": ""车"", ""side"": ""red"", ""position"": [0, 9], ""is_alive"": true},
                  {""id"": ""b_horse_1"", ""type"": ""horse"", ""name"": ""马"", ""side"": ""black"", ""position"": [1, 0], ""is_alive"": true},
                  {""id"": ""r_soldier_dead"", ""type"": ""soldier"", ""name"": ""兵"", ""side"": ""red"", ""position"": [3, 3], ""is_alive"": false}
                ],
                ""current_turn"": ""red"",
                ""move_history"": [
                  {""piece_id"": ""r_chariot_1"", ""from"": [0,9], ""to"": [1,9], ""kind"": ""move""},
                  {""piece_id"": ""b_horse_1"", ""from"": [1,0], ""to"": [2,2], ""kind"": ""move""}
                ],
                ""game_status"": {""state"": ""playing""},
                ""mechanisms"": {""skip_turns"": [], ""ai_control"": [{""side"": ""red"", ""turns"": 1}]}
              },
              ""board"": {""width"": 9, ""height"": 10},
              ""rules"": {""ai_difficulty"": {""personality"": ""calm""}},
              ""ui_config"": {""theme"": ""dark""},
              ""pieces_red"": {""pieces"": {""chariot"": {""label"": ""车"", ""moves"": []}}}
            }");
        }

        static AgentKarmaState KarmaState() => new AgentKarmaState
        {
            Karma = 50,
            KarmaMax = 120,
            SingleMax = 120,
            Detection = 12.5,
            Realm = "hell",
            SkillModifiers = new JObject { ["efficiency_fraud"] = true },
            AllowedTools = AllAllowed,
        };

        static AgentToolContext Ctx(IReadOnlyList<string> allowed, Func<string, JObject, (bool, string)> validator = null)
            => new AgentToolContext
            {
                AllowedTools = allowed,
                KarmaStateProvider = KarmaState,
                ConfigValidator = validator ?? ((name, cfg) => (true, (string)null)),
                ObjectiveTypesProvider = () => new[] { "checkmate", "capture_all", "survive_turns", "reach_position" },
            };

        static ToolRouter NewRouter(IReadOnlyList<string> allowed = null, JObject configs = null,
            Func<string, JObject, (bool, string)> validator = null)
            => new ToolRouter(new SandboxTransaction(configs ?? SampleConfigs()), Ctx(allowed ?? AllAllowed, validator));

        static LlmToolCall Call(string name, string args) => new LlmToolCall { Id = "c1", Name = name, ArgumentsJson = args };

        static JObject DispatchJson(ToolRouter router, string name, string args)
        {
            var result = router.Dispatch(Call(name, args));
            Assert.IsNotNull(result, "Dispatch 必须返回结构化结果");
            return result;
        }

        static (bool ok, string errorCode, string hint) Fail(ToolRouter router, string name, string args)
        {
            var r = DispatchJson(router, name, args);
            Assert.IsFalse(r.Value<bool>("ok"), $"{name} 应当失败：{r.ToString()}");
            return (false, r.Value<string>("errorCode"), r.Value<string>("hint"));
        }

        // ── 五步派发：存在性 / 权限 / Schema / 限额 ──────────────────

        [Test]
        public void UnknownTool_StructuredError_NeverThrows()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "conquer_world", "{}");

            Assert.IsFalse(r.Value<bool>("ok"), "AGT-04 幻觉工具名");
            Assert.AreEqual("UNKNOWN_TOOL", r.Value<string>("errorCode"));
            StringAssert.Contains("get_board_state", r.Value<string>("hint"), "hint 携带合法工具列表");
            Assert.AreEqual(0, router.Sandbox.Writes, "未知工具零突变");
        }

        [Test]
        public void CorruptArgumentsJson_StructuredError()
        {
            var router = NewRouter();
            var (ok, code, _) = Fail(router, "move_piece", "{不是合法JSON");
            Assert.AreEqual("BAD_ARGS_JSON", code, "AGT-06 参数损坏结构化回喂");
        }

        [Test]
        public void SchemaInvalid_StructuredError()
        {
            var router = NewRouter();
            // move_piece 缺必填 action。
            var (ok, code, hint) = Fail(router, "move_piece", "{\"piece_id\":\"x\"}");
            Assert.AreEqual("SCHEMA_INVALID", code);
            Assert.IsFalse(string.IsNullOrEmpty(hint));
        }

        [Test]
        public void L2Locked_Rejected_WithSkillTreeHint()
        {
            var router = NewRouter(NoL2);
            var (ok, code, hint) = Fail(router, "apply_config_patch",
                "{\"operations\":[{\"target\":\"board\",\"patch\":[{\"op\":\"replace\",\"path\":\"/width\",\"value\":8}]}]}");

            Assert.AreEqual("TOOL_LOCKED", code, "AGT-05 越权拒绝（cheat_mastery_t1a 未解锁）");
            StringAssert.Contains("解锁", hint);
            Assert.AreEqual(0, router.Sandbox.Writes, "越权写零突变");
        }

        [Test]
        public void WriteCallsLimit_RejectsBeyond24()
        {
            var router = NewRouter();
            for (int i = 0; i < AgentLimits.MaxWriteCallsPerSubmit; i++)
            {
                var r = DispatchJson(router, "move_piece",
                    $"{{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[0,{8 - (i % 2)}]}}");
                // 前 24 次允许（含失败的业务校验——限额按调用计）。
                Assert.IsTrue(router.WriteCalls <= AgentLimits.MaxWriteCallsPerSubmit);
            }
            var over = DispatchJson(router, "move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[1,8]}");
            Assert.IsFalse(over.Value<bool>("ok"), "第 25 次写调用超限额");
            Assert.AreEqual("WRITE_LIMIT_EXCEEDED", over.Value<string>("errorCode"));
        }

        // ── L0 读工具：零突变 ────────────────────────────────────────

        [Test]
        public void ReadTools_ZeroMutation()
        {
            var router = NewRouter();
            var before = router.Sandbox.Configs.ToString(Newtonsoft.Json.Formatting.None);

            DispatchJson(router, "get_board_state", "{}");
            DispatchJson(router, "get_config_slice", "{\"target\":\"rules\"}");
            DispatchJson(router, "get_karma_state", "{}");

            Assert.AreEqual(before, router.Sandbox.Configs.ToString(Newtonsoft.Json.Formatting.None), "读工具零突变");
            Assert.AreEqual(0, router.Sandbox.Writes);
            Assert.IsFalse(router.Sandbox.HasChanges);
        }

        [Test]
        public void GetBoardState_IncludeSideRecentMoves()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "get_board_state",
                "{\"include\":[\"pieces\",\"recent_moves\"],\"side\":\"black\",\"recent_moves\":1}");

            Assert.IsTrue(r.Value<bool>("ok"));
            var state = (JObject)r["board_state"];
            Assert.IsFalse(state.ContainsKey("turn"), "未请求的部分不返回");
            var pieces = (JArray)state["pieces"];
            Assert.AreEqual(1, pieces.Count, "side=black 过滤");
            Assert.AreEqual("b_horse_1", pieces[0].Value<string>("id"));
            var moves = (JArray)state["recent_moves"];
            Assert.AreEqual(1, moves.Count, "recent_moves=1 截尾");
            Assert.AreEqual("b_horse_1", moves[0].Value<string>("piece_id"));
        }

        [Test]
        public void GetBoardState_TruncatesWhenTooManyPieces()
        {
            var configs = SampleConfigs();
            var pieces = (JArray)configs["board_state"]["pieces"];
            for (int i = 0; i < 70; i++)
                pieces.Add(new JObject
                {
                    ["id"] = $"x{i}", ["type"] = "soldier", ["side"] = "red",
                    ["position"] = new JArray(i % 9, 5), ["is_alive"] = true,
                });
            var router = NewRouter(configs: configs);

            var r = DispatchJson(router, "get_board_state", "{}");
            Assert.IsTrue(r.Value<bool>("truncated"), "分页保护：>64 活子截断");
            Assert.LessOrEqual(((JArray)r["board_state"]["pieces"]).Count, 64);
        }

        [Test]
        public void BoardGeometry_ReadsNestedGeometry_AndEnforcesBounds()
        {
            var configs = SampleConfigs();
            configs["board"] = JObject.Parse("{\"geometry\":{\"width\":9,\"height\":10}}");
            var router = NewRouter(configs: configs);

            Assert.AreEqual(9, BoardGeometry.Width((JObject)configs["board"]));
            Assert.AreEqual(10, BoardGeometry.Height((JObject)configs["board"]));

            var board = DispatchJson(router, "get_board_state", "{\"include\":[\"board\"]}");
            Assert.AreEqual(9, board["board_state"]["board"].Value<int>("width"));
            Assert.AreEqual(10, board["board_state"]["board"].Value<int>("height"));

            // 真实 board.json 的 geometry 布局下，越界必须被拦截（修复前 hasBounds=false 静默放行）。
            var (ok, code, _) = Fail(router, "move_piece",
                "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[10,9]}");
            Assert.IsFalse(ok);
            Assert.AreEqual("OUT_OF_BOUNDS", code);
        }

        [Test]
        public void GetConfigSlice_PathSubtreeAndDepth()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "get_config_slice", "{\"target\":\"rules\",\"path\":\"/ai_difficulty/personality\"}");
            Assert.IsTrue(r.Value<bool>("ok"));
            Assert.AreEqual("calm", r["value"].Value<string>());

            var whole = DispatchJson(router, "get_config_slice", "{\"target\":\"rules\"}");
            Assert.IsTrue(whole["value"] is JObject);
        }

        [Test]
        public void GetConfigSlice_MissingTargetAndPath()
        {
            var router = NewRouter();
            // board_state 不在 get_config_slice 的 inputSchema enum——Schema 硬校验先行拦截（E0 合同）。
            var r = DispatchJson(router, "get_config_slice", "{\"target\":\"board_state\"}");
            Assert.IsFalse(r.Value<bool>("ok"));
            Assert.AreEqual("SCHEMA_INVALID", r.Value<string>("errorCode"));

            var (ok2, code2, hint2) = Fail(router, "get_config_slice", "{\"target\":\"rules\",\"path\":\"/no/such\"}");
            Assert.AreEqual("PATH_NOT_FOUND", code2);
            StringAssert.Contains("顶层键", hint2, "PATH_NOT_FOUND 必须给出可操作的结构提示");

            // E3-S2 自愈：读失败附整片 fallback，模型下一轮可直接写补丁（省一轮）。
            var fallback = DispatchJson(router, "get_config_slice", "{\"target\":\"rules\",\"path\":\"/no/such\"}");
            Assert.IsTrue(fallback["fallback_value"] is JObject);
            Assert.IsTrue(((JObject)fallback["fallback_value"]).ContainsKey("ai_difficulty"));
        }

        [Test]
        public void GetConfigSlice_NonPointerPath_ReturnsActionableBadPointer()
        {
            var router = NewRouter();
            // E3-S2 实测：模型常写 "horse" / "pieces.horse"；必须回喂「以 / 开头」的可操作 hint。
            var (ok, code, hint) = Fail(router, "get_config_slice", "{\"target\":\"pieces_red\",\"path\":\"horse\"}");
            Assert.IsFalse(ok);
            Assert.AreEqual("BAD_POINTER", code);
            StringAssert.Contains("/pieces/horse", hint);
            Assert.AreEqual(0, router.Sandbox.Writes, "失败读不得产生写入");
        }

        [Test]
        public void GetKarmaState_FromProvider()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "get_karma_state", "{}");
            Assert.IsTrue(r.Value<bool>("ok"));
            Assert.AreEqual(50, r["karma"].Value<int>("current"));
            Assert.AreEqual(120, r["karma"].Value<int>("single_max"));
            Assert.AreEqual(12.5, r.Value<double>("detection"));
            Assert.AreEqual("hell", r.Value<string>("realm"));
            StringAssert.Contains("apply_config_patch", r["allowed_tools"].ToString());
        }

        // ── L1 写工具：move_piece ────────────────────────────────────

        [Test]
        public void MovePiece_Move_ChangesPositionAtomically()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "move_piece",
                "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}");

            Assert.IsTrue(r.Value<bool>("ok"), r.ToString());
            Assert.AreEqual("/pieces/0/position", r.Value<string>("changed"));
            Assert.AreEqual(1, router.Sandbox.Writes);
            var piece = (JObject)router.Sandbox.Configs["board_state"]["pieces"][0];
            Assert.AreEqual(4, (int)piece["position"][0]);
            Assert.AreEqual(0, (int)router.Sandbox.Snapshot["board_state"]["pieces"][0]["position"][0], "快照不动");
        }

        [Test]
        public void MovePiece_OccupiedTarget_Rejected()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "move_piece",
                "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[1,0]}");
            Assert.IsFalse(r.Value<bool>("ok"));
            Assert.AreEqual("TARGET_OCCUPIED", r.Value<string>("errorCode"));
            StringAssert.Contains("b_horse_1", r.Value<string>("error"), "error 告知占据者（可操作：先 remove 它）");
            Assert.AreEqual(0, router.Sandbox.Writes);
        }

        [Test]
        public void MovePiece_OutOfBounds_Rejected_WithRangeHint()
        {
            var router = NewRouter();
            var (ok, code, hint) = Fail(router, "move_piece",
                "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[9,3]}");
            Assert.AreEqual("OUT_OF_BOUNDS", code);
            StringAssert.Contains("x∈[0,8]", hint, "hint 携带合法范围");
        }

        [Test]
        public void MovePiece_UnknownPiece_Rejected()
        {
            var router = NewRouter();
            var (ok, code, _) = Fail(router, "move_piece",
                "{\"action\":\"move\",\"piece_id\":\"ghost\",\"to\":[0,0]}");
            Assert.AreEqual("PIECE_NOT_FOUND", code);
        }

        [Test]
        public void MovePiece_Place_AutoIdAndCanonicalName()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "move_piece",
                "{\"action\":\"place\",\"to\":[4,4],\"new_piece\":{\"type\":\"cannon\",\"side\":\"red\"}}");

            Assert.IsTrue(r.Value<bool>("ok"), r.ToString());
            var pieces = (JArray)router.Sandbox.Configs["board_state"]["pieces"];
            var added = (JObject)pieces[pieces.Count - 1];
            Assert.AreEqual("cannon", added.Value<string>("type"));
            Assert.AreEqual("red", added.Value<string>("side"));
            Assert.AreEqual("cannon", added.Value<string>("name"), "name 缺省按纠正表/类型补");
            Assert.IsFalse(string.IsNullOrEmpty(added.Value<string>("id")), "id 自动生成");
        }

        [Test]
        public void MovePiece_Remove_SetsIsAliveFalse()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "move_piece", "{\"action\":\"remove\",\"piece_id\":\"b_horse_1\"}");
            Assert.IsTrue(r.Value<bool>("ok"));
            Assert.IsFalse(router.Sandbox.Configs["board_state"]["pieces"][1].Value<bool>("is_alive"));
            Assert.IsTrue(router.Sandbox.Snapshot["board_state"]["pieces"][1].Value<bool>("is_alive"), "快照不动");
        }

        [Test]
        public void MovePiece_MissingPieceId_Rejected()
        {
            var router = NewRouter();
            var (ok, code, _) = Fail(router, "move_piece", "{\"action\":\"move\",\"to\":[0,0]}");
            Assert.AreEqual("PIECE_ID_REQUIRED", code);
        }

        // ── L2 写工具：apply_config_patch ────────────────────────────

        [Test]
        public void ApplyConfigPatch_AppliesMultipleTargets()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "apply_config_patch",
                "{\"operations\":[" +
                "{\"target\":\"board\",\"patch\":[{\"op\":\"replace\",\"path\":\"/width\",\"value\":8}]}," +
                "{\"target\":\"rules\",\"patch\":[{\"op\":\"replace\",\"path\":\"/ai_difficulty/personality\",\"value\":\"aggressive\"}]}]}");

            Assert.IsTrue(r.Value<bool>("ok"), r.ToString());
            Assert.AreEqual(2, r.Value<int>("applied"));
            Assert.AreEqual(8, (int)router.Sandbox.Configs["board"]["width"]);
            Assert.AreEqual("aggressive", router.Sandbox.Configs["rules"]["ai_difficulty"].Value<string>("personality"));
        }

        [Test]
        public void ApplyConfigPatch_OutOfWhitelist_RejectedWithPrefixList()
        {
            var router = NewRouter();
            var (ok, code, hint) = Fail(router, "apply_config_patch",
                "{\"operations\":[{\"target\":\"board_state\",\"patch\":[{\"op\":\"replace\",\"path\":\"/move_history\",\"value\":[]}]}]}");

            Assert.AreEqual("PATH_REJECTED", code);
            StringAssert.Contains("/mechanisms/**", hint, "hint 携带合法前缀列表");
            Assert.AreEqual(0, router.Sandbox.Writes, "越权零突变");
        }

        [Test]
        public void ApplyConfigPatch_Atomic_AllOrNothing()
        {
            var router = NewRouter();
            var before = router.Sandbox.Configs.ToString(Newtonsoft.Json.Formatting.None);

            // 第一个 op 合法、第二个越权 ⇒ 整体拒绝，沙箱零变化。
            Fail(router, "apply_config_patch",
                "{\"operations\":[" +
                "{\"target\":\"board\",\"patch\":[{\"op\":\"replace\",\"path\":\"/width\",\"value\":8}]}," +
                "{\"target\":\"board_state\",\"patch\":[{\"op\":\"replace\",\"path\":\"/move_history\",\"value\":[]}]}]}");

            Assert.AreEqual(before, router.Sandbox.Configs.ToString(Newtonsoft.Json.Formatting.None), "原子事务：不留半套补丁");
            Assert.AreEqual(0, router.Sandbox.Writes);
        }

        [Test]
        public void ApplyConfigPatch_OpsLimitExceeded()
        {
            var router = NewRouter();
            var ops = new JArray();
            for (int i = 0; i < AgentLimits.MaxOpsPerCall + 1; i++)
                ops.Add(new JObject { ["op"] = "replace", ["path"] = "/width", ["value"] = 8 });
            Fail(router, "apply_config_patch", new JObject
            {
                ["operations"] = new JArray { new JObject { ["target"] = "board", ["patch"] = ops } },
            }.ToString());
        }

        [Test]
        public void ApplyConfigPatch_ConfigValidatorFailure_Rejected()
        {
            var router = NewRouter(validator: (name, cfg) =>
                (false, $"棋盘宽度 99 超出边界"));
            var (ok, code, hint) = Fail(router, "apply_config_patch",
                "{\"operations\":[{\"target\":\"board\",\"patch\":[{\"op\":\"replace\",\"path\":\"/width\",\"value\":99}]}]}");

            Assert.AreEqual("CONFIG_INVALID", code);
            StringAssert.Contains("超出边界", hint, "hint/error 携带可操作的校验失败原因");
        }

        [Test]
        public void ApplyConfigPatch_PiecesValidationFailure_HintCarriesMoveRuleShape()
        {
            // E3-S2 实测：模型不知道走法项必填 kind/land/sym，反复 CONFIG_INVALID。
            var router = NewRouter(validator: (name, cfg) => (false, "move.sym 不在枚举"));
            var (ok, code, hint) = Fail(router, "apply_config_patch",
                "{\"operations\":[{\"target\":\"pieces_red\",\"patch\":[{\"op\":\"replace\",\"path\":\"/pieces/chariot/label\",\"value\":\"车\"}]}]}");
            Assert.IsFalse(ok);
            Assert.AreEqual("CONFIG_INVALID", code);
            StringAssert.Contains("kind(jump|ray)", hint);
            StringAssert.Contains("sym(none|rotate4|rotate4_mirror|mirror_x)", hint);
        }

        [Test]
        public void ApplyConfigPatch_InvalidPatchSyntax_Rejected()
        {
            var router = NewRouter();
            Fail(router, "apply_config_patch",
                "{\"operations\":[{\"target\":\"board\",\"patch\":[{\"op\":\"frobnicate\",\"path\":\"/width\"}]}]}");
        }

        // ── L2 写工具：set_objective ─────────────────────────────────

        [Test]
        public void SetObjective_UnknownType_RejectedWithHint()
        {
            var router = NewRouter();
            var (ok, code, hint) = Fail(router, "set_objective", "{\"type\":\"conquer_world\"}");
            Assert.AreEqual("UNKNOWN_OBJECTIVE_TYPE", code);
            StringAssert.Contains("checkmate", hint);
        }

        [Test]
        public void SetObjective_WritesSandboxObjectiveKey()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "set_objective", "{\"type\":\"capture_all\",\"params\":{\"piece_type\":\"general\"}}");

            Assert.IsTrue(r.Value<bool>("ok"), r.ToString());
            Assert.AreEqual("capture_all", router.Sandbox.Configs["objective"].Value<string>("type"));
            Assert.AreEqual(1, router.Sandbox.Writes);
        }

        // ── L1 元工具：undo / commit ─────────────────────────────────

        [Test]
        public void UndoLastChange_Empty_ReturnsOkFalse()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "undo_last_change", "{}");
            Assert.IsFalse(r.Value<bool>("ok"));
            Assert.AreEqual("NO_UNDO", r.Value<string>("errorCode"));
        }

        [Test]
        public void UndoLastChange_RollsBackLatest()
        {
            var router = NewRouter();
            DispatchJson(router, "move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[4,8]}");
            DispatchJson(router, "move_piece", "{\"action\":\"move\",\"piece_id\":\"b_horse_1\",\"to\":[3,3]}");

            var r = DispatchJson(router, "undo_last_change", "{}");
            Assert.IsTrue(r.Value<bool>("ok"), r.ToString());
            Assert.AreEqual(1, r.Value<int>("remaining_changes"));
            Assert.AreEqual(4, (int)router.Sandbox.Configs["board_state"]["pieces"][0]["position"][0], "第一次的变更保留");
            Assert.AreEqual(1, (int)router.Sandbox.Configs["board_state"]["pieces"][1]["position"][0], "第二次已回退");
        }

        [Test]
        public void CommitPreview_SetsFlagAndSummary()
        {
            var router = NewRouter();
            var r = DispatchJson(router, "commit_preview", "{\"summary\":\"已移子\",\"partial_note\":\"颜色没改\"}");

            Assert.IsTrue(r.Value<bool>("ok"));
            Assert.IsTrue(router.CommitRequested);
            Assert.AreEqual("已移子", router.CommitSummary);
            Assert.AreEqual("颜色没改", router.CommitPartialNote);
        }

        // ── 铁律：永不抛异常 ─────────────────────────────────────────

        [Test]
        public void Dispatch_NeverThrows_OnGarbage()
        {
            var router = NewRouter();
            Assert.DoesNotThrow(() => router.Dispatch(Call(null, null)));
            Assert.DoesNotThrow(() => router.Dispatch(Call("", "")));
            Assert.DoesNotThrow(() => router.Dispatch(Call("move_piece", "[[[nonsense")));
            Assert.DoesNotThrow(() => router.Dispatch(Call("get_config_slice", "{\"target\":123}")));
            Assert.DoesNotThrow(() => router.Dispatch(Call("apply_config_patch", "{\"operations\":\"not-array\"}")));
        }
    }
}
