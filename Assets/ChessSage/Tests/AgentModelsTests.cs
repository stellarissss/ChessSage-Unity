using System.Collections.Generic;
using ChessSage.Core.Agent;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E1-S5：AgentModels（SandboxTransaction / AgentProgress / AgentResult / AgentTrajectoryEntry）。
    /// 期望值独立推导：沙箱 = 提交时快照深拷贝 + 逆补丁栈（LIFO）；modified_configs 只含差异切片
    /// 且值为全量（CheatDiffRenderer.Render 兼容契约——§5.7「仅差异」的正确解读）。
    /// </summary>
    [TestFixture]
    public sealed class AgentModelsTests
    {
        static JObject SampleConfigs()
        {
            return JObject.Parse(@"{
              ""board_state"": {
                ""pieces"": [
                  {""id"": ""r_chariot_1"", ""type"": ""chariot"", ""name"": ""车"", ""side"": ""red"", ""position"": [0, 9], ""is_alive"": true}
                ],
                ""current_turn"": ""red"",
                ""move_history"": [],
                ""game_status"": {""state"": ""playing""},
                ""mechanisms"": {""skip_turns"": []}
              },
              ""board"": {""width"": 9, ""height"": 10},
              ""rules"": {""ai_difficulty"": {""personality"": ""calm""}},
              ""ui_config"": {""theme"": ""dark""},
              ""pieces_red"": {""pieces"": {""chariot"": {""label"": ""车"", ""moves"": []}}}
            }");
        }

        // ── SandboxTransaction ──────────────────────────────────────

        [Test]
        public void Sandbox_Snapshot_IsDeepCopy_NotAlias()
        {
            var configs = SampleConfigs();
            var sandbox = new SandboxTransaction(configs);

            sandbox.Configs["board"]["width"] = 42;

            Assert.AreEqual(9, (int)configs["board"]["width"], "改沙箱不得污染调用方传入的配置（提交时快照）");
            Assert.AreEqual(9, (int)sandbox.Snapshot["board"]["width"], "快照保持提交时原值");
        }

        [Test]
        public void Sandbox_TryApplySlice_AtomicAndCounted()
        {
            var sandbox = new SandboxTransaction(SampleConfigs());

            var newBoard = (JObject)sandbox.Configs["board"].DeepClone();
            newBoard["width"] = 8;
            var applied = sandbox.TryApplySlice("board", newBoard, "move_piece", "改棋盘宽度");

            Assert.IsTrue(applied.Changed);
            Assert.AreEqual(8, (int)sandbox.Configs["board"]["width"]);
            Assert.AreEqual(1, sandbox.Writes, "实际变更计入 Writes");
            Assert.AreEqual(9, (int)sandbox.Snapshot["board"]["width"], "快照不动");
            Assert.AreEqual(1, sandbox.UndoDepth, "变更压栈");

            // 无变化的应用：不算 Writes、不压栈。
            var same = (JObject)sandbox.Configs["board"].DeepClone();
            var again = sandbox.TryApplySlice("board", same, "move_piece", "重复应用同值");
            Assert.IsFalse(again.Changed);
            Assert.AreEqual(1, sandbox.Writes);
            Assert.AreEqual(1, sandbox.UndoDepth);
        }

        [Test]
        public void Sandbox_Undo_RestoresPrevious_LIFO()
        {
            var sandbox = new SandboxTransaction(SampleConfigs());

            var b1 = (JObject)sandbox.Configs["board"].DeepClone();
            b1["width"] = 8;
            sandbox.TryApplySlice("board", b1, "apply_config_patch", "第一次");
            var b2 = (JObject)sandbox.Configs["board"].DeepClone();
            b2["height"] = 12;
            sandbox.TryApplySlice("board", b2, "apply_config_patch", "第二次");

            Assert.AreEqual(8, (int)sandbox.Configs["board"]["width"]);
            Assert.AreEqual(12, (int)sandbox.Configs["board"]["height"]);

            var undone = sandbox.Undo();
            Assert.IsNotNull(undone);
            Assert.AreEqual("第二次", undone.Description);
            Assert.AreEqual(10, (int)sandbox.Configs["board"]["height"], "逆补丁还原 height");
            Assert.AreEqual(8, (int)sandbox.Configs["board"]["width"], "第一次的变更保留");

            var undone2 = sandbox.Undo();
            Assert.AreEqual("第一次", undone2.Description);
            Assert.AreEqual(9, (int)sandbox.Configs["board"]["width"], "回到快照原值");

            Assert.IsNull(sandbox.Undo(), "弹空返回 null（undo_last_change 据此回 {ok:false}）");
        }

        [Test]
        public void Sandbox_Peek_DoesNotPop()
        {
            var sandbox = new SandboxTransaction(SampleConfigs());
            var b = (JObject)sandbox.Configs["board"].DeepClone();
            b["width"] = 8;
            sandbox.TryApplySlice("board", b, "apply_config_patch", "唯一变更");

            var peeked = sandbox.Peek();
            Assert.IsNotNull(peeked);
            Assert.AreEqual("唯一变更", peeked.Description);
            Assert.AreEqual(1, sandbox.UndoDepth, "Peek 不弹出");
        }

        [Test]
        public void Sandbox_BuildModifiedConfigs_OnlyChangedSlices_WithFullValues()
        {
            var sandbox = new SandboxTransaction(SampleConfigs());

            var b = (JObject)sandbox.Configs["board"].DeepClone();
            b["width"] = 8;
            sandbox.TryApplySlice("board", b, "apply_config_patch", "改宽度");
            var state = (JObject)sandbox.Configs["board_state"].DeepClone();
            ((JArray)state["pieces"])[0]["position"] = new JArray(1, 1);
            sandbox.TryApplySlice("board_state", state, "move_piece", "移子");

            var modified = sandbox.BuildModifiedConfigs();

            Assert.AreEqual(2, modified.Count, "只有变化的切片出现（rules/ui_config/pieces_red 不动则不出现）");
            Assert.IsTrue(modified.ContainsKey("board"));
            Assert.IsTrue(modified.ContainsKey("board_state"));
            Assert.AreEqual(8, (int)modified["board"]["width"]);
            // 值为全量切片（CheatDiffRenderer 期望的形状，不是 RFC6902 补丁数组）。
            Assert.IsTrue(modified["board_state"] is JObject);
            Assert.AreEqual(1, (int)modified["board_state"]["pieces"][0]["position"][0]);
            Assert.IsTrue(sandbox.HasChanges);
        }

        [Test]
        public void Sandbox_NoChanges_ModifiedEmpty()
        {
            var sandbox = new SandboxTransaction(SampleConfigs());
            Assert.IsFalse(sandbox.HasChanges);
            Assert.IsFalse(sandbox.BuildModifiedConfigs().HasValues);
        }

        [Test]
        public void Sandbox_Objective_PseudoSlice_CanWriteAndUndo()
        {
            var sandbox = new SandboxTransaction(SampleConfigs());
            var objective = new JObject { ["type"] = "capture_all", ["params"] = new JObject { ["piece_type"] = "general" } };

            var applied = sandbox.TryApplySlice("objective", objective, "set_objective", "设置关卡目标");
            Assert.IsTrue(applied.Changed);
            Assert.AreEqual("capture_all", sandbox.Configs["objective"].Value<string>("type"));

            var undone = sandbox.Undo();
            Assert.AreEqual("设置关卡目标", undone.Description);
            Assert.IsFalse(sandbox.Configs.ContainsKey("objective"), "objective 是快照外键，逆补丁后移除");
        }

        [Test]
        public void Sandbox_RejectsUnknownTarget()
        {
            var sandbox = new SandboxTransaction(SampleConfigs());
            var applied = sandbox.TryApplySlice("not_a_slice", new JObject(), "apply_config_patch", "x");
            Assert.IsFalse(applied.Changed);
            Assert.IsFalse(string.IsNullOrEmpty(applied.Error), "未知 target 必须给出错误");
        }

        // ── AgentProgress / AgentResult / Trajectory ─────────────────

        [Test]
        public void AgentProgress_P0Defaults_ChunkTokensZero()
        {
            var p = new AgentProgress();
            Assert.AreEqual(0, p.ChunkTokens, "P0 非流式：ChunkTokens 恒 0（P1 才驱动连续计数器）");
            Assert.AreEqual(0, p.Round);
            Assert.AreEqual(0, p.TokensSoFar);
            Assert.AreEqual(0, p.ProjectedKarma);
            Assert.IsFalse(p.CapWarning);
            Assert.IsFalse(p.TimedOut);
            Assert.IsFalse(p.Growing);
        }

        [Test]
        public void AgentResult_ToResultJson_MatchesConvertResultContract()
        {
            var result = new AgentResult
            {
                Type = "applied",
                Message = "已为你移子",
                ModifiedConfigs = new JObject { ["board_state"] = new JObject { ["pieces"] = new JArray() } },
                EstimatedKarmaCost = 32,
                TokenEff = 12000,
                Rounds = 3,
                Karma = new KarmaResult { Karma = 32, Roll = 1.05 },
            };

            var json = result.ToResultJson();

            Assert.AreEqual(true, json.Value<bool>("success"));
            Assert.AreEqual("applied", json.Value<string>("type"));
            Assert.AreEqual("已为你移子", json.Value<string>("message"));
            Assert.AreEqual(32, json.Value<int>("estimated_karma_cost"));
            Assert.IsFalse(json.Value<bool>("karma_blocked"), "裁决 #3 后 karma_blocked 恒 false（占位）");
            Assert.IsTrue(json["modified_configs"] is JObject);
            Assert.AreEqual(3, result.Rounds);
            Assert.AreEqual(12000, result.TokenEff);
            Assert.AreEqual(32, result.Karma.Karma);
        }

        [Test]
        public void AgentResult_Failure_NoModifiedConfigs_ZeroKarma()
        {
            var result = AgentResult.Failure("天道默然", "推演中断，没有任何变更", tokenEff: 5000, rounds: 4);

            var json = result.ToResultJson();
            Assert.IsFalse(json.Value<bool>("success"));
            Assert.AreEqual("error", json.Value<string>("type"));
            Assert.IsFalse(json.ContainsKey("modified_configs"), "失败卡不携带 modified_configs");
            Assert.AreEqual(0, json.Value<int>("estimated_karma_cost"), "失败卡不收费");
            Assert.IsTrue(result.Karma.Failed);
        }

        [Test]
        public void AgentResult_Fun_ZeroWrite_KarmaConstant1()
        {
            var result = AgentResult.Fun("今日天象不错", tokenEff: 2500, rounds: 1);

            var json = result.ToResultJson();
            Assert.IsTrue(json.Value<bool>("success"));
            Assert.AreEqual("fun", json.Value<string>("type"));
            Assert.AreEqual(1, json.Value<int>("estimated_karma_cost"), "零写入闲聊等价旧 E 类固定价");
            Assert.IsTrue(result.Karma.ZeroWrite);
        }

        [Test]
        public void AgentTrajectoryEntry_RecordsToolCallRoundAndResult()
        {
            var entry = new AgentTrajectoryEntry
            {
                Round = 2,
                ToolName = "apply_config_patch",
                ArgumentsJson = "{\"operations\":[]}",
                ResultJson = "{\"ok\":true,\"applied\":2}",
                Ok = true,
            };

            Assert.AreEqual(2, entry.Round);
            Assert.AreEqual("apply_config_patch", entry.ToolName);
            Assert.IsTrue(entry.Ok);
            Assert.IsNull(entry.ErrorCode, "成功调用无错误码");
        }
    }
}
