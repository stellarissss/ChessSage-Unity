using System.Collections.Generic;
using System.IO;
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
    /// T3 作弊改规面板的 Core 侧纯逻辑测试（零引擎依赖，dotnet / EditMode 双跑）。
    ///
    /// 覆盖：人读 diff、代价分档、失败文案映射、Schema 错误翻译、预置指令、双撤销栈、
    /// 以及**最易错的集成顺序**（提交不扣费 / 确认才扣费 / 溢出才结算识破）。
    /// </summary>
    public sealed class CheatPanelTests
    {
        // ── 夹具 ────────────────────────────────────────────────────────

        sealed class Harness : System.IDisposable
        {
            readonly SamsaraFixture _fx;
            public CheatController Controller { get; }
            public GameSession Session { get; }
            public SamsaraEngine Samsara => _fx.Engine;

            public Harness(string variant, string apiKey = "")
            {
                var loader = new ConfigLoader(TestEnv.ConfigsRoot);
                var config = loader.LoadVariant(variant);
                Session = new GameSession(config, new MechanismEngine(config.Rules));
                _fx = new SamsaraFixture();
                var client = new LlmClient(apiKey);           // 空 key ⇒ IsAvailable=false（离线）
                var engine = new OrchestratorEngine(client, null) { GameType = variant };
                SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
                SchemaValidator.ClearCache();
                Controller = new CheatController(Session, _fx.Engine, engine);
            }

            public void Dispose() => _fx.Dispose();
        }

        static JObject LoadPieces(string variant, string file) => TestEnv.LoadConfig(variant, file);

        // ── 人读 diff（UX §5.1）────────────────────────────────────────

        [Test]
        public void DiffRenderer_ProducesReadableBeforeAfter_NotPointers()
        {
            var oldCfg = new JObject { ["pieces_red"] = LoadPieces("xiangqi", "pieces_red") };
            var preset = FindPreset("让我的马可以横着走");
            var newPieces = ChessSage.Core.Json.JsonPatch
                .Apply(oldCfg["pieces_red"], preset.Patch) as JObject;

            var cards = CheatDiffRenderer.Render(oldCfg, new JObject { ["pieces_red"] = newPieces });

            Assert.AreEqual(1, cards.Count, "马走法改动应合成一张卡");
            var card = cards[0];
            Assert.IsTrue(card.HasChanges, "卡片应有改动行");
            var line = card.Lines[0];
            Assert.AreNotEqual(line.Before, line.After, "改前/改后必须不同");
            StringAssert.DoesNotContain("/pieces/", line.Before, "禁止显示原始 JSON Pointer");
            StringAssert.DoesNotContain("/pieces/", line.After, "禁止显示原始 JSON Pointer");
            StringAssert.Contains("馬", card.Title, "标题应含棋子名（中文）");
        }

        [Test]
        public void DiffRenderer_MergesCrossConfigIntoSingleCard()
        {
            // 同一匹「马」：既改 pieces 走法，又改 board_state 位置 → 合并为一张卡（UX §5.4.2）
            var oldCfg = new JObject
            {
                ["pieces_red"] = LoadPieces("xiangqi", "pieces_red"),
                ["board_state"] = TestEnv.LoadConfig("xiangqi", "board_state"),
            };
            var newPieces = (JObject)oldCfg["pieces_red"].DeepClone();
            ((JObject)newPieces["pieces"]["horse"])["moves"] = new JArray(
                JObject.Parse("{\"kind\":\"jump\",\"to\":[1,0],\"block\":[],\"land\":\"any\",\"sym\":\"rotate4\"}"));

            var newBoard = (JObject)oldCfg["board_state"].DeepClone();
            var pieces = (JArray)newBoard["pieces"];
            int idx = -1;
            for (int i = 0; i < pieces.Count; i++)
                if ((string)pieces[i]["type"] == "horse" && (string)pieces[i]["side"] == "red") { idx = i; break; }
            Assert.GreaterOrEqual(idx, 0, "应存在红马");
            ((JArray)pieces[idx]["position"])[0] = 9;

            var cards = CheatDiffRenderer.Render(oldCfg, new JObject
            {
                ["pieces_red"] = newPieces,
                ["board_state"] = newBoard,
            });

            Assert.AreEqual(1, cards.Count, "跨配置的同一棋子改动应合并为一张卡");
            Assert.GreaterOrEqual(cards[0].Lines.Count, 2, "合并卡应同时含走法与位置两行");
        }

        [Test]
        public void DiffRenderer_EmptyDiff_YieldsNoCards()
        {
            var cfg = new JObject { ["pieces_red"] = LoadPieces("xiangqi", "pieces_red") };
            var cards = CheatDiffRenderer.Render(cfg, cfg);
            Assert.AreEqual(0, cards.Count, "无实质变化不应产生卡片");
        }

        // ── 代价分档（UX §6 / §8）───────────────────────────────────────

        [Test]
        public void CostCalculator_TiersAndDelta()
        {
            var mods = new JObject { ["detection_coefficient"] = 0.1, ["detection_alpha"] = 1.5 };

            var safe = CheatCostCalculator.Compute(30, 68, 120, mods, 12, false);
            Assert.AreEqual(0, safe.Overshoot, "68+30 ≤ 120 不溢出");
            Assert.AreEqual(CheatTier.Safe, safe.Tier);
            Assert.AreEqual(0.0, safe.DetectionDelta, 1e-9, "不溢出 Δ=0");

            var critical = CheatCostCalculator.Compute(45, 92, 120, mods, 0, false);
            Assert.AreEqual(17, critical.Overshoot, "92+45-120=17");
            Assert.AreEqual(CheatTier.Critical, critical.Tier);
            Assert.AreEqual(0.1 * System.Math.Pow(17, 1.5), critical.DetectionDelta, 1e-6, "Δ=0.1×O^1.5");

            var danger = CheatCostCalculator.Compute(90, 110, 120, mods, 0, false);
            Assert.AreEqual(80, danger.Overshoot, "110+90-120=80");
            Assert.AreEqual(CheatTier.Danger, danger.Tier);
        }

        // ── 失败文案映射 + Schema 翻译（UX §7）──────────────────────────

        [Test]
        public void ErrorTranslator_SchemaOob_TranslatedToChinese()
        {
            var msg = Translate("Schema校验失败，重试2次后仍有错误: [pieces.0.position.0] 12 超出棋盘范围 (0-8)");
            StringAssert.Contains("横坐标超出", msg);
            StringAssert.DoesNotContain("position", msg, "不得残留原始 JSON Pointer");

            var msgY = Translate("[pieces.3.position.1] 11 超出棋盘范围 (0-9)");
            StringAssert.Contains("纵坐标超出", msgY);
        }

        [Test]
        public void ErrorTranslator_MapsEngineResultTypes()
        {
            var blocked = CheatErrorTranslator.Present(new JObject
            { ["type"] = "rejected", ["karma_blocked"] = true, ["message"] = "作弊业力评估为 200 点，超出单次上限 120 点，天道拦截·不予执行" });
            StringAssert.Contains("业障过重", blocked.Title);
            Assert.IsTrue(blocked.Actions.Exists(a => a.Kind == CheatActionKind.SplitSteps), "应引导拆成两小步");

            var locked = CheatErrorTranslator.Present(new JObject
            { ["type"] = "rejected", ["classification"] = "C+", ["message"] = "该操作被技能树拦截：自定义棋子（需在技能树中解锁）" });
            StringAssert.Contains("尚未解锁", locked.Title);
            Assert.IsTrue(locked.Actions.Exists(a => a.Kind == CheatActionKind.GoSkillTree), "应含前往技能树");

            var offline = CheatErrorTranslator.Present(new JObject
            { ["type"] = "error", ["message"] = "未设置API密钥，请先在设置中输入DeepSeek API Key" });
            Assert.IsTrue(offline.Offline, "无密钥应转入离线降级");

            var fun = CheatErrorTranslator.Present(new JObject
            { ["type"] = "fun", ["message"] = "红方子力略占优" });
            Assert.IsTrue(fun.IsFun, "E 类应走对话气泡");

            Assert.IsNull(CheatErrorTranslator.Present(new JObject { ["type"] = "applied" }), "成功不应产生失败反馈");
        }

        static string Translate(string raw) => CheatErrorTranslator.TranslateValidationMessage(raw);

        // ── 预置指令（UX §10）──────────────────────────────────────────

        [Test]
        public void Presets_FilteredByVariant()
        {
            var xiangqi = CheatPresetLibrary.ForVariant("xiangqi");
            Assert.IsTrue(xiangqi.Exists(p => p.Text == "让我的马可以横着走"), "象棋应含旗舰预置");
            Assert.IsFalse(xiangqi.Exists(p => p.Variant == "weiqi"), "象棋局不应显示围棋预置");
            Assert.IsTrue(xiangqi.Exists(p => p.Variant == "common"), "通用预置应保留");
        }

        [Test]
        public void FlagshipPreset_PassesSchemaValidation()
        {
            SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
            SchemaValidator.ClearCache();

            var preset = FindPreset("让我的马可以横着走");
            var oldPieces = LoadPieces("xiangqi", "pieces_red");
            var newPieces = ChessSage.Core.Json.JsonPatch.Apply(oldPieces, preset.Patch) as JObject;
            var (ok, err) = SchemaValidator.HardValidateConfig("pieces_red", newPieces);
            Assert.IsTrue(ok, "旗舰预置补丁必须通过 Schema：" + err);

            // 负向对照（防止 D-05 回归后本断言再次沦为「空转」）：同一 "pieces_red" 名下，
            // 非法原语必须被拒——只有当闸门对 side 后缀名真正生效时，上面的正向断言才有意义。
            var illegal = JObject.Parse(
                "{\"side\":\"red\",\"pieces\":{\"horse\":{\"label\":\"马\",\"moves\":["
                + "{\"kind\":\"teleport\",\"land\":\"any\",\"sym\":\"rotate4\",\"to\":[1,0]}]}}}");
            Assert.IsFalse(SchemaValidator.HardValidateConfig("pieces_red", illegal).ok,
                "pieces_red 名下的非法原语必须被拒（证明校验闸门对 side 后缀名确已生效）");
        }

        [Test]
        public void AllPresets_ApplyCleanlyToTheirVariant()
        {
            foreach (var variant in new[] { "xiangqi", "dongwuqi", "tiaoqi", "wuziqi", "weiqi", "heibaiqi" })
            {
                using var h = new Harness(variant);
                foreach (var preset in CheatPresetLibrary.ForVariant(variant))
                {
                    var preview = h.Controller.SubmitPreset(preset);
                    Assert.IsTrue(preview.Ok, $"{variant} 预置「{preset.Text}」应生成预览");
                    Assert.Greater(preview.Cards.Count, 0, $"{variant} 预置「{preset.Text}」应有 diff 卡");
                }
            }
        }

        // ── 集成顺序：提交不扣费 / 确认才扣费（UX §11.1）────────────────

        [Test]
        public void SubmitPreset_DoesNotSpendKarma_ConfirmDoes()
        {
            using var h = new Harness("xiangqi");
            var preset = FindPreset("让我的马可以横着走");

            var before = h.Samsara.GetKarma();
            var horseBefore = MoveCount(h.Session.Config.PiecesA, "horse");

            var preview = h.Controller.SubmitPreset(preset);
            Assert.IsTrue(preview.Ok);
            Assert.AreEqual(before, h.Samsara.GetKarma(), "预览阶段绝不能扣业力");
            Assert.AreEqual(horseBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "预览阶段绝不能改配置");
            Assert.IsNotNull(preview.CostInfo);
            Assert.AreEqual(preset.Karma, preview.CostInfo.Cost, "代价应来自预置价目");

            var res = h.Controller.Confirm(preview);
            Assert.IsTrue(res.Applied);
            Assert.AreEqual(before + preset.Karma, h.Samsara.GetKarma(), "确认后应扣费");
            Assert.AreEqual(horseBefore + 1, MoveCount(h.Session.Config.PiecesA, "horse"), "确认后配置生效");

            // 重复施展必须各扣一次（缓存不改成本，UX §1.4 已知风险点）
            var preview2 = h.Controller.SubmitPreset(preset);
            h.Controller.Confirm(preview2);
            Assert.AreEqual(before + preset.Karma * 2, h.Samsara.GetKarma(), "重复施展应再扣一次");
        }

        [Test]
        public void CancelPreview_LeavesEverythingUnchanged()
        {
            using var h = new Harness("xiangqi");
            var preset = FindPreset("让我的马可以横着走");
            var before = h.Samsara.GetKarma();
            var horseBefore = MoveCount(h.Session.Config.PiecesA, "horse");

            h.Controller.SubmitPreset(preset);   // 进预览
            // 玩家取消：什么都不做

            Assert.AreEqual(before, h.Samsara.GetKarma(), "取消后业力不变（未扣费）");
            Assert.AreEqual(horseBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "取消后配置不变");
            Assert.IsFalse(h.Controller.HasRuleUndo, "取消不入改规栈");
        }

        // ── 双撤销栈互不干涉（UX §6.2）─────────────────────────────────

        [Test]
        public void RuleUndo_DoesNotRefundKarma_NorTouchMoveHistory()
        {
            using var h = new Harness("xiangqi");
            var preset = FindPreset("让我的马可以横着走");
            var horseBefore = MoveCount(h.Session.Config.PiecesA, "horse");

            var res = h.Controller.Confirm(h.Controller.SubmitPreset(preset));
            var karmaAfterCheat = h.Samsara.GetKarma();
            Assert.IsTrue(h.Controller.HasRuleUndo, "确认后应入改规栈");
            var movesBefore = h.Session.State.MoveHistory.Count;

            Assert.IsTrue(h.Controller.UndoRule(out _), "应可撤回改规");
            Assert.AreEqual(horseBefore, MoveCount(h.Session.Config.PiecesA, "horse"), "撤回后规则回退");
            Assert.AreEqual(karmaAfterCheat, h.Samsara.GetKarma(), "撤回不返还业力（UX §6.2.6）");
            Assert.AreEqual(movesBefore, h.Session.State.MoveHistory.Count, "撤回改规不回退棋谱");
            Assert.IsFalse(h.Controller.HasRuleUndo, "栈已空");
        }

        [Test]
        public void MismatchWarning_AfterMoveFollowingCheat()
        {
            using var h = new Harness("xiangqi");
            h.Controller.Confirm(h.Controller.SubmitPreset(FindPreset("让我的马可以横着走")));
            Assert.IsFalse(h.Controller.NeedsMismatchWarning, "刚改规未走子，无需警告");

            h.Session.State.MoveHistory.Add(new JObject { ["kind"] = "move" });
            Assert.IsTrue(h.Controller.NeedsMismatchWarning, "改规后走子，撤回前应提示局面可能不一致");
        }

        [Test]
        public void OfflineController_UsesPresetsWithoutLlm()
        {
            using var h = new Harness("xiangqi");   // 空 key ⇒ 离线
            Assert.IsTrue(h.Controller.Offline, "空密钥应进入离线模式");
            Assert.IsTrue(h.Controller.CanSubmit, "开局非 AI 回合，应可提交");
            Assert.IsFalse(h.Controller.AiTurnBlocked, "开局不应被 AI 回合拦截");
        }

        [Test]
        public void IncreaseKarma_Overshoot_TriggersDetectionDelta()
        {
            using var h = new Harness("xiangqi");
            // 把业力推到 110，使 +40 溢出 30（危险档）
            h.Samsara.IncreaseKarma(60);   // 50 → 110
            var preset = FindPreset("让我的马可以横着走");   // cost 40
            var preview = h.Controller.SubmitPreset(preset);
            Assert.AreEqual(CheatTier.Danger, preview.CostInfo.Tier, "110+40 应溢出 30 落入危险档");

            var res = h.Controller.Confirm(preview);
            Assert.AreEqual(30, res.Overshoot, "溢出量 30");
            Assert.Greater(res.DetectionDelta, 0.0, "溢出应产生识破增量");
        }

        // ── 工具 ────────────────────────────────────────────────────────

        static PresetCommand FindPreset(string text)
        {
            foreach (var p in CheatPresetLibrary.All())
                if (p.Text == text) return p;
            throw new System.InvalidOperationException("未找到预置：" + text);
        }

        static int MoveCount(JObject piecesCfg, string type)
        {
            var moves = piecesCfg?["pieces"]?[type]?["moves"] as JArray;
            return moves?.Count ?? -1;
        }
    }
}
