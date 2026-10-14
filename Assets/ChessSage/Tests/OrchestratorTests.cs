using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChessSage.Core.Cheat;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Json;
using ChessSage.Core.Mechanism;
using ChessSage.Core.Orchestration;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// T4 Orchestrator 编排全链路端到端回归（零引擎依赖，dotnet / EditMode 双跑）。
    ///
    /// 覆盖（对应任务 7 项）：
    ///   1. 主链路：自然语言 → 意图解析 → JSON Patch 生成 → Schema 校验 → 应用到配置，
    ///      逐步断言产物（意图分类 / patch_mode / 新配置 / 生效后的对局配置）。
    ///   2. Schema 拒绝路径：非法坐标补丁被拒，且给出**可操作**错误（含具体坐标与范围），
    ///      经 ErrorTranslator 后为人读文案（非空串、非通用文案）。
    ///   3. 业力公式数值断言 Δ = C×O^α（**独立推导**，不调用实现算期望）：默认 C/α、
    ///      技能树下调 C/α、O 边界（0/小/大）。
    ///   4. 识破判定：概率阈值边界（Check(0)/Check(100)/锁死）、溢出结算路径（必中/累计）。
    ///   5. 减费技能对公式的影响：技能生效后本手成本下降 → 溢出 O 归零 → Δ 归零。
    ///   6. 离线可回归：LLM 调用全程走注入的 Fake（无网络、无 API key），
    ///      无 Fake 时空密钥一律返回离线错误、绝不联网。
    ///   7. 改规撤销语义：改规栈与棋局悔棋栈相互独立；撤回改规不返还业力、不回退棋谱。
    ///
    /// 【关键约束】本文件**不引用任何 UnityEngine 类型**（ChessSage.Core 零引擎依赖），
    /// 因此同一份源码在 L1(dotnet) 与 L3(Unity EditMode) 下均能编译运行。
    ///
    /// 【防陷阱】HashSet/JArray 的无序语义用 Is.EquivalentTo / ContainsValue，
    /// 不使用裸 Assert.AreEqual 或 JArray.Contains（后者是引用比较，见缺陷 D-01）。
    /// </summary>
    public sealed class OrchestratorTests
    {
        // ══════════════════════════════════════════════════════════════
        // 可注入的假 LLM（离线回归的地基）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Fake LLM 传输：按 system prompt 分流三类调用，绝不发起网络请求。
        ///   · system == PromptBuilder.IntentParserSystem → 返回意图 JSON
        ///   · system 为空（KarmaEstimator 走 ChatUserOnlyAsync）→ 返回业力整数
        ///   · 其余（BoardTransformerSystem / RuleModifierSystem …）→ 返回补丁 JSON
        /// </summary>
        sealed class FakeLlm
        {
            public string IntentJson = "{}";
            public string CodeJson = "[]";
            public string KarmaText = "40";
            public int Calls;
            public readonly List<string> Systems = new List<string>();

            public LlmClient Build()
            {
                var client = new LlmClient("");           // 空密钥 ⇒ 仅靠 MockHandler 可用
                client.MockHandler = (system, user, temp, maxTokens, model, noThink) =>
                {
                    Calls++;
                    Systems.Add(system ?? "");
                    string content =
                        system == PromptBuilder.IntentParserSystem ? IntentJson :
                        string.IsNullOrEmpty(system) ? KarmaText :
                        CodeJson;
                    return Task.FromResult(new LlmRawResponse
                    {
                        Content = content,
                        PromptTokens = 10,
                        CompletionTokens = 5,
                    });
                };
                return client;
            }
        }

        /// <summary>隔离夹具：真实 GameSession + 隔离 Samsara + 注入 Fake 的 Orchestrator + CheatController。</summary>
        sealed class Harness : IDisposable
        {
            readonly SamsaraFixture _fx;

            public readonly FakeLlm Fake = new FakeLlm();
            public GameSession Session { get; }
            public OrchestratorEngine Engine { get; }
            public CheatController Controller { get; }

            public Harness(string variant = "xiangqi")
            {
                var loader = new ConfigLoader(TestEnv.ConfigsRoot);
                var config = loader.LoadVariant(variant);
                Session = new GameSession(config, new MechanismEngine(config.Rules));

                _fx = new SamsaraFixture();
                Engine = new OrchestratorEngine(Fake.Build(), null) { GameType = variant };

                SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
                SchemaValidator.ClearCache();

                Controller = new CheatController(Session, _fx.Engine, Engine);
            }

            public SamsaraEngine Samsara => _fx.Engine;
            public void SetSkills(params string[] ids) => _fx.SetSkills(ids);

            public void Dispose() => _fx.Dispose();
        }

        // ══════════════════════════════════════════════════════════════
        // 1. 主链路：NL → 意图 → Patch → Schema → 应用到配置
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void MainChain_BoardStateMove_IntentPatchSchemaApply()
        {
            using var h = new Harness("xiangqi");

            h.Fake.IntentJson = BuildIntent("B", "B", "move", "b_chariot_1").ToString(Newtonsoft.Json.Formatting.None);
            h.Fake.CodeJson = new JArray
            {
                Op("replace", "/pieces/0/position", new JArray(6, 4)),
            }.ToString(Newtonsoft.Json.Formatting.None);

            var ctx = Context(h);
            var result = Run(h.Engine.ProcessCommandAsync("把我的黑车挪到中路", ctx));

            // 步骤产物 1：意图解析（B/C 为多 action 合并结果，分类落在日志的 parsed_result 上）
            var log = h.Engine.GetLogs(1)[0];
            Assert.AreEqual("B", GetStr(log["intent_analysis"]?["parsed_result"] as JObject, "classification"),
                "日志应留存解析后的意图分类");
            Assert.AreEqual(h.Fake.IntentJson,
                (log["intent_analysis"]?["raw_output"]?.Value<string>()), "日志应留存 LLM 原始输出");

            // 步骤产物 2：JSON Patch 生成
            var codeGen = log["code_generation"] as JObject;
            Assert.AreEqual("patch", GetStr(codeGen, "patch_mode"), "应走 RFC6902 patch 模式而非全量 diff");
            Assert.AreEqual(1, JsonHelpers.GetInt(codeGen, "patch_operations"), "应记录 1 条 patch 操作");

            // 步骤产物 3：Schema 校验 + 新配置
            Assert.AreEqual("applied", GetStr(result, "type"), "合法补丁应生效");
            var newBoard = result["modified_configs"]?["board_state"] as JObject;
            Assert.IsNotNull(newBoard, "应返回新的 board_state 配置");
            Assert.AreEqual(6, (int)newBoard["pieces"][0]["position"][0], "棋子 x 应更新为 6");
            Assert.AreEqual(4, (int)newBoard["pieces"][0]["position"][1], "棋子 y 应更新为 4");
            Assert.IsTrue(SchemaValidator.ValidateBoardState(newBoard).ok,
                "在链中通过 Schema 校验的配置，独立复验也必须通过");

            // 步骤产物 4：应用到配置（经确认链路）
            var preview = ConvertPreview(h, result);
            Assert.IsTrue(preview.Ok, "应进入可确认的预览态");
            Assert.IsTrue(h.Controller.Confirm(preview).Applied, "确认应生效");
            var applied = h.Session.State.GetPieceById("b_chariot_1");
            Assert.AreEqual(6, applied.X, "对局配置的棋子 x 应被真实改写");
            Assert.AreEqual(4, applied.Y, "对局配置的棋子 y 应被真实改写");
        }

        [Test]
        public void MainChain_RuleChange_PatchAddsMoveAndAppliesToPiecesConfig()
        {
            using var h = new Harness("xiangqi");

            int movesBefore = MoveCount(h.Session.Config.PiecesA, "horse");

            h.Fake.IntentJson = BuildIntent("C", "C", "change_move", "horse").ToString(Newtonsoft.Json.Formatting.None);
            h.Fake.CodeJson = FlagshipHorsePatch().ToString(Newtonsoft.Json.Formatting.None);

            var preview = Run(h.Controller.SubmitAsync("让我的马可以横着走"));

            // 意图 + patch 步骤
            var log = h.Engine.GetLogs(1)[0];
            Assert.AreEqual("C", GetStr(log["intent_analysis"]?["parsed_result"] as JObject, "classification"));
            Assert.AreEqual("patch", GetStr(log["code_generation"] as JObject, "patch_mode"));

            // Schema 校验步骤：pieces 配置必须结构合法（独立复验，不依赖 C 路径）
            var newPieces = preview.ModifiedConfigs?["pieces_red"] as JObject;
            Assert.IsNotNull(newPieces, "C 类应返回 pieces_red 新配置");
            Assert.IsTrue(SchemaValidator.ValidateConfig("pieces", newPieces).ok,
                "改后 pieces 配置必须通过 Schema");
            Assert.AreEqual(movesBefore + 1, MoveCount(newPieces, "horse"), "马应新增 1 条走法");

            // 应用到配置步骤
            Assert.IsTrue(h.Controller.Confirm(preview).Applied);
            Assert.AreEqual(movesBefore + 1, MoveCount(h.Session.Config.PiecesA, "horse"),
                "确认后红方马的走法应真实生效");
        }

        // ══════════════════════════════════════════════════════════════
        // 2. Schema 拒绝路径
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void SchemaReject_OutOfBounds_ReturnsActionableError_NotGeneric()
        {
            using var h = new Harness("xiangqi");

            h.Fake.IntentJson = BuildIntent("B", "B", "move", "b_chariot_1").ToString(Newtonsoft.Json.Formatting.None);
            // 把棋子放到 x=12（棋盘宽 9 ⇒ 合法 0-8），触发越界
            h.Fake.CodeJson = new JArray
            {
                Op("replace", "/pieces/0/position", new JArray(12, 4)),
            }.ToString(Newtonsoft.Json.Formatting.None);

            var result = Run(h.Engine.ProcessCommandAsync("把我的黑车挪到棋盘外", Context(h)));

            Assert.AreEqual("error", GetStr(result, "type"), "越界补丁必须被拒绝");
            var msg = GetStr(result, "message");
            Assert.IsNotEmpty(msg, "错误信息不得为空串");
            StringAssert.Contains("超出棋盘范围", msg, "必须说明越界原因");
            StringAssert.Contains("12", msg, "必须点名具体坐标，而非笼统文案");
            Assert.AreNotEqual("该操作无法实现", msg, "不得退化为通用兜底文案");
            Assert.AreEqual(0, h.Session.State.MoveHistory.Count, "被拒的改规绝不落地到对局");

            // 经失败翻译器后应为「人读、可操作」的文案
            var preview = Run(h.Controller.SubmitAsync("把我的黑车挪到棋盘外"));
            Assert.AreEqual("error", preview.Type);
            Assert.IsNotNull(preview.Failure, "应给出结构化失败反馈");
            StringAssert.Contains("横坐标", preview.Failure.Reason, "应把 JSON Pointer 翻译成人话");
            StringAssert.DoesNotContain("position", preview.Failure.Reason, "不得残留原始字段名");
            Assert.IsNotEmpty(preview.Failure.Title, "必须有标题");
        }

        [Test]
        public void SchemaReject_Validator_SurfacesCoordinateAndRange()
        {
            // 直接对校验器断言：错误串必须「点名路径 + 数值 + 合法范围」，否则 UI 无法给出可操作建议。
            var board = TestEnv.LoadConfig("xiangqi", "board_state");
            ((JArray)board["pieces"][3]["position"])[1] = 11;   // 纵坐标越界（高 10 ⇒ 0-9）

            var (ok, err) = SchemaValidator.ValidateBoardState(board);
            Assert.IsFalse(ok, "纵坐标越界应被拒");
            StringAssert.Contains("pieces.3.position.1", err, "错误须定位到具体棋子与轴");
            StringAssert.Contains("11", err, "错误须含非法数值");
            StringAssert.Contains("0-9", err, "错误须含合法范围");
        }

        // ══════════════════════════════════════════════════════════════
        // 3. 业力公式数值断言（独立推导，不调用实现算期望）
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void KarmaDelta_DefaultCoefficients_MatchesIndependentFormula()
        {
            // 独立推导：Δ = 0.1 × O^1.5（不借助任何实现常量/函数求期望）
            var est = new KarmaEstimator(new LlmClient(""));

            Assert.AreEqual(0.0, est.CalculateDetectionDelta(0), 1e-9, "O=0 → 0（边界）");
            Assert.AreEqual(0.0, est.CalculateDetectionDelta(-5), 1e-9, "O<0 → 0（边界）");
            Assert.AreEqual(0.1 * Math.Pow(4, 1.5), est.CalculateDetectionDelta(4), 1e-9, "O=4（小）");
            Assert.AreEqual(0.1 * Math.Pow(64, 1.5), est.CalculateDetectionDelta(64), 1e-9, "O=64");
            Assert.AreEqual(0.1 * Math.Pow(100, 1.5), est.CalculateDetectionDelta(100), 1e-9, "O=100（临界必中）");
            Assert.AreEqual(0.1 * Math.Pow(1000, 1.5), est.CalculateDetectionDelta(1000), 1e-6, "O=1000（大）");
        }

        [Test]
        public void KarmaDelta_LoweredBySkillTree_MatchesIndependentFormula()
        {
            // 技能树下调 C（藏锋 0.1→0.07）与 α（缓冲 1.5→1.3）；期望用独立推导式写死。
            using var h = new Harness("xiangqi");
            h.SetSkills("stealth_t1", "karma_capacity_t3a");
            var mods = h.Samsara.GetSkillModifiers();

            Assert.AreEqual(0.07, JsonHelpers.GetDouble(mods, "detection_coefficient", -1), 1e-9, "技能应把 C 降到 0.07");
            Assert.AreEqual(1.3, JsonHelpers.GetDouble(mods, "detection_alpha", -1), 1e-9, "技能应把 α 降到 1.3");

            var est = new KarmaEstimator(new LlmClient(""));
            const double c = 0.07, alpha = 1.3;

            foreach (var o in new[] { 0.0, 4.0, 30.0, 100.0 })
            {
                double expected = o <= 0 ? 0.0 : c * Math.Pow(o, alpha);
                Assert.AreEqual(expected, est.CalculateDetectionDelta(o, mods), 1e-9, $"技能后 O={o} 的 Δ");
            }

            // 同一溢出量下，下调后的 Δ 必须严格小于默认 Δ（技能确实降低了识破风险）
            double def = est.CalculateDetectionDelta(30);
            double lowered = est.CalculateDetectionDelta(30, mods);
            Assert.Less(lowered, def, "藏锋+缓冲应使 O=30 的 Δ 严格下降");
        }

        // ══════════════════════════════════════════════════════════════
        // 4. 识破判定：阈值边界 + 溢出结算路径
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void Detection_ThresholdBoundaries_AreDeterministic()
        {
            using var h = new Harness("xiangqi");
            var det = h.Samsara.Detection;

            Assert.IsFalse(det.Check(0.0), "概率 0 恒不命中");
            Assert.IsFalse(det.Check(-1.0), "负概率恒不命中");
            Assert.IsTrue(det.Check(100.0), "概率满值必命中（roll∈[0,100) < 100）");

            h.Samsara.State.ResetOnDetection();
            Assert.IsTrue(h.Samsara.IsDetectionLocked(), "被识破后应锁死");
            Assert.IsFalse(det.Check(100.0), "锁死后即使概率满值也恒不命中");
        }

        [Test]
        public void Detection_NonPositiveOvershoot_NoSettlement()
        {
            using var h = new Harness("xiangqi");
            var res = h.Samsara.Detection.HandleOverdraft(0.0);
            Assert.IsFalse(res["detected"].Value<bool>(), "无溢出不结算");
            Assert.AreEqual(0.0, res["delta"].Value<double>(), 1e-9, "无溢出 Δ=0");
            Assert.AreEqual(0.0, h.Samsara.GetDetection(), 1e-9, "无溢出识破概率不变");
        }

        [Test]
        public void Detection_AccumulatesDelta_AndSettles()
        {
            using var h = new Harness("xiangqi");
            // 新档会自动下发入门技能 stealth_t1（C=0.07）；此处清空以锁定「名义默认」C=0.1/α=1.5。
            h.SetSkills();
            var res = h.Samsara.Detection.HandleOverdraft(4.0);   // 默认 C/α ⇒ Δ=0.8

            Assert.AreEqual(0.1 * Math.Pow(4, 1.5), res["delta"].Value<double>(), 1e-9, "Δ 应等于公式值");
            // 结算含随机判定（roll<current）；未命中 → 概率累计为 Δ，命中 → 归零并锁死。二者取一并覆盖。
            double after = h.Samsara.GetDetection();
            Assert.That(after, Is.EqualTo(0.0).Within(1e-9).Or.EqualTo(0.1 * Math.Pow(4, 1.5)).Within(1e-9),
                "溢出后概率要么累计为 Δ，要么命中归零");
        }

        [Test]
        public void Detection_OvershootOf100_AlwaysDetected_AndResetsRealmDetection()
        {
            using var h = new Harness("xiangqi");
            h.SetSkills();   // 锁定名义默认 C=0.1/α=1.5（清掉入门技能 stealth_t1）
            // O=100，默认 C/α ⇒ Δ=100 ⇒ 概率封顶 100 ⇒ 必中
            var res = h.Samsara.Detection.HandleOverdraft(100.0);

            Assert.IsTrue(res["detected"].Value<bool>(), "O=100 应必被识破");
            Assert.AreEqual(100.0, res["delta"].Value<double>(), 1e-9, "Δ=0.1×100^1.5=100");
            Assert.AreEqual(0.0, res["current"].Value<double>(), 1e-9, "命中后概率清空");
            Assert.IsTrue(res["locked"].Value<bool>(), "命中后锁死");
            Assert.AreEqual(h.Samsara.Detection.GetJudgmentText(), res["message"].Value<string>(), "应给出判词");
            Assert.IsTrue(h.Samsara.IsDetectionLocked(), "状态机应进入锁死");
            Assert.AreEqual(0.0, h.Samsara.GetDetection(), 1e-9, "锁死后概率归零");

            // 锁死后再次溢出：不再累积、不再判定
            var again = h.Samsara.Detection.HandleOverdraft(100.0);
            Assert.IsFalse(again["detected"].Value<bool>(), "锁死后不再判定");
            Assert.AreEqual(0.0, again["delta"].Value<double>(), 1e-9, "锁死后不再累积 Δ");
        }

        // ══════════════════════════════════════════════════════════════
        // 5. 减费技能：成本下降 → 溢出归零 → Δ 归零
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void CostReductionSkill_LowersOvershootAndDelta()
        {
            using var h = new Harness("xiangqi");
            h.Fake.KarmaText = "40";   // LLM 评估的基准成本
            const int karma = 90, karmaMax = 120;

            // 基准（无减费技能，人道）。用独立预估器避免 AssessAsync 的 (gameType,instruction,class) 缓存串味。
            var estBase = new KarmaEstimator(new LlmClient(""));
            estBase.SetLocalKarmaState(karma, karmaMax, 120);
            estBase.SetRealmDetection(0.0, "human");
            int baseline = Run(estBase.AssessAsync("让我的马可以横着走", "C", "", new JObject()));
            Assert.AreEqual(40, baseline, "无技能时应为基准成本");

            double oBaseline = Math.Max(0, karma + baseline - karmaMax);        // = 10
            double deltaBaseline = 0.1 * Math.Pow(oBaseline, 1.5);             // 独立推导
            Assert.AreEqual(deltaBaseline, estBase.CalculateDetectionDelta(oBaseline), 1e-9, "基准 Δ");

            // 减费技能：地狱折扣（realm_insight_t1a，本道 −25%），且当前处于地狱道
            h.SetSkills("realm_insight_t1a");
            var mods = h.Samsara.GetSkillModifiers();
            Assert.IsTrue(JsonHelpers.GetBool(mods, "hell_hungry_discount", false), "技能应点亮地狱折扣");

            var estSkill = new KarmaEstimator(new LlmClient(""));
            estSkill.SetLocalKarmaState(karma, karmaMax, 120);
            estSkill.SetRealmDetection(0.0, "hell");
            int discounted = Run(estSkill.AssessAsync("让我的马可以横着走", "C", "", mods));

            Assert.AreEqual((int)(40 * 0.75), discounted, "地狱折扣应把成本降到 30");
            Assert.Less(discounted, baseline, "减费技能必须让本手更便宜");

            double oDiscounted = Math.Max(0, karma + discounted - karmaMax);   // = 0
            double deltaDiscounted = oDiscounted <= 0 ? 0.0 : 0.1 * Math.Pow(oDiscounted, 1.5);
            Assert.AreEqual(0.0, oDiscounted, "成本降到 30 后溢出归零");
            Assert.Less(deltaDiscounted, deltaBaseline, "Δ 随成本下降而下降（这是防作弊太贵的杠杆）");
        }

        // ══════════════════════════════════════════════════════════════
        // 6. 离线可回归（无网络 / 无 API key）
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void Offline_NoApiKey_ReturnsErrorWithoutNetworkOrThrow()
        {
            var engine = new OrchestratorEngine(new LlmClient(""), null) { GameType = "xiangqi" };
            Assert.IsFalse(engine.Client.IsAvailable, "空密钥且无 Mock ⇒ 不可用");

            var ctx = new JObject { ["configs"] = new JObject() };
            var result = Run(engine.ProcessCommandAsync("让我的马可以横着走", ctx));

            Assert.AreEqual("error", GetStr(result, "type"), "离线应返回 error 而非抛异常");
            StringAssert.Contains("未设置", GetStr(result, "message"), "应提示未配置密钥");

            // 无 LLM 时的兜底价目表（离线也能展示代价）
            Assert.AreEqual(40, engine.KarmaAssessor.FallbackAssess("C"), "C 类兜底价 40");
            Assert.AreEqual(1, engine.KarmaAssessor.FallbackAssess("E"), "E 类兜底价 1");
            Assert.AreEqual(80, engine.KarmaAssessor.FallbackAssess("C+"), "C+ 类兜底价 80");
        }

        [Test]
        public void Offline_CheatController_DoesNotCallLlm_OnSubmit()
        {
            using var h = new Harness("xiangqi");
            h.Controller.SetOffline(true);

            var preview = Run(h.Controller.SubmitAsync("让我的马可以横着走"));

            Assert.AreEqual(0, h.Fake.Calls, "离线提交绝不能调用 LLM");
            Assert.IsTrue(preview.Failure?.Offline ?? false, "应转入离线降级反馈");
            Assert.AreEqual("offline", preview.Type);
        }

        [Test]
        public void FakeLlm_InjectedClient_IsAvailableWithoutRealKey()
        {
            var fake = new FakeLlm();
            var client = fake.Build();
            Assert.IsTrue(client.HasMock, "应已注入 mock 传输");
            Assert.IsTrue(client.IsAvailable, "有 mock ⇒ 无需真实密钥即视为可用");

            var r = Run(client.ChatAsync("sys", "user"));
            Assert.IsTrue(r.Ok, "mock 调用应成功");
            Assert.AreEqual(1, fake.Calls);
        }

        // ══════════════════════════════════════════════════════════════
        // 7. 改规撤销语义（双栈独立 / 不返还业力）
        // ══════════════════════════════════════════════════════════════

        [Test]
        public void RuleUndo_RefundsNoKarma_AndLeavesMoveHistoryUntouched()
        {
            using var h = new Harness("xiangqi");
            h.Fake.IntentJson = BuildIntent("C", "C", "change_move", "horse").ToString(Newtonsoft.Json.Formatting.None);
            h.Fake.CodeJson = FlagshipHorsePatch().ToString(Newtonsoft.Json.Formatting.None);

            int horseBefore = MoveCount(h.Session.Config.PiecesA, "horse");
            int karmaBefore = h.Samsara.GetKarma();

            var res = h.Controller.Confirm(Run(h.Controller.SubmitAsync("让我的马可以横着走")));
            Assert.IsTrue(res.Applied);
            int karmaAfterCheat = h.Samsara.GetKarma();
            Assert.AreEqual(karmaBefore + res.Cost, karmaAfterCheat, "确认应扣费");
            Assert.AreEqual(horseBefore + 1, MoveCount(h.Session.Config.PiecesA, "horse"), "改规应生效");
            Assert.IsTrue(h.Controller.HasRuleUndo, "确认后应入改规栈");
            int movesAtUndo = h.Session.State.MoveHistory.Count;

            Assert.IsTrue(h.Controller.UndoRule(out _), "应可撤回改规");
            Assert.AreEqual(horseBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "撤回后规则回退");
            Assert.AreEqual(karmaAfterCheat, h.Samsara.GetKarma(), "撤回不返还业力（世界观自洽）");
            Assert.AreEqual(movesAtUndo, h.Session.State.MoveHistory.Count, "撤回改规不回退棋谱");
            Assert.IsFalse(h.Controller.HasRuleUndo, "栈应已空");
        }

        [Test]
        public void RuleStack_IsIndependent_OfGameMoveUndoStack()
        {
            using var h = new Harness("xiangqi");
            h.Fake.IntentJson = BuildIntent("C", "C", "change_move", "horse").ToString(Newtonsoft.Json.Formatting.None);
            h.Fake.CodeJson = FlagshipHorsePatch().ToString(Newtonsoft.Json.Formatting.None);

            int horseBefore = MoveCount(h.Session.Config.PiecesA, "horse");
            var preview = Run(h.Controller.SubmitAsync("让我的马可以横着走"));
            Assert.IsTrue(h.Controller.Confirm(preview).Applied);
            Assert.AreEqual(1, h.Controller.RuleUndoDepth, "改规栈深度应为 1");

            // 改规后再走一手棋：只应推进「棋局栈」，不得扰动「改规栈」
            var piece = FirstMoveableRedPiece(h.Session);
            var target = h.Session.GetValidMoves(piece.Id)[0];
            Assert.IsTrue(h.Session.MakePlayerMove(piece.Id, target[0], target[1]).Success, "合法走子应成功");
            Assert.IsTrue(h.Controller.NeedsMismatchWarning, "改规后已走子，撤回前应提示局面可能不一致");
            Assert.AreEqual(1, h.Controller.RuleUndoDepth, "走子不得改变改规栈深度");
            Assert.AreEqual(horseBefore + 1, MoveCount(h.Session.Config.PiecesA, "horse"), "走子不影响已生效改规");

            // 悔棋（棋局栈）：只回退棋谱，改规仍在
            int movesAfterMove = h.Session.State.MoveHistory.Count;
            Assert.IsTrue(h.Session.UndoLastMove(), "悔棋应成功");
            Assert.Less(h.Session.State.MoveHistory.Count, movesAfterMove, "悔棋应回退棋谱");
            Assert.AreEqual(1, h.Controller.RuleUndoDepth, "悔棋不得弹改规栈");
            Assert.AreEqual(horseBefore + 1, MoveCount(h.Session.Config.PiecesA, "horse"), "悔棋不得回退改规");

            // 撤回改规（改规栈）：只回退规则，棋谱不动
            int movesBeforeRuleUndo = h.Session.State.MoveHistory.Count;
            Assert.IsTrue(h.Controller.UndoRule(out _), "应可撤回改规");
            Assert.AreEqual(horseBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "撤回改规应回退规则");
            Assert.AreEqual(movesBeforeRuleUndo, h.Session.State.MoveHistory.Count, "撤回改规不得回退棋谱");
        }

        [Test]
        public void Confirm_GuaranteedOvershoot_TriggersDetectionSettlement()
        {
            using var h = new Harness("xiangqi");
            h.SetSkills();   // 清掉入门技能 stealth_t1，锁定名义默认 C=0.1/α=1.5
            h.Fake.KarmaText = "120";   // 引擎代价 = 120（恰好不超单次上限）
            h.Fake.IntentJson = BuildIntent("C", "C", "change_move", "horse").ToString(Newtonsoft.Json.Formatting.None);
            h.Fake.CodeJson = FlagshipHorsePatch().ToString(Newtonsoft.Json.Formatting.None);

            // 把业力顶到安全线 120，使 +120 恰好溢出 120（Δ=100 → 必被识破）
            int karmaMax = h.Samsara.GetKarmaMax();
            h.Samsara.IncreaseKarma(karmaMax - h.Samsara.GetKarma());
            Assert.AreEqual(karmaMax, h.Samsara.GetKarma(), "前置：业力应处于安全线");

            var preview = Run(h.Controller.SubmitAsync("让我的马可以横着走"));
            Assert.IsTrue(preview.Ok, "应进入预览");
            Assert.AreEqual(120, preview.CostInfo.Overshoot, "120+120−120 应溢出 120");
            Assert.AreEqual(CheatTier.Danger, preview.CostInfo.Tier, "大额溢出应落入危险档");
            Assert.AreEqual(0.0, h.Samsara.GetDetection(), 1e-9, "预览阶段不得改动识破概率");

            var res = h.Controller.Confirm(preview);
            Assert.IsTrue(res.Applied);
            Assert.AreEqual(120, res.Overshoot, "确认应结算溢出 120");
            Assert.AreEqual(0.1 * Math.Pow(120, 1.5), res.DetectionDelta, 1e-6, "识破增量应等于公式值");
            Assert.IsTrue(res.Detected, "溢出 120 必被识破");
            Assert.AreEqual(0.0, res.DetectionAfter, 1e-9, "被识破后概率清空");
            Assert.IsFalse(string.IsNullOrEmpty(res.Judgment), "应给出判词");
            Assert.IsTrue(h.Samsara.IsDetectionLocked(), "识破后应锁死");
        }

        // ══════════════════════════════════════════════════════════════
        // 8. D-05 回归：C 类改规（pieces_* 变体）必须走 pieces 同一校验路径（已修复）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// D-05 回归：C 类改规（pieces_red / pieces_black / pieces_white）**不得绕过 Schema 校验**。
        ///
        /// 缺陷历史：<c>HardValidateConfig</c> 的 switch 只有 <c>case "pieces"</c>，对 side 后缀名
        /// 落 default 静默放行；而 <c>HandleActionCWithLogAsync</c> 传的恰是 "pieces_red"，
        /// 导致 C 类（最常见的改规路径）整条 Schema 闸门被跳过。
        ///
        /// 修复：<see cref="SchemaValidator.NormalizeConfigName"/> 把 pieces_* 归一化为 pieces，
        /// 复用同一校验实现。本用例正/负双向断言：
        ///   · 负向：非法原语（kind="teleport"）在四种名字下都必须被拒；
        ///   · 正向：真实合法的 pieces_red 仍必须通过（防止修复过度拒绝）。
        /// </summary>
        [Test]
        public void D05_CClassRuleConfig_IsValidatedNotBypassed()
        {
            SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
            SchemaValidator.ClearCache();

            var bad = JObject.Parse(
                "{\"side\":\"red\",\"pieces\":{\"horse\":{\"label\":\"马\",\"moves\":["
                + "{\"kind\":\"teleport\",\"land\":\"any\",\"sym\":\"rotate4\",\"to\":[1,0]}]}}}");

            // 期望：side 后缀名与 "pieces" 同等待遇，均应被拒绝。
            Assert.IsFalse(SchemaValidator.HardValidateConfig("pieces", bad).ok, "基准：pieces 应拒绝非法原语");
            Assert.IsFalse(SchemaValidator.HardValidateConfig("pieces_red", bad).ok, "pieces_red 不应绕过 Schema");
            Assert.IsFalse(SchemaValidator.HardValidateConfig("pieces_black", bad).ok, "pieces_black 不应绕过 Schema");
            Assert.IsFalse(SchemaValidator.HardValidateConfig("pieces_white", bad).ok, "pieces_white 不应绕过 Schema");

            // 正向对照：真实合法的红方棋子配置仍应通过（证明闸门确在生效，而非一律拒绝）。
            var goodPiecesRed = TestEnv.LoadConfig("xiangqi", "pieces_red");
            Assert.IsTrue(SchemaValidator.HardValidateConfig("pieces_red", goodPiecesRed).ok,
                "合法的 pieces_red 应通过（证明 D-05 修复沿用同一校验、未过度拒绝）");

            // 归一化契约：pieces_* 一律映射到基名 pieces。
            Assert.AreEqual("pieces", SchemaValidator.NormalizeConfigName("pieces_red"));
            Assert.AreEqual("pieces", SchemaValidator.NormalizeConfigName("pieces_black"));
            Assert.AreEqual("pieces", SchemaValidator.NormalizeConfigName("pieces_white"));
            Assert.AreEqual("rules", SchemaValidator.NormalizeConfigName("rules"), "非 pieces 名不得被改写");
        }

        // ══════════════════════════════════════════════════════════════
        // 工具
        // ══════════════════════════════════════════════════════════════

        /// <summary>同步等待异步调用（Unity EditMode 运行器不支持 async Task 测试方法）。</summary>
        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        static JObject Context(Harness h) => new JObject
        {
            ["configs"] = h.Controller.BuildConfigs(),
            ["skill_modifiers"] = h.Samsara.GetSkillModifiers(),
            ["allowed_classifications"] = ToJArray(h.Samsara.GetAllowedClassifications()),
        };

        /// <summary>把引擎结果转成 CheatPreview（复用 CheatController 的公开提交链路等价物）。</summary>
        static CheatPreview ConvertPreview(Harness h, JObject result)
        {
            // 复用真实链路：以一个 fake 结果经由 Submit 转换；此处仅将引擎产物包装为预览。
            var preview = new CheatPreview
            {
                Type = GetStr(result, "type"),
                Classification = GetStr(result, "classification"),
                Message = GetStr(result, "message"),
                ModifiedConfigs = result["modified_configs"] as JObject,
            };
            preview.Cards.AddRange(CheatDiffRenderer.Render(h.Controller.BuildConfigs(), preview.ModifiedConfigs));
            preview.Ok = preview.Cards.Count > 0;
            preview.CostInfo = CheatCostCalculator.Compute(
                JsonHelpers.GetInt(result, "estimated_karma_cost"),
                h.Samsara.GetKarma(), h.Samsara.GetKarmaMax(),
                h.Samsara.GetSkillModifiers(), h.Samsara.GetDetection(), h.Samsara.IsDetectionLocked());
            return preview;
        }

        static JObject BuildIntent(string classification, string actionType, string actionStr, string target)
        {
            var instruction = new JObject
            {
                ["action"] = actionStr,
                ["target"] = target,
                ["parameters"] = new JObject(),
            };
            var action = new JObject
            {
                ["type"] = actionType,
                ["target_files"] = new JArray(),
                ["instruction"] = instruction,
                ["prompt"] = "按玩家指令修改",
            };
            return new JObject
            {
                ["success"] = true,
                ["feasible"] = true,
                ["classification"] = classification,
                ["cost_energy"] = 5,
                ["response_to_player"] = "天道已应允",
                ["actions"] = new JArray { action },
            };
        }

        static JObject Op(string op, string path, JToken value)
            => new JObject { ["op"] = op, ["path"] = path, ["value"] = value };

        /// <summary>旗舰预置等价补丁：给红方「马」增加横走一格（jump 1,0 rotate4）。</summary>
        static JArray FlagshipHorsePatch() => new JArray
        {
            Op("add", "/pieces/horse/moves/-", new JObject
            {
                ["kind"] = "jump",
                ["to"] = new JArray(1, 0),
                ["block"] = new JArray(),
                ["land"] = "any",
                ["sym"] = "rotate4",
            }),
        };

        static JArray ToJArray(IEnumerable<string> items)
        {
            var arr = new JArray();
            foreach (var i in items) arr.Add(i);
            return arr;
        }

        static int MoveCount(JObject piecesCfg, string type)
        {
            var moves = piecesCfg?["pieces"]?[type]?["moves"] as JArray;
            return moves?.Count ?? -1;
        }

        static ChessSage.Core.Model.Piece FirstMoveableRedPiece(GameSession s)
        {
            foreach (var p in s.State.Pieces)
                if (p.IsAlive && p.Side == "red" && s.GetValidMoves(p.Id).Count > 0) return p;
            Assert.Fail("未找到有合法走法的红方棋子");
            return null;
        }

        static string GetStr(JObject o, string k)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return "";
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }
    }
}
