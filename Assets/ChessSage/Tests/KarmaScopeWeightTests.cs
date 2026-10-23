using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E3-C：作用域深度权重（<c>karma = clamp(round(f × σ × surge × discount × scopeWeight) + flat)</c>）。
    ///
    /// 【为什么要这层】167 次真实校准显示 D/B/A/C 的 T_eff 都卡在 7.2–7.9K（3 轮 × ≈2.4K 的结构性下限），
    /// 纯 f(T) 在数学上无法还原四档旧价差。作用域权重是**补丁的机械属性**（target + op + JSON Pointer），
    /// 不依赖意图分类，因此可单测、可回放、可只改 json 重校准。
    ///
    /// 【独立性纪律】期望值全部用测试侧字面量独立推导（f 的锚点值与乘数顺序），不调用被测公式生成期望值。
    /// </summary>
    [TestFixture]
    public sealed class KarmaScopeWeightTests
    {
        // ── 测试替身 ────────────────────────────────────────────────

        sealed class FixedRandom : IRandom
        {
            readonly double _value;
            public FixedRandom(double value) { _value = value; }
            public double NextDouble() => _value;
        }

        sealed class ScriptedRandom : IRandom
        {
            readonly double[] _script;
            int _index;
            public ScriptedRandom(params double[] script) { _script = script; }
            public double NextDouble()
            {
                if (_index >= _script.Length)
                    throw new InvalidOperationException($"脚本随机源耗尽（已取 {_index} 次）");
                return _script[_index++];
            }
        }

        /// <summary>任何抽取即抛错——断言「该路径不得消耗随机源 / 不得受作用域影响」。</summary>
        sealed class ExplodingRandom : IRandom
        {
            public double NextDouble() => throw new InvalidOperationException("该路径不得消耗随机源");
        }

        static KarmaFormula Formula() => new KarmaFormula(
            KarmaCurveConfig.Default, FluctuationConfig.Default, chatCap: 5, scopeWeights: ScopeWeightsConfig.Default);

        static KarmaContext Ctx(params string[] scopes) => new KarmaContext
        {
            Realm = "human",
            SingleMax = 120,
            ScopeTags = scopes,
        };

        // ════════════════════════════════════════════════════════════
        // ① 权重表（合同默认值 = E3-C 实测反推）
        // ════════════════════════════════════════════════════════════

        [TestCase(ScopeWeightsConfig.UiConfig, 1.0)]
        [TestCase(ScopeWeightsConfig.BoardState, 2.9)]
        [TestCase(ScopeWeightsConfig.Pieces, 1.85)]
        [TestCase(ScopeWeightsConfig.Rules, 3.35)]
        [TestCase(ScopeWeightsConfig.Objectives, 3.35)]
        public void Defaults_MatchCalibrationTable(string scope, double expected)
        {
            Assert.AreEqual(expected, ScopeWeightsConfig.Default.Get(scope), 1e-12, $"{scope} 默认权重");
        }

        [Test]
        public void Defaults_UnknownOrEmptyScope_IsNeutral()
        {
            Assert.AreEqual(1.0, ScopeWeightsConfig.Default.Get(null), 1e-12);
            Assert.AreEqual(1.0, ScopeWeightsConfig.Default.Get(string.Empty), 1e-12);
            Assert.AreEqual(1.0, ScopeWeightsConfig.Default.Get("weapon_config"), 1e-12, "未知作用域不得静默涨价");
        }

        [Test]
        public void Max_TakesLargest_FirstWinsTies()
        {
            string dominant;
            Assert.AreEqual(3.35, ScopeWeightsConfig.Default.Max(
                new[] { ScopeWeightsConfig.Pieces, ScopeWeightsConfig.Rules }, out dominant), 1e-12);
            Assert.AreEqual(ScopeWeightsConfig.Rules, dominant);

            // 并列（rules / objectives 同为 3.35）⇒ 先出现者胜，保证回放确定性。
            var tie = ScopeWeightsConfig.Default.Max(
                new[] { ScopeWeightsConfig.Objectives, ScopeWeightsConfig.Rules }, out dominant);
            Assert.AreEqual(3.35, tie, 1e-12);
            Assert.AreEqual(ScopeWeightsConfig.Objectives, dominant);

            Assert.AreEqual(1.0, ScopeWeightsConfig.Default.Max(null, out dominant), 1e-12);
            Assert.IsNull(dominant, "无作用域 ⇒ 无主导作用域");
            Assert.AreEqual(1.0, ScopeWeightsConfig.Default.Max(new string[0], out dominant), 1e-12);
            Assert.IsNull(dominant);
        }

        [Test]
        public void ScopeWeights_RejectsInvalidValues()
        {
            var missing = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                { ScopeWeightsConfig.UiConfig, 1.0 },
            };
            Assert.Throws<ArgumentException>(() => new ScopeWeightsConfig(missing), "缺作用域必须拒绝（防静默默认）");

            foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, 101.0 })
            {
                var table = BuildTable(ScopeWeightsConfig.Rules, bad);
                Assert.Throws<ArgumentException>(() => new ScopeWeightsConfig(table), $"rules={bad} 应被拒绝");
            }
            Assert.DoesNotThrow(() => new ScopeWeightsConfig(BuildTable(ScopeWeightsConfig.Rules, 4.0)));
        }

        static Dictionary<string, double> BuildTable(string scope, double value)
        {
            var table = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var s in ScopeWeightsConfig.ScopeOrder)
                table[s] = s == scope ? value : ScopeWeightsConfig.Default.Get(s);
            return table;
        }

        // ════════════════════════════════════════════════════════════
        // ② 作用域映射（apply_config_patch 的 JSON Pointer 前缀 + op 形状）
        // ════════════════════════════════════════════════════════════

        [TestCase("ui_config", "replace", "/theme/board/background_color", ScopeWeightsConfig.UiConfig)]
        [TestCase("ui_config", "add", "/theme/pieces/red_color", ScopeWeightsConfig.UiConfig)]
        [TestCase("board", "replace", "/width", ScopeWeightsConfig.UiConfig)]
        [TestCase("board", "replace", "/geometry/height", ScopeWeightsConfig.UiConfig)]
        [TestCase("rules", "replace", "/ai_difficulty/personality", ScopeWeightsConfig.Rules)]
        [TestCase("board_state", "add", "/mechanisms/skip_turns/-", ScopeWeightsConfig.Rules)]
        [TestCase("board_state", "replace", "/mechanisms/ai_control", ScopeWeightsConfig.Rules)]
        [TestCase("board_state", "replace", "/pieces/0/position", ScopeWeightsConfig.BoardState)]
        [TestCase("board_state", "replace", "/game_status/state", ScopeWeightsConfig.BoardState)]
        [TestCase("objective", "replace", "/type", ScopeWeightsConfig.Objectives)]
        public void ForPatch_MapsTargetAndPointer(string target, string op, string pointer, string expected)
        {
            Assert.AreEqual(expected, AgentScope.ForPatch(target, op, pointer),
                $"{target} {op} {pointer} 应归入 {expected}");
        }

        [TestCase("add", "/pieces/bender", ScopeWeightsConfig.Pieces)]      // 铸子：新增棋子类型
        [TestCase("add", "/pieces/-", ScopeWeightsConfig.Rules)]            // 数组追加不是新类型（保守）
        [TestCase("add", "/pieces/horse/moves/-", ScopeWeightsConfig.Rules)] // 改规：改既有棋子走法
        [TestCase("add", "/pieces/horse/moves/1", ScopeWeightsConfig.Rules)]
        [TestCase("replace", "/pieces/elephant/moves", ScopeWeightsConfig.Rules)]
        [TestCase("add", "/pieces/bender/moves/-", ScopeWeightsConfig.Rules)]
        public void ForPatch_DistinguishesCastFromRuleEdit(string op, string pointer, string expected)
        {
            foreach (var target in new[] { "pieces_red", "pieces_black", "pieces_white" })
                Assert.AreEqual(expected, AgentScope.ForPatch(target, op, pointer),
                    $"{target} {op} {pointer}：铸子(pieces) 与 改规(rules) 必须可区分");
        }

        [Test]
        public void ForPatch_UnknownTarget_IsNeutral()
        {
            Assert.IsNull(AgentScope.ForPatch("weapon_config", "add", "/weapons/-"));
            Assert.IsNull(AgentScope.ForPatch(null, "add", "/x"));
        }

        [Test]
        public void ForSlice_MapsSandboxKeys()
        {
            Assert.AreEqual(ScopeWeightsConfig.BoardState, AgentScope.ForSlice("board_state"));
            Assert.AreEqual(ScopeWeightsConfig.UiConfig, AgentScope.ForSlice("ui_config"));
            Assert.AreEqual(ScopeWeightsConfig.UiConfig, AgentScope.ForSlice("board"));
            Assert.AreEqual(ScopeWeightsConfig.Rules, AgentScope.ForSlice("rules"));
            Assert.AreEqual(ScopeWeightsConfig.Pieces, AgentScope.ForSlice("pieces_red"));
            Assert.AreEqual(ScopeWeightsConfig.Pieces, AgentScope.ForSlice("pieces_black"));
            Assert.AreEqual("objectives", AgentScope.ForSlice("objective"));
            Assert.IsNull(AgentScope.ForSlice("weapon_config"));
        }

        // ════════════════════════════════════════════════════════════
        // ③ 权重应用（顺序 / max / clamp / 零写入隔离）
        // ════════════════════════════════════════════════════════════

        [TestCase(ScopeWeightsConfig.UiConfig, 16)]
        [TestCase(ScopeWeightsConfig.Pieces, 30)]      // round(16×1.85)=round(29.6)=30
        [TestCase(ScopeWeightsConfig.BoardState, 46)]  // round(16×2.9)=round(46.4)=46
        [TestCase(ScopeWeightsConfig.Rules, 54)]       // round(16×3.35)=round(53.6)=54
        public void Compute_AppliesScopeWeight_AtUiAnchor(string scope, int expected)
        {
            // T=8000 ⇒ f=16（锚点精确值）；σ=1.0（u=0.4，且 0.4≥0.3 不触发激增）。
            var result = Formula().Compute(8000, writes: 1, success: true, ctx: Ctx(scope),
                rng: new FixedRandom(0.4));
            Assert.AreEqual(expected, result.Karma, $"{scope} 权重未按合同生效");
            Assert.AreEqual(ScopeWeightsConfig.Default.Get(scope), result.ScopeWeight, 1e-12);
            Assert.AreEqual(scope, result.DominantScope);
        }

        [Test]
        public void Compute_MultipliesAfterDiscount_BeforeFlat()
        {
            // 正确顺序：round(f × roll × discount × scope) + flat
            //   f=16, σ=1, 神思省力触发 ×0.7, scope=rules ×3.35 ⇒ round(37.52)=38，+20 = 58
            // 错误顺序（先加 flat 再乘）：(round(16×0.7)+20)×3.35 ≈ 103.85 ⇒ 104 —— 必须区分开。
            var ctx = Ctx(ScopeWeightsConfig.Rules);
            ctx.Flat = 20;
            ctx.EfficiencyFraud = true;
            // 抽取序：σ、fraud 触发签、surge（T<20K 不抽）⇒ 0.4 / 0.0
            var result = Formula().Compute(8000, 1, true, ctx, new ScriptedRandom(0.4, 0.0));
            Assert.AreEqual(0.7, result.Discount, 1e-12);
            Assert.AreEqual(58, result.Karma, "作用域乘数必须位于 discount 之后、flat 之前");

            var wrongOrder = (int)Math.Round(16 * 0.7, MidpointRounding.AwayFromZero) + 20;
            wrongOrder = (int)Math.Round(wrongOrder * 3.35, MidpointRounding.AwayFromZero);
            Assert.AreNotEqual(wrongOrder, result.Karma, "测试必须能区分乘数顺序");
        }

        [Test]
        public void Compute_UsesMaxAcrossScopes_NotSumOrProduct()
        {
            var max = Formula().Compute(8000, 1, true,
                Ctx(ScopeWeightsConfig.UiConfig, ScopeWeightsConfig.Pieces, ScopeWeightsConfig.Rules),
                new FixedRandom(0.4));
            Assert.AreEqual(3.35, max.ScopeWeight, 1e-12, "取最大值，不是加和/连乘");
            Assert.AreEqual(54, max.Karma);
            Assert.AreEqual(ScopeWeightsConfig.Rules, max.DominantScope);
        }

        [Test]
        public void Compute_EmptyOrNullScopes_AreNeutral_GoldenAnchorsUnchanged()
        {
            foreach (var ctx in new[] { Ctx(), new KarmaContext { SingleMax = 120, ScopeTags = null } })
            {
                Assert.AreEqual(1.0, Formula().Compute(8000, 1, true, ctx, new FixedRandom(0.4)).ScopeWeight, 1e-12);
                Assert.AreEqual(16, Formula().Compute(8000, 1, true, ctx, new FixedRandom(0.4)).Karma,
                    "无作用域 ⇒ 旧黄金值必须逐值不变");
                Assert.AreEqual(55, Formula().Compute(16000, 1, true, ctx, new FixedRandom(0.4)).Karma);
            }
        }

        [Test]
        public void Compute_ClampsAfterScopeWeight()
        {
            // f(16000)=55 × 3.35 = 184.25 ⇒ 184 ⇒ clamp 120（不能因权重绕过单次上限）。
            var result = Formula().Compute(16000, 1, true, Ctx(ScopeWeightsConfig.Rules), new FixedRandom(0.4));
            Assert.AreEqual(120, result.Karma);
            Assert.AreEqual(3.35, result.ScopeWeight, 1e-12);
        }

        [Test]
        public void Compute_ZeroWrite_IgnoresScope_AndConsumesNoRandomness()
        {
            var result = Formula().Compute(16000, writes: 0, success: true,
                ctx: Ctx(ScopeWeightsConfig.Rules), rng: new ExplodingRandom());
            Assert.AreEqual(1, result.Karma, "零写入恒 1：作用域不得改变 E 档价");
            Assert.IsTrue(result.ZeroWrite);
            Assert.AreEqual(1.0, result.ScopeWeight, 1e-12, "零写入路径应如实报告未应用作用域");
            Assert.IsNull(result.DominantScope);

            var failure = Formula().Compute(16000, writes: 0, success: false,
                ctx: Ctx(ScopeWeightsConfig.Rules), rng: new ExplodingRandom());
            Assert.AreEqual(0, failure.Karma, "失败卡恒 0：作用域同样无效");
        }

        [Test]
        public void ProjectDisplay_CanIncludeScopeWeight()
        {
            Assert.AreEqual(16, Formula().ProjectDisplay(8000), "默认口径（scope=1）保持旧行为");
            Assert.AreEqual(54, Formula().ProjectDisplay(8000, 3.35));
            Assert.AreEqual(16, Formula().ProjectDisplay(8000, 0.0), "非法权重回退中性 1.0");
        }

        // ════════════════════════════════════════════════════════════
        // ④ json 参数化（校准只改 json、不动代码）
        // ════════════════════════════════════════════════════════════

        [Test]
        public void Config_ParsesScopeWeights_AndMergesDefaults()
        {
            var config = AgentRemakeConfig.FromJson(JObject.Parse(
                "{\"scope_weights\":{\"rules\":4.0}}"));
            Assert.AreEqual(4.0, config.ScopeWeights.Get(ScopeWeightsConfig.Rules), 1e-12);
            Assert.AreEqual(2.9, config.ScopeWeights.Get(ScopeWeightsConfig.BoardState), 1e-12,
                "缺键取合同默认值");
            Assert.IsEmpty(config.Warnings);

            var formula = config.CreateFormula();
            // round(16×4.0)=64
            Assert.AreEqual(64, formula.Compute(8000, 1, true, Ctx(ScopeWeightsConfig.Rules), new FixedRandom(0.4)).Karma);
        }

        [TestCase("{\"scope_weights\":{\"rules\":-1}}", "作用域权重")]
        [TestCase("{\"scope_weights\":{\"weapon\":2}}", "作用域权重")]
        [TestCase("{\"scope_weights\":3}", "作用域权重")]
        public void Config_InvalidScopeWeights_FallsBackWholeSection(string json, string warningHint)
        {
            var config = AgentRemakeConfig.FromJson(JObject.Parse(json));
            Assert.AreEqual(3.35, config.ScopeWeights.Get(ScopeWeightsConfig.Rules), 1e-12,
                "非法段必须整段回退默认权重");
            Assert.IsTrue(System.Linq.Enumerable.Any(config.Warnings, w => w.Contains(warningHint)),
                "回退必须留告警（兜底不等于静默）");
        }

        // ════════════════════════════════════════════════════════════
        // ⑤ 引擎累计与遥测（成功写入的作用域上限 → 结算）
        // ════════════════════════════════════════════════════════════

        static JObject SampleConfigs() => JObject.Parse(@"{
              ""board_state"": {
                ""pieces"": [{""id"": ""r_chariot_1"", ""type"": ""chariot"", ""name"": ""车"", ""side"": ""red"", ""position"": [0, 9], ""is_alive"": true}],
                ""current_turn"": ""red"", ""move_history"": [], ""game_status"": {""state"": ""playing""},
                ""mechanisms"": {""skip_turns"": [], ""ai_control"": []}
              },
              ""board"": {""width"": 9, ""height"": 10},
              ""rules"": {""ai_difficulty"": {""personality"": ""calm""}},
              ""ui_config"": {""theme"": ""dark""},
              ""pieces_red"": {""pieces"": {""horse"": {""label"": ""馬"", ""moves"": []}}}
            }");

        static AgentEngine NewEngine(LlmTransport transport)
        {
            var client = new LlmClient(apiKey: "k", baseUrl: "https://mock.invalid/v1", model: "deepseek-flash");
            client.MockToolHandler = transport;
            var engine = new AgentEngine(client, config: null, sessionSeed: 20260101);
            engine.ConfigValidator = (name, cfg) => (true, (string)null);
            engine.ObjectiveTypesProvider = () => new[] { "checkmate", "capture_all" };
            engine.SyncKarmaState(new AgentKarmaState
            {
                Karma = 50, KarmaMax = 120, SingleMax = 120, Detection = 0.0, Realm = "human",
            });
            return engine;
        }

        static LlmRawResponse ToolRound(string name, string args)
            => new LlmRawResponse
            {
                FinishReason = "tool_calls",
                PromptTokens = 500,
                CompletionTokens = 100,
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = "c1", Name = name, ArgumentsJson = args } },
            };

        [Test]
        public void Engine_AccumulatesSuccessfulWriteScopes_AndExposesDominant()
        {
            TestEnv.RunSync(async () =>
            {
                var rounds = new[]
                {
                    // 铸子（pieces）
                    ToolRound("apply_config_patch",
                        "{\"operations\":[{\"target\":\"pieces_red\",\"patch\":[{\"op\":\"add\",\"path\":\"/pieces/bender\",\"value\":{\"label\":\"拐\",\"moves\":[]}}]}]}"),
                    // 改规（rules）——权重更大，应成为主导作用域
                    ToolRound("apply_config_patch",
                        "{\"operations\":[{\"target\":\"pieces_red\",\"patch\":[{\"op\":\"add\",\"path\":\"/pieces/horse/moves/-\",\"value\":{\"kind\":\"jump\",\"to\":[1,0],\"block\":[],\"land\":\"any\",\"sym\":\"rotate4\"}}]}]}"),
                    new LlmRawResponse { FinishReason = "stop", Content = "改好了", PromptTokens = 100, CompletionTokens = 20 },
                };
                var i = 0;
                var engine = NewEngine(req => Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]));

                await engine.ProcessCommandAsync("铸子并改规", new JObject { ["configs"] = SampleConfigs() },
                    CancellationToken.None);

                var result = engine.LastResult;
                CollectionAssert.AreEquivalent(new[] { ScopeWeightsConfig.Pieces, ScopeWeightsConfig.Rules },
                    result.ScopeTags, "两次成功写入的作用域都应被记录");
                Assert.AreEqual(ScopeWeightsConfig.Rules, result.Scope, "主导作用域 = 权重最大者");
                Assert.AreEqual(3.35, result.ScopeWeight, 1e-12);
                Assert.AreEqual(result.Karma.ScopeWeight, result.ScopeWeight, 1e-12);
                Assert.AreEqual("rules", result.ToResultJson().Value<string>("scope"), "结果契约应暴露 scope（校准审计）");
                Assert.AreEqual(2, result.Writes, "作用域记录不得改变写入计数");

                var record = engine.Ledger.Requests[engine.Ledger.Requests.Count - 1];
                Assert.AreEqual("rules", record.Scope);
                Assert.AreEqual(3.35, record.ScopeWeight, 1e-12);
                CollectionAssert.AreEquivalent(new[] { "pieces", "rules" }, record.Scopes);
            });
        }

        [Test]
        public void Engine_ReadOnlyRun_HasNoScopeAndNeutralWeight()
        {
            TestEnv.RunSync(async () =>
            {
                var rounds = new[]
                {
                    ToolRound("get_board_state", "{}"),
                    new LlmRawResponse { FinishReason = "stop", Content = "看完了", PromptTokens = 100, CompletionTokens = 20 },
                };
                var i = 0;
                var engine = NewEngine(req => Task.FromResult(rounds[Math.Min(i++, rounds.Length - 1)]));

                await engine.ProcessCommandAsync("看看局势", new JObject { ["configs"] = SampleConfigs() },
                    CancellationToken.None);

                var result = engine.LastResult;
                Assert.AreEqual("fun", result.Type);
                Assert.IsNull(result.Scope, "零写入不得产生作用域");
                Assert.AreEqual(1.0, result.ScopeWeight, 1e-12);
                Assert.AreEqual(1, result.EstimatedKarmaCost, "E 档恒 1 不作用域影响");
                Assert.IsFalse(result.ToResultJson().ContainsKey("scope"), "无作用域时不写 scope 键");
            });
        }
    }
}
