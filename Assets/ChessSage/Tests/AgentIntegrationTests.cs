using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using ChessSage.Core.Cheat;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Orchestration;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E2（集成层）端到端回归：**真实 CheatController + 真实 Samsara + 真实 GameSession + mock Agent 引擎**。
    ///
    /// 覆盖 E2 四条红线与 UI 链路数据契约：
    ///   ① 两段式契约：SubmitAsync 只预估不改状态 / Confirm 唯一扣费 / 取消预览不收费；
    ///   ② 改规栈不回退棋谱：Confirm 入栈的规则切片不含 board_state，UndoRule 不动棋谱；
    ///   ③ 离线降级：传输失败 → 错误预览；连续 2 次失败 → Offline=true → 预置链路仍可走（④）；
    ///   ④ 预置链路：SubmitPreset 绕过 Agent、固定业力价，与自然语言并存不悖。
    /// 另含：20s 到点收尾（有写入 → partial 预览 / 零写入 → 失败卡零业力）、进度回调序列、
    /// 顶格警示不拦截（clamp 120）、门控换轴（allowed_tools / allowed_patch_targets）。
    ///
    /// 【纪律】零网络、零 wall-clock：传输走 MockToolHandler，20s 经 CancellationToken 模拟；
    /// 本文件不引用 UnityEngine（dotnet / EditMode 双跑）。
    /// </summary>
    [TestFixture]
    public sealed class AgentIntegrationTests
    {
        const int Seed = 20261023;
        const int MaxRounds = 4;

        // ────────────────────────────────────────────────────────────────
        // 夹具
        // ────────────────────────────────────────────────────────────────

        /// <summary>同步收集进度（引擎线程直接回调；不依赖 SynchronizationContext）。</summary>
        sealed class ProgressRecorder : IProgress<AgentProgress>
        {
            public readonly List<AgentProgress> Items = new List<AgentProgress>();
            public void Report(AgentProgress value)
            {
                if (value != null) Items.Add(value);
            }
        }

        sealed class Harness : IDisposable
        {
            readonly SamsaraFixture _fx;

            public GameSession Session { get; }
            public AgentEngine Engine { get; }
            public CheatController Controller { get; }
            public int TransportCalls { get; private set; }

            public Harness(string variant, LlmTransport transport, bool wireSchemaValidator = true)
            {
                var loader = new ConfigLoader(TestEnv.ConfigsRoot);
                var config = loader.LoadVariant(variant);
                Session = new GameSession(config, new MechanismEngine(config.Rules));
                _fx = new SamsaraFixture();

                var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "deepseek-flash");
                client.MockToolHandler = req =>
                {
                    TransportCalls++;
                    return transport(req);
                };

                Engine = new AgentEngine(client, config: AgentRemakeConfig.Default, sessionSeed: Seed)
                {
                    GameType = variant,
                };
                if (wireSchemaValidator)
                {
                    SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
                    SchemaValidator.ClearCache();
                    Engine.ConfigValidator = (name, cfg) => SchemaValidator.HardValidateConfig(name, cfg);
                }
                else
                {
                    Engine.ConfigValidator = (name, cfg) => (true, (string)null);
                }
                Engine.ObjectiveTypesProvider = () => new[] { "checkmate", "capture_all", "survive_turns" };

                Controller = new CheatController(Session, _fx.Engine, Engine);
            }

            public SamsaraEngine Samsara => _fx.Engine;
            public void SetSkills(params string[] ids) => _fx.SetSkills(ids);
            public void Dispose() => _fx.Dispose();
        }

        // ── mock 轮次构造 ───────────────────────────────────────────────

        static LlmRawResponse ToolRound(string name, string args, int prompt, int completion, string id = "call_1")
            => new LlmRawResponse
            {
                FinishReason = "tool_calls",
                PromptTokens = prompt,
                CompletionTokens = completion,
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall { Id = id, Name = name, ArgumentsJson = args },
                },
            };

        static LlmRawResponse TextRound(string content, int prompt, int completion)
            => new LlmRawResponse
            {
                Content = content,
                FinishReason = "stop",
                PromptTokens = prompt,
                CompletionTokens = completion,
            };

        /// <summary>把响应序列做成一个传输：按调用序号供给，用尽后重复最后一条（耗尽即错位会被断言抓出）。</summary>
        static LlmTransport Scripted(params LlmRawResponse[] rounds)
        {
            int callNo = 0;
            return req => Task.FromResult(rounds[Math.Min(callNo++, rounds.Length - 1)]);
        }

        /// <summary>旗舰补丁：给红方「马」增加横走一格（与预置「让我的马可以横着走」等价）。</summary>
        static string HorsePatchArgs() =>
            "{\"operations\":[{\"target\":\"pieces_red\",\"patch\":[{\"op\":\"add\",\"path\":\"/pieces/horse/moves/-\"," +
            "\"value\":{\"kind\":\"jump\",\"to\":[1,0],\"block\":[],\"land\":\"any\",\"sym\":\"rotate4\"}}]}]}";

        static int MoveCount(JObject piecesCfg, string type)
        {
            var moves = piecesCfg?["pieces"]?[type]?["moves"] as JArray;
            return moves?.Count ?? -1;
        }

        // ────────────────────────────────────────────────────────────────
        // 红线① + 冒烟：提交 → 预览 → 确认 → 扣费（真实引擎，mock 传输）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void Submit_Preview_Confirm_AppliesOnlyOnConfirm_AndChargesOnce()
        {
            using var h = new Harness("xiangqi", Scripted(
                ToolRound("get_config_slice", "{\"target\":\"pieces_red\"}", 1200, 260),
                ToolRound("apply_config_patch", HorsePatchArgs(), 1600, 420),
                ToolRound("commit_preview", "{\"summary\":\"马可横走一格\"}", 1900, 120)));

            int karmaBefore = h.Samsara.GetKarma();
            int movesBefore = MoveCount(h.Session.Config.PiecesA, "horse");
            int historyBefore = h.Session.State.MoveHistory.Count;

            // ── 提交：只预估，不改任何真实状态 ──
            var preview = h.Controller.SubmitAsync("让我的马可以横着走").GetAwaiter().GetResult();

            Assert.AreEqual("applied", preview.Type, "Agent 结果应经 ConvertResult 落为 applied");
            Assert.IsTrue(preview.Ok, "应进入可确认的预览态");
            Assert.Greater(preview.Cards.Count, 0, "应渲染 diff 卡");
            Assert.IsNotNull(preview.CostInfo, "应有代价预估");
            Assert.Greater(preview.CostInfo.Cost, 0, "有写入必须计价（token 实耗）");
            Assert.LessOrEqual(preview.CostInfo.Cost, 120, "单次业价恒 ≤ 120（clamp 口径）");

            Assert.AreEqual(karmaBefore, h.Samsara.GetKarma(), "红线①：提交阶段绝不能扣业力");
            Assert.AreEqual(movesBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "红线①：提交阶段绝不能改配置");
            Assert.IsFalse(h.Controller.HasRuleUndo, "红线①：提交阶段不入改规栈");
            Assert.AreEqual(historyBefore, h.Session.State.MoveHistory.Count, "红线①：提交阶段不动棋谱");

            // ── 确认：唯一扣费点 ──
            var res = h.Controller.Confirm(preview);

            Assert.IsTrue(res.Applied);
            Assert.AreEqual(preview.CostInfo.Cost, res.Cost, "确认收费 = 预览数字（不重摇）");
            Assert.AreEqual(karmaBefore + preview.CostInfo.Cost, h.Samsara.GetKarma(), "红线①：Confirm 唯一扣费");
            Assert.AreEqual(movesBefore + 1, MoveCount(h.Session.Config.PiecesA, "horse"), "确认后规则真实生效");
            Assert.AreEqual(1, h.Controller.RuleUndoDepth, "红线②：确认后入改规栈");
            Assert.AreEqual(historyBefore, h.Session.State.MoveHistory.Count, "红线②：改规不入棋谱栈");

            // 端到端冒烟证据（Logs/e2-smoke.txt）：把链路关键数字打出来，便于人工复核。
            TestContext.Progress.WriteLine(
                $"[smoke] submit.type={preview.Type} cards={preview.Cards.Count} cost={preview.CostInfo.Cost} " +
                $"karma={karmaBefore}->{h.Samsara.GetKarma()} tokenEff={h.Controller.LastAgentResult.TokenEff} " +
                $"rounds={h.Controller.LastAgentResult.Rounds} writes={h.Controller.LastAgentResult.Writes} " +
                $"tools={string.Join(",", h.Controller.LastAgentResult.Trajectory.ConvertAll(t => t.ToolName).ToArray())}");

            // ── 撤回改规：规则回退、棋谱不动、不返还业力 ──
            Assert.IsTrue(h.Controller.UndoRule(out _));
            Assert.AreEqual(movesBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "撤回后规则回退");
            Assert.AreEqual(karmaBefore + preview.CostInfo.Cost, h.Samsara.GetKarma(), "撤回不返还业力");
            Assert.AreEqual(historyBefore, h.Session.State.MoveHistory.Count, "红线②：撤回改规不回退棋谱");
        }

        [Test]
        public void CancelPreview_LeavesEverythingUnchanged_AndChargesNothing()
        {
            using var h = new Harness("xiangqi", Scripted(
                ToolRound("apply_config_patch", HorsePatchArgs(), 1600, 420),
                ToolRound("commit_preview", "{\"summary\":\"马可横走一格\"}", 1900, 120)));

            int karmaBefore = h.Samsara.GetKarma();
            int movesBefore = MoveCount(h.Session.Config.PiecesA, "horse");

            var preview = h.Controller.SubmitAsync("让我的马可以横着走").GetAwaiter().GetResult();
            Assert.IsTrue(preview.Ok);

            // 玩家取消：面板只是丢弃预览（不调用 Confirm）
            Assert.AreEqual(karmaBefore, h.Samsara.GetKarma(), "红线①：取消预览不收费");
            Assert.AreEqual(movesBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "红线①：取消预览不改配置");
            Assert.IsFalse(h.Controller.HasRuleUndo, "取消不入改规栈");
        }

        // ────────────────────────────────────────────────────────────────
        // 红线②：agent 产生的 board_state 变更视同棋谱（UndoRule 不回退）
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void AgentBoardStateChange_IsMoveSemantics_UndoRuleDoesNotRevertIt()
        {
            using var h = new Harness("xiangqi", Scripted(
                ToolRound("move_piece", "{\"action\":\"move\",\"piece_id\":\"r_chariot_1\",\"to\":[0,8]}", 1200, 260),
                ToolRound("apply_config_patch", HorsePatchArgs(), 1600, 420),
                ToolRound("commit_preview", "{\"summary\":\"车退一格，且马可横走\"}", 1900, 120)));

            var preview = h.Controller.SubmitAsync("车退一格并让马横走").GetAwaiter().GetResult();
            Assert.IsTrue(preview.Ok, "应进入预览");
            JToken newPiecePos = null;
            foreach (var p in (JArray)preview.ModifiedConfigs?["board_state"]?["pieces"])
                if ((string)p["id"] == "r_chariot_1") { newPiecePos = p["position"]?[1]; break; }
            Assert.IsNotNull(newPiecePos, "预览应含 board_state 变更");
            Assert.AreEqual(8, newPiecePos.Value<int>(), "预览内棋子位置应是沙箱结果");

            Assert.IsTrue(h.Controller.Confirm(preview).Applied);
            int chariotY = h.Session.State.GetPieceById("r_chariot_1").Y;
            Assert.AreEqual(8, chariotY, "确认后棋子位置真实生效");

            Assert.IsTrue(h.Controller.UndoRule(out _));
            Assert.AreEqual(8, h.Session.State.GetPieceById("r_chariot_1").Y,
                "红线②：撤回改规不得回退 board_state（棋谱语义）");
        }

        // ────────────────────────────────────────────────────────────────
        // 20s 到点收尾（策略 A）：有写入 → partial 预览；零写入 → 失败卡零业力
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void Timeout_WithWrites_PartialPreview_TimedOutFlagAndZeroWriteFailure()
        {
            var cts = new CancellationTokenSource();
            var calls = 0;
            LlmTransport transport = req =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromResult(ToolRound("apply_config_patch", HorsePatchArgs(), 1600, 420));
                // 第 2 轮：模拟 20s 到点——组合根取消 cts，当前轮 HTTP 取消（阻塞期即抛）
                cts.Cancel();
                throw new OperationCanceledException("20s 到点");
            };

            using var h = new Harness("xiangqi", transport);
            int karmaBefore = h.Samsara.GetKarma();

            var preview = h.Controller.SubmitAsync("让我的马可以横着走", cts.Token).GetAwaiter().GetResult();

            Assert.IsTrue(h.Controller.LastAgentResult.TimedOut, "到点应置 TimedOut（UX 标志位）");
            Assert.IsTrue(preview.Ok, "有写入 → 收尾策略 A：交付 partial 预览");
            Assert.IsTrue(preview.Partial, "preview.Partial 必须置真（partial 角标）");
            Assert.Greater(preview.Cards.Count, 0, "半成品仍应有 diff 卡");
            Assert.LessOrEqual(preview.CostInfo.Cost, 120, "半成品价按 f(T_stop) 且 clamp ≤ 120");
            Assert.AreEqual(karmaBefore, h.Samsara.GetKarma(), "预览阶段不收费（两段式契约不变）");

            // 半成品可确认（与主动收手同一收尾函数）
            Assert.IsTrue(h.Controller.Confirm(preview).Applied, "半成品预览同样可确认");
        }

        [Test]
        public void Timeout_NoWrites_FailureCard_ZeroKarma_AndActionableCopy()
        {
            var cts = new CancellationTokenSource();
            LlmTransport transport = req =>
            {
                cts.Cancel();   // 首轮即到点：沙箱零写入
                throw new OperationCanceledException("20s 到点");
            };

            using var h = new Harness("xiangqi", transport);
            int karmaBefore = h.Samsara.GetKarma();
            var preview = h.Controller.SubmitAsync("让我的马可以横着走", cts.Token).GetAwaiter().GetResult();

            Assert.IsFalse(preview.Ok, "零写入 → 失败卡（不收费）");
            Assert.IsNotNull(preview.Failure, "必须有结构化失败反馈");
            StringAssert.Contains("力竭", preview.Failure.Reason + preview.Failure.Title,
                "设计 §3.2 #17：到点零写入文案「天道力竭…不耗业力」");
            Assert.IsNotEmpty(preview.Failure.Title);
            Assert.AreEqual(karmaBefore, h.Samsara.GetKarma(), "零写入到点零业力");
            Assert.IsFalse(h.Controller.HasRuleUndo, "不入改规栈");
        }

        // ────────────────────────────────────────────────────────────────
        // 进度回调（S4 数据契约）：每轮一次、token 单调、投射业力随动、顶格警示
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void Progress_ReportsPerRound_MonotonicTokens_AndCapWarning()
        {
            // 单轮 14K token：第 2 轮起投射 ≥ 120（顶格警示判据），但循环不得被拦截。
            using var h = new Harness("xiangqi", Scripted(
                ToolRound("get_config_slice", "{\"target\":\"rules\"}", 8000, 6000, "c1"),
                ToolRound("apply_config_patch", HorsePatchArgs(), 9000, 7000, "c2"),
                ToolRound("commit_preview", "{\"summary\":\"改好了\"}", 10000, 500)));

            var progress = new ProgressRecorder();
            var preview = h.Controller.SubmitAsync("让我的马可以横着走", CancellationToken.None, progress)
                .GetAwaiter().GetResult();

            Assert.GreaterOrEqual(progress.Items.Count, 3, "每轮至少一次进度回调（P0 轮级）");
            long last = -1;
            foreach (var p in progress.Items)
            {
                Assert.GreaterOrEqual(p.TokensSoFar, last, "TokensSoFar 必须单调不减");
                last = p.TokensSoFar;
                Assert.GreaterOrEqual(p.ProjectedKarma, 0);
            }
            Assert.IsTrue(progress.Items.Exists(p => p.CapWarning), "投射触顶应置 CapWarning（只警示不拦截）");
            Assert.IsTrue(progress.Items.Exists(p => p.CurrentPhase == "wrap-up"), "收尾应有 wrap-up 进度");

            Assert.AreEqual("applied", preview.Type, "顶格警示不拦截：循环继续到 commit_preview");
            Assert.IsTrue(preview.Ok);
            Assert.LessOrEqual(preview.CostInfo.Cost, 120, "确认口径 clamp 到 120（裁决 #3）");
            Assert.AreEqual(TransportCallsOf(h), 3, "顶格警示不影响传输调用次数");
        }

        static int TransportCallsOf(Harness h) => h.TransportCalls;

        // ────────────────────────────────────────────────────────────────
        // 红线③ 离线降级 + 红线④ 预置链路
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void TransportThrows_TwoConsecutiveFailures_SwitchOffline_PresetsStillWork()
        {
            LlmTransport boom = req => throw new InvalidOperationException("网络不通");
            using var h = new Harness("xiangqi", boom);

            var first = h.Controller.SubmitAsync("让我的马可以横着走").GetAwaiter().GetResult();
            Assert.AreEqual("error", first.Type, "传输失败应落错误预览");
            Assert.AreEqual(1, h.Controller.ConsecutiveFailures);
            Assert.IsFalse(h.Controller.Offline, "首次失败不降级");

            var second = h.Controller.SubmitAsync("让我的马可以横着走").GetAwaiter().GetResult();
            Assert.AreEqual(2, h.Controller.ConsecutiveFailures);
            Assert.IsTrue(h.Controller.Offline, "红线③：连续 2 次失败 → 离线降级");

            int callsBefore = h.TransportCalls;
            var third = h.Controller.SubmitAsync("让我的马可以横着走").GetAwaiter().GetResult();
            Assert.AreEqual("offline", third.Type, "离线后提交直接返回离线引导");
            Assert.IsTrue(third.Failure?.Offline ?? false);
            Assert.AreEqual(callsBefore, h.TransportCalls, "离线后不再调用 LLM");

            // 红线④：离线预置链路照常（绕过 Agent、固定业力价）
            var preset = ChessSage.Core.Cheat.CheatPresetLibrary.ForVariant("xiangqi")
                .Find(p => p.Text == "让我的马可以横着走");
            Assert.IsNotNull(preset, "预置库应含旗舰预置");
            int karma = h.Samsara.GetKarma();
            var pv = h.Controller.SubmitPreset(preset);
            Assert.IsTrue(pv.Ok, "离线预置仍可生成预览");
            Assert.AreEqual(preset.Karma, pv.CostInfo.Cost, "预置=明码标价（固定业力）");
            Assert.AreEqual(karma, h.Samsara.GetKarma(), "预置预览不扣费");
            Assert.IsTrue(h.Controller.Confirm(pv).Applied, "预置可确认");
        }

        [Test]
        public void SubmitPreset_DoesNotInvokeAgent_AtAll()
        {
            using var h = new Harness("xiangqi", Scripted(TextRound("不该被调用", 100, 10)));
            var preset = ChessSage.Core.Cheat.CheatPresetLibrary.ForVariant("xiangqi")
                .Find(p => p.Text == "让我的马可以横着走");

            var pv = h.Controller.SubmitPreset(preset);

            Assert.IsTrue(pv.Ok);
            Assert.AreEqual(0, h.TransportCalls, "红线④：预置绕过 Agent，零 LLM 调用");
        }

        // ────────────────────────────────────────────────────────────────
        // 门控换轴（S3）：context 键由 Samsara 真源派生
        // ────────────────────────────────────────────────────────────────

        [Test]
        public void ContextAllowedTools_DerivedFromSamsaraSkills_NoLegacyKey()
        {
            using var h = new Harness("xiangqi", Scripted(TextRound("嗯", 100, 10)));

            // 新档：恒可用集（L0 读 + L1 写 + patch 通道），无特权工具
            var fresh = h.Controller.BuildAllowedTools();
            CollectionAssert.Contains(fresh, "move_piece");
            CollectionAssert.Contains(fresh, "apply_config_patch");
            CollectionAssert.DoesNotContain(fresh, "set_objective", "改胜条需 cheat_mastery_t1a");

            // t1a：改胜条
            h.SetSkills("stealth_t1", "cheat_mastery_t1a");
            CollectionAssert.Contains(h.Controller.BuildAllowedTools(), "set_objective");

            // t1b（互斥分支）：工具集不额外放开特权工具，但 ui_config/board 作用域解锁
            h.SetSkills("stealth_t1", "cheat_mastery_t1b");
            CollectionAssert.DoesNotContain(h.Controller.BuildAllowedTools(), "set_objective");
            CollectionAssert.Contains(h.Controller.BuildAllowedPatchTargets(), "ui_config");
            CollectionAssert.Contains(h.Controller.BuildAllowedPatchTargets(), "board");

            // 旧键零残留：旧分类轴方法必须删除（反射守卫；E3 LegacyFreeProbe 全量面见 QA §4.4.1-P4）
            // get_karma_state 必须把「本次提交的」工具集告诉 Agent（否则它不知道自己能做什么）
            using var h2 = new Harness("xiangqi", Scripted(
                ToolRound("get_karma_state", "{}", 800, 100),
                TextRound("你可改胜条。", 900, 80)));
            h2.SetSkills("stealth_t1", "cheat_mastery_t1a");
            h2.Controller.SubmitAsync("我能做什么").GetAwaiter().GetResult();
            Assert.IsNotNull(h2.Controller.LastAgentResult, "应有结果对象");
            StringAssert.Contains("set_objective", h2.Controller.LastAgentResult.Trajectory[0].ResultJson,
                "get_karma_state 的 allowed_tools 必须含 t1a 解锁的工具");

            var legacy = typeof(CheatController).GetMethod("AllowedClassifications",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNull(legacy, "旧分类轴方法必须删除");
        }

        [Test]
        public void DiscountSkill_LowersKarma_OnSameScript_AndSameSeed()
        {
            // 裁决 #6 / 设计 D-6：减费技能 = 业力乘数（不是 token 折扣）。
            // 同脚本、同 token、同 sessionSeed ⇒ roll 相同，唯一变量是技能 ⇒ 终值必须严格更低。
            int Cost(bool withSkill)
            {
                using var h = new Harness("xiangqi", Scripted(
                    ToolRound("apply_config_patch", HorsePatchArgs(), 1600, 420),
                    ToolRound("commit_preview", "{\"summary\":\"改好了\"}", 1900, 120)));
                if (withSkill) h.SetSkills("stealth_t1", "realm_insight_t1a");  // 地狱道 −25%
                var pv = h.Controller.SubmitAsync("让我的马可以横着走").GetAwaiter().GetResult();
                Assert.IsTrue(pv.Ok, "应进入预览");
                return pv.CostInfo.Cost;
            }

            int baseline = Cost(false);
            int discounted = Cost(true);
            Assert.Greater(baseline, 1, "前置：基准价应可被折扣（未被 clamp 到下限）");
            Assert.Less(discounted, baseline, "减费技能必须让同一手的业价严格下降");
        }

        [Test]
        public void PatchScope_NotUnlocked_RejectedWithScopeHint_AndZeroWrites()
        {
            // t1a 玩家（无 t1b）改 ui_config：应被作用域门控拒绝并给出可操作提示
            using var h = new Harness("xiangqi", Scripted(
                ToolRound("apply_config_patch",
                    "{\"operations\":[{\"target\":\"ui_config\",\"patch\":[{\"op\":\"replace\",\"path\":\"/theme\",\"value\":\"light\"}]}]}",
                    1200, 300)));

            h.SetSkills("stealth_t1", "cheat_mastery_t1a");
            var preview = h.Controller.SubmitAsync("把界面换成浅色").GetAwaiter().GetResult();

            // 工具拒绝回喂后模型未再调用 → 结果非 applied；关键是配置零改动、无预览
            Assert.IsFalse(preview.Ok, "未解锁作用域不得产生可确认预览");
            var ui = h.Session.Config.UiConfig as JObject;
            Assert.AreNotEqual("light", ui?["theme"]?.ToString(), "未解锁作用域零写入");
        }
    }
}
