using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ChessSage.Core.Agent;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E0-S5：agent_remake.json 参数化 + 加载器兜底（校准只改 json、不动代码）。
    ///
    /// 【独立推导原则】期望值一律用测试侧字面量独立推导，不调用被测实现：
    ///   - 合同默认值 = 裁决表（2026-10-23）：六锚点 2500/1 → 21000/91、floor 2500、
    ///     extend_last_slope、min 1、max 120；σ ~ U(0.8,1.3)；surge 20K/0.30/[1.25,1.5]；
    ///     chat_cap 5；limits 4/3/3/60000/24000/20；performance M2/maximize/A/2048/4096；
    ///     realm flat animal.high_rank_piece = 20。
    ///   - 重锚曲线 [[1000,5],[2000,10]]、floor 1000、不延伸 ⇒ f(999)=1.0 / f(1000)=5 /
    ///     f(1500)=7.5 / f(2000)=10 / f(2500)=10；CapToken(10)=2000、CapToken(11)=int.MaxValue。
    /// 【隔离】SetUp 清空两个环境变量、TearDown 还原；文件全部落临时目录并清理。
    /// </summary>
    [TestFixture]
    public sealed class AgentConfigLoaderTests
    {
        // ───────────────────────────────────────────────
        // 一、内置合同（AgentRemakeConfig.Default）
        // ───────────────────────────────────────────────

        [Test]
        public void Default_AllSections_MatchContractTable()
        {
            var c = AgentRemakeConfig.Default;
            AssertContractValues(c, "内置默认");
            Assert.AreEqual("builtin", c.Source, "内置默认的 Source 必须标记 builtin");
            Assert.That(c.Warnings, Is.Empty, "内置默认不得带告警");
        }

        [Test]
        public void Default_CreateFormula_ReproducesAnchorGolden()
        {
            var f = AgentRemakeConfig.Default.CreateFormula();
            Assert.That(f.CurveValue(2500), Is.EqualTo(1.0).Within(1e-9));
            Assert.That(f.CurveValue(8000), Is.EqualTo(16.0).Within(1e-9));
            Assert.That(f.CurveValue(12000), Is.EqualTo(32.0).Within(1e-9));
            Assert.That(f.CurveValue(15000), Is.EqualTo(48.0).Within(1e-9));
            Assert.That(f.CurveValue(16000), Is.EqualTo(55.0).Within(1e-9));
            Assert.That(f.CurveValue(21000), Is.EqualTo(91.0).Within(1e-9));
            // 末段延伸 0.0072/token：25K ⇒ 91 + 4000×0.0072 = 119.8（投射 120，即触顶）
            Assert.That(f.CurveValue(25000), Is.EqualTo(119.8).Within(1e-9));
            Assert.AreEqual(120, f.ProjectDisplay(25000));
            // 反查触顶：120 ⇒ 25028；150 ⇒ 29195；160 ⇒ 30584（设计 §1.5 已复算签署）
            Assert.AreEqual(25028, f.ProjectCapToken(120));
            Assert.AreEqual(29195, f.ProjectCapToken(150));
            Assert.AreEqual(30584, f.ProjectCapToken(160));
        }

        [Test]
        public void Default_GetRealmFlat_ContractAndMisses()
        {
            var c = AgentRemakeConfig.Default;
            Assert.AreEqual(20, c.GetRealmFlat("animal", "high_rank_piece"));
            Assert.AreEqual(0, c.GetRealmFlat("animal", "unknown_condition"));
            Assert.AreEqual(0, c.GetRealmFlat("hell", "gate"));
            Assert.AreEqual(0, c.GetRealmFlat(null, "high_rank_piece"));
            Assert.AreEqual(0, c.GetRealmFlat("animal", null));
        }

        // ───────────────────────────────────────────────
        // 二、FromJson 逐键默认（缺键静默取合同值）
        // ───────────────────────────────────────────────

        [Test]
        public void FromJson_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => AgentRemakeConfig.FromJson(null));
        }

        [Test]
        public void FromJson_EmptyObject_AllSectionDefaults_NoWarnings()
        {
            var c = AgentRemakeConfig.FromJson(new JObject());
            AssertContractValues(c, "空对象");
            Assert.That(c.Warnings, Is.Empty, "整段缺失是合法默认，不应告警");
        }

        [Test]
        public void FromJson_NullSections_AllSectionDefaults_NoWarnings()
        {
            var root = JObject.Parse(@"{ ""karma_curve"": null, ""fluctuation"": null, ""chat_cap"": null,
                ""limits"": null, ""performance"": null, ""realm_karma_flat"": null }");
            var c = AgentRemakeConfig.FromJson(root);
            AssertContractValues(c, "显式 null 段");
            Assert.That(c.Warnings, Is.Empty, "JSON null 段等同于缺省，不应告警");
        }

        [Test]
        public void FromJson_PerKeyDefaults_WhenKeysAbsent()
        {
            var root = JObject.Parse(@"{ ""karma_curve"": { ""anchors"": [[2500, 1], [8000, 16]] },
                ""fluctuation"": { ""uniform"": [0.9, 1.1] },
                ""limits"": { ""max_rounds"": 2 } }");
            var c = AgentRemakeConfig.FromJson(root);

            Assert.AreEqual(2, c.KarmaCurve.Anchors.Count, "anchors 应取配置值");
            Assert.AreEqual(2500, c.KarmaCurve.Anchors[0].Token);
            Assert.AreEqual(1, c.KarmaCurve.Anchors[0].Karma);
            Assert.AreEqual(2500, c.KarmaCurve.FloorToken, "floor_token 缺省 ⇒ 合同 2500");
            Assert.IsTrue(c.KarmaCurve.ExtendLastSlope, "extend 缺省 ⇒ true");
            Assert.AreEqual(1, c.KarmaCurve.Min);
            Assert.AreEqual(120, c.KarmaCurve.Max);

            Assert.That(c.Fluctuation.UniformMin, Is.EqualTo(0.9).Within(1e-12));
            Assert.That(c.Fluctuation.UniformMax, Is.EqualTo(1.1).Within(1e-12));
            Assert.AreEqual(20000, c.Fluctuation.SurgeTokenThreshold, "surge 缺省 ⇒ 合同 20000");
            Assert.That(c.Fluctuation.SurgeProbability, Is.EqualTo(0.30).Within(1e-12));
            Assert.That(c.Fluctuation.SurgeMin, Is.EqualTo(1.25).Within(1e-12));
            Assert.That(c.Fluctuation.SurgeMax, Is.EqualTo(1.5).Within(1e-12));

            Assert.AreEqual(2, c.Limits.MaxRounds, "max_rounds 取配置值");
            Assert.AreEqual(3, c.Limits.RepeatToolCap, "其余 limits 键缺省 ⇒ 合同值");
            Assert.AreEqual(60000, c.Limits.TokenHardStop);
            Assert.AreEqual(24000, c.Limits.ContextSoftCap);
            Assert.AreEqual(20, c.Limits.HardTimeoutS);

            Assert.That(c.Warnings, Is.Empty, "逐键默认不应告警");
        }

        // ───────────────────────────────────────────────
        // 三、参数化黄金（改 json 后 f 输出变、代码零改）
        // ───────────────────────────────────────────────

        [Test]
        public void FromJson_ReAnchoredCurve_ChangesOutput()
        {
            var root = JObject.Parse(@"{ ""karma_curve"": {
                ""anchors"": [[1000, 5], [2000, 10]], ""floor_token"": 1000,
                ""extend_last_slope"": false, ""min"": 1, ""max"": 50 } }");
            var f = AgentRemakeConfig.FromJson(root).CreateFormula();

            Assert.That(f.CurveValue(999), Is.EqualTo(1.0).Within(1e-9), "T < floor ⇒ 平地价 1.0");
            Assert.That(f.CurveValue(1000), Is.EqualTo(5.0).Within(1e-9), "首锚常数");
            Assert.That(f.CurveValue(1500), Is.EqualTo(7.5).Within(1e-9), "段间线性 5 + 5×(500/1000)");
            Assert.That(f.CurveValue(2000), Is.EqualTo(10.0).Within(1e-9), "末锚");
            Assert.That(f.CurveValue(2500), Is.EqualTo(10.0).Within(1e-9), "不延伸 ⇒ 末锚常数");
            Assert.AreEqual(2000, f.ProjectCapToken(10), "恰好末锚");
            Assert.AreEqual(int.MaxValue, f.ProjectCapToken(11), "超出末锚且不延伸 ⇒ 永不触顶哨兵");
        }

        [Test]
        public void FromJson_FluctuationOverride_DeterministicRoll()
        {
            // 重锚曲线 + σ 钉 1.0 + 激增概率 0 ⇒ Compute 完全确定：f(1500)=7.5 ⇒ round=8
            var noSurge = JObject.Parse(@"{ ""karma_curve"": {
                    ""anchors"": [[1000, 5], [2000, 10]], ""floor_token"": 1000, ""extend_last_slope"": false },
                ""fluctuation"": { ""uniform"": [1.0, 1.0], ""surge"": { ""token_threshold"": 20000,
                    ""probability"": 0.0, ""range"": [1.25, 1.5] } } }");
            var f1 = AgentRemakeConfig.FromJson(noSurge).CreateFormula();
            var r1 = f1.Compute(1500, writes: 2, success: true, ctx: Ctx(singleMax: 50), rng: new FixedRandom(0.1));
            Assert.AreEqual(8, r1.Karma, "7.5 × σ1.0 × 无激增 ⇒ AwayFromZero 舍入 8");

            // 合同曲线 + σ 钉 1.0 + 激增概率 0 ⇒ f(16000)=55 恒得 55
            var contractNoSurge = JObject.Parse(@"{ ""fluctuation"": {
                ""uniform"": [1.0, 1.0], ""surge"": { ""token_threshold"": 20000, ""probability"": 0.0 } } }");
            var f2 = AgentRemakeConfig.FromJson(contractNoSurge).CreateFormula();
            var r2 = f2.Compute(16000, writes: 3, success: true, ctx: Ctx(), rng: new FixedRandom(0.1));
            Assert.AreEqual(55, r2.Karma, "合同曲线 16K 锚点在无波动下应精确产出 55");

            // 重锚曲线 + 阈值放到 100 + p=0.5 + surge 钉 2.0 ⇒ 7.5 × 1.0 × 2.0 = 15
            var surged = JObject.Parse(@"{ ""karma_curve"": {
                    ""anchors"": [[1000, 5], [2000, 10]], ""floor_token"": 1000, ""extend_last_slope"": false },
                ""fluctuation"": { ""uniform"": [1.0, 1.0], ""surge"": { ""token_threshold"": 100,
                    ""probability"": 0.5, ""range"": [2.0, 2.0] } } }");
            var f3 = AgentRemakeConfig.FromJson(surged).CreateFormula();
            var r3 = f3.Compute(1500, writes: 2, success: true, ctx: Ctx(singleMax: 50), rng: new FixedRandom(0.1));
            Assert.AreEqual(15, r3.Karma, "7.5 × σ1.0 × surge2.0 = 15");
            Assert.IsTrue(r3.Surged, "阈值 100 且 u=0.1 < p=0.5 必须触发激增");
        }

        [Test]
        public void FromJson_ChatCapOverride_ZeroWriteFuse()
        {
            var zero = AgentRemakeConfig.FromJson(JObject.Parse(@"{ ""chat_cap"": 0 }"));
            Assert.AreEqual(0, zero.ChatCap);
            Assert.AreEqual(0, zero.CreateFormula().Compute(100, writes: 0, success: true, ctx: Ctx(), rng: new ExplodingRandom()).Karma,
                "chat_cap=0 时零写入成功路径保险丝生效 ⇒ 0");

            var seven = AgentRemakeConfig.FromJson(JObject.Parse(@"{ ""chat_cap"": 7 }"));
            Assert.AreEqual(1, seven.CreateFormula().Compute(100, writes: 0, success: true, ctx: Ctx(), rng: new ExplodingRandom()).Karma,
                "chat_cap=7 时恒 1 规则仍封顶 1");

            var d = AgentRemakeConfig.Default;
            Assert.AreEqual(1, d.CreateFormula().Compute(100, writes: 0, success: true, ctx: Ctx(), rng: new ExplodingRandom()).Karma,
                "合同 chat_cap=5：零写入成功恒 1");
            Assert.AreEqual(0, d.CreateFormula().Compute(100, writes: 0, success: false, ctx: Ctx(), rng: new ExplodingRandom()).Karma,
                "零写入失败 ⇒ 0（失败卡不收费）");
        }

        // ───────────────────────────────────────────────
        // 四、段级校验失败 ⇒ 整段回退默认 + 告警（其余段保持）
        // ───────────────────────────────────────────────

        [Test]
        public void FromJson_BadAnchors_SectionFallsBack_Warning()
        {
            var root = JObject.Parse(@"{ ""karma_curve"": { ""anchors"": [[2000, 10], [1000, 5]] }, ""chat_cap"": 7 }");
            var c = AgentRemakeConfig.FromJson(root);
            AssertContractKarmaCurve(c, "锚点倒挂应回退六档");
            Assert.AreEqual(7, c.ChatCap, "未损坏的 chat_cap 段必须保留");
            Assert.That(HasWarning(c, "karma_curve"), Is.True, "必须留下 karma_curve 段回退告警");
        }

        [Test]
        public void FromJson_AnchorsNotPairs_Warns()
        {
            var root = JObject.Parse(@"{ ""karma_curve"": { ""anchors"": [[1000], [2000, 10]] } }");
            var c = AgentRemakeConfig.FromJson(root);
            AssertContractKarmaCurve(c, "非二元组锚点应回退");
            Assert.That(HasWarning(c, "karma_curve"), Is.True);
        }

        [Test]
        public void FromJson_AnchorsFloatToken_Warns()
        {
            var root = JObject.Parse(@"{ ""karma_curve"": { ""anchors"": [[1000.5, 5], [2000, 10]] } }");
            var c = AgentRemakeConfig.FromJson(root);
            AssertContractKarmaCurve(c, "小数 token 应回退");
            Assert.That(HasWarning(c, "karma_curve"), Is.True);
        }

        [Test]
        public void FromJson_SectionWrongType_Warns()
        {
            var root = JObject.Parse(@"{ ""karma_curve"": ""oops"" }");
            var c = AgentRemakeConfig.FromJson(root);
            AssertContractKarmaCurve(c, "段类型错误应回退");
            Assert.That(HasWarning(c, "karma_curve"), Is.True);
        }

        [Test]
        public void FromJson_UniformNotPair_Warns()
        {
            var root = JObject.Parse(@"{ ""fluctuation"": { ""uniform"": [0.8] } }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.That(c.Fluctuation.UniformMin, Is.EqualTo(0.8).Within(1e-12), "uniform 非法 ⇒ fluctuation 整段回退");
            Assert.That(HasWarning(c, "fluctuation"), Is.True);
        }

        [Test]
        public void FromJson_UniformReversed_CtorRejects_Warns()
        {
            var root = JObject.Parse(@"{ ""fluctuation"": { ""uniform"": [1.3, 0.8] } }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.That(c.Fluctuation.UniformMin, Is.EqualTo(0.8).Within(1e-12));
            Assert.That(c.Fluctuation.UniformMax, Is.EqualTo(1.3).Within(1e-12));
            Assert.That(HasWarning(c, "fluctuation"), Is.True, "区间倒挂须被构造校验拒绝并回退");
        }

        [Test]
        public void FromJson_ChatCapWrongType_Warns_KeepsDefault()
        {
            var root = JObject.Parse(@"{ ""chat_cap"": ""five"" }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.AreEqual(5, c.ChatCap, "类型错误 ⇒ 回退合同 5");
            Assert.That(HasWarning(c, "chat_cap"), Is.True);
        }

        [Test]
        public void FromJson_LimitsInvalid_SectionFallback()
        {
            var root = JObject.Parse(@"{ ""limits"": { ""max_rounds"": 0, ""hard_timeout_s"": 99 } }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.AreEqual(4, c.Limits.MaxRounds, "max_rounds=0 非法 ⇒ limits 整段回退");
            Assert.AreEqual(20, c.Limits.HardTimeoutS, "同段的合法键也随段回退（原子语义）");
            Assert.That(HasWarning(c, "limits"), Is.True);
        }

        [Test]
        public void FromJson_PerformanceUnknownEnum_SectionFallback()
        {
            var root = JObject.Parse(@"{ ""performance"": { ""effort_profile"": ""M9"", ""prefix_cache"": ""maximize"" } }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.AreEqual("M2", c.Performance.EffortProfile, "未知档位 ⇒ performance 整段回退");
            Assert.AreEqual(2048, c.Performance.ToolRoundMaxTokens);
            Assert.That(HasWarning(c, "performance"), Is.True);
        }

        [Test]
        public void FromJson_PerformanceCaseNormalized()
        {
            var root = JObject.Parse(@"{ ""performance"": { ""effort_profile"": ""m2"", ""prefix_cache"": ""MAXIMIZE"",
                ""timeout_action"": ""a"", ""per_round_max_tokens"": { ""tool"": 1024, ""final"": 8192 } } }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.AreEqual("M2", c.Performance.EffortProfile, "大小写归一");
            Assert.AreEqual("maximize", c.Performance.PrefixCache);
            Assert.AreEqual("A", c.Performance.TimeoutAction);
            Assert.AreEqual(1024, c.Performance.ToolRoundMaxTokens);
            Assert.AreEqual(8192, c.Performance.FinalRoundMaxTokens);
            Assert.That(c.Warnings, Is.Empty, "合法大小写变体不得告警");
        }

        [Test]
        public void FromJson_RealmFlat_Negative_SectionFallback()
        {
            var root = JObject.Parse(@"{ ""realm_karma_flat"": { ""animal"": { ""high_rank_piece"": -5 } } }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.AreEqual(20, c.GetRealmFlat("animal", "high_rank_piece"), "负 flat ⇒ 整段回退默认");
            Assert.That(HasWarning(c, "realm_karma_flat"), Is.True);
        }

        [Test]
        public void FromJson_RealmFlat_ExtraEntries()
        {
            var root = JObject.Parse(@"{ ""realm_karma_flat"": { ""animal"": { ""high_rank_piece"": 20 },
                ""hell"": { ""gate"": 7 } } }");
            var c = AgentRemakeConfig.FromJson(root);
            Assert.AreEqual(20, c.GetRealmFlat("animal", "high_rank_piece"));
            Assert.AreEqual(7, c.GetRealmFlat("hell", "gate"));
            Assert.AreEqual(7, c.GetRealmFlat("HELL", "gate"), "道境键大小写不敏感");
            Assert.AreEqual(0, c.GetRealmFlat("hell", "missing"));
            Assert.AreEqual(0, c.GetRealmFlat("ghost", "gate"));
            Assert.That(c.Warnings, Is.Empty);
        }

        // ───────────────────────────────────────────────
        // 五、加载器：候选链 / 注释 / 损坏兜底 / env 覆盖
        // ───────────────────────────────────────────────

        [Test]
        public void Load_ExplicitFile_WithComments_MatchesContract()
        {
            var dir = NewTempDir();
            try
            {
                var path = WriteJson(dir, "explicit.json",
                    "// 行注释\n" +
                    "{\n" +
                    "  /* 块注释 */ \"chat_cap\": 9,\n" +
                    "  \"limits\": { \"max_rounds\": 2 },\n" +
                    "  \"performance\": { \"effort_profile\": \"M2\" }\n" +
                    "}");
                var c = AgentConfigLoader.Load(path);
                Assert.AreEqual(9, c.ChatCap);
                Assert.AreEqual(2, c.Limits.MaxRounds);
                Assert.AreEqual("M2", c.Performance.EffortProfile);
                Assert.AreEqual(2048, c.Performance.ToolRoundMaxTokens, "未给的键取默认");
                Assert.AreEqual(path, c.Source, "Source 必须回显实际加载路径");
                Assert.That(c.Warnings, Is.Empty, "健康文件不得告警");
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_ExplicitFile_WithBom_Parsed()
        {
            var dir = NewTempDir();
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "bom.json");
                File.WriteAllText(path, "{ \"chat_cap\": 3 }", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                var c = AgentConfigLoader.Load(path);
                Assert.AreEqual(3, c.ChatCap, "BOM 不影响解析");
                Assert.That(c.Warnings, Is.Empty);
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_ShippedFile_MatchesContract()
        {
            var path = FindRepoFile("Assets/StreamingAssets/Agent/agent_remake.json");
            if (path == null) Assert.Ignore("未能在仓库内定位出厂 agent_remake.json（非仓库环境）");
            var c = AgentConfigLoader.Load(path);
            AssertContractValues(c, "出厂文件");
            Assert.AreEqual(path, c.Source);
            Assert.That(c.Warnings, Is.Empty, "出厂文件必须与合同完全一致且无回退");
        }

        [Test]
        public void Load_ExplicitPathMissing_Warns_AndReturnsContract()
        {
            var dir = NewTempDir();
            try
            {
                var missing = Path.Combine(dir, "no-such-file.json");
                var c = AgentConfigLoader.Load(missing);
                AssertContractValues(c, "显式路径缺失");
                Assert.That(HasWarning(c, missing), Is.True, "显式路径缺失必须告警");
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_EmptyExplicitPath_FallsThrough()
        {
            var c = AgentConfigLoader.Load("");
            AssertContractValues(c, "空显式路径");
        }

        [Test]
        public void Load_CorruptFile_Warns_AndReturnsContract()
        {
            var dir = NewTempDir();
            try
            {
                var path = WriteJson(dir, "bad.json", "{ 这不是 json");
                var c = AgentConfigLoader.Load(path);
                AssertContractValues(c, "损坏文件回退");
                Assert.That(HasWarning(c, "解析失败"), Is.True, "损坏文件必须留下解析失败告警");
                Assert.That(HasWarning(c, path), Is.True, "告警须指明文件");
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_EmptyFile_Warns_AndReturnsContract()
        {
            var dir = NewTempDir();
            try
            {
                var path = WriteJson(dir, "empty.json", "");
                var c = AgentConfigLoader.Load(path);
                AssertContractValues(c, "空文件回退");
                Assert.That(HasWarning(c, "为空"), Is.True, "空文件必须留下内容为空告警");
                Assert.That(HasWarning(c, path), Is.True, "告警须指明文件");
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_NullLiteralContent_Warns_AndReturnsContract()
        {
            var dir = NewTempDir();
            try
            {
                var path = WriteJson(dir, "null.json", "null");
                var c = AgentConfigLoader.Load(path);
                AssertContractValues(c, "字面 null 回退");
                Assert.That(HasWarning(c, path), Is.True, "字面 null 内容必须告警并指明文件");
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_JsonArrayRoot_Warns_AndReturnsContract()
        {
            var dir = NewTempDir();
            try
            {
                var path = WriteJson(dir, "array.json", "[1, 2, 3]");
                var c = AgentConfigLoader.Load(path);
                AssertContractValues(c, "数组根回退");
                Assert.That(HasWarning(c, "解析失败"), Is.True, "根不是对象必须报解析失败");
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_DirectoryAsExplicitPath_Skipped()
        {
            var dir = NewTempDir();
            try
            {
                Directory.CreateDirectory(dir);
                var c = AgentConfigLoader.Load(dir);
                AssertContractValues(c, "目录路径跳过");
                Assert.That(HasWarning(c, dir), Is.True, "不可读候选须告警");
            }
            finally { Cleanup(dir); }
        }

        [Test]
        public void Load_EnvConfigPath_Overrides()
        {
            var dir = NewTempDir();
            try
            {
                var path = WriteJson(dir, "env.json", ReAnchoredJson);
                Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarConfigPath, path);
                var c = AgentConfigLoader.Load();
                Assert.AreEqual(path, c.Source, "env 显式路径优先于自动探测");
                Assert.That(c.CreateFormula().CurveValue(1500), Is.EqualTo(7.5).Within(1e-9),
                    "改 json 即改输出——参数化黄金");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarConfigPath, null);
                Cleanup(dir);
            }
        }

        [Test]
        public void Load_EnvConfigPathMissing_Ignored()
        {
            var dir = NewTempDir();
            try
            {
                var missing = Path.Combine(dir, "gone.json");
                Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarConfigPath, missing);
                var c = AgentConfigLoader.Load();
                AssertContractValues(c, "env 路径缺失后继续链");
                Assert.That(HasWarning(c, missing), Is.True, "env 显式路径缺失必须告警");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarConfigPath, null);
                Cleanup(dir);
            }
        }

        [Test]
        public void Load_EnvAssetsRoot_Overrides()
        {
            var dir = NewTempDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "StreamingAssets", "Agent"));
                var path = WriteJson(Path.Combine(dir, "StreamingAssets", "Agent"), "agent_remake.json", ReAnchoredJson);
                Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarAssetsRoot, dir);
                var c = AgentConfigLoader.Load();
                Assert.AreEqual(path, c.Source, "ASSETS_ROOT 布局应被识别");
                Assert.That(c.CreateFormula().CurveValue(1500), Is.EqualTo(7.5).Within(1e-9));
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarAssetsRoot, null);
                Cleanup(dir);
            }
        }

        [Test]
        public void Load_DefaultCall_ReturnsContractValues()
        {
            // 自动模式：仓库内探测到出厂文件或回退内置——二者合同值一致
            var c = AgentConfigLoader.Load();
            AssertContractValues(c, "默认调用");
            Assert.IsFalse(string.IsNullOrEmpty(c.Source), "Source 必须有值");
        }

        [Test]
        public void Load_NoCandidates_ReturnsBuiltin_WithWarning()
        {
            var c = AgentConfigLoader.Load(probeRoots: new string[0]);
            AssertContractValues(c, "零候选");
            Assert.AreEqual("builtin", c.Source, "零候选 ⇒ 内置默认");
            Assert.That(HasWarning(c, "未找到"), Is.True, "零候选必须留下未找到告警");
        }

        // ───────────────────────────────────────────────
        // 基础设施
        // ───────────────────────────────────────────────

        const string ReAnchoredJson = @"{ ""karma_curve"": {
            ""anchors"": [[1000, 5], [2000, 10]], ""floor_token"": 1000,
            ""extend_last_slope"": false, ""min"": 1, ""max"": 50 } }";

        string _envPath, _envRoot;

        [SetUp]
        public void SetUp()
        {
            _envPath = Environment.GetEnvironmentVariable(AgentConfigLoader.EnvVarConfigPath);
            _envRoot = Environment.GetEnvironmentVariable(AgentConfigLoader.EnvVarAssetsRoot);
            Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarConfigPath, null);
            Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarAssetsRoot, null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarConfigPath, _envPath);
            Environment.SetEnvironmentVariable(AgentConfigLoader.EnvVarAssetsRoot, _envRoot);
        }

        static KarmaContext Ctx(string realm = "human", int singleMax = 120, int flat = 0)
            => new KarmaContext { Realm = realm, SingleMax = singleMax, Flat = flat };

        static string NewTempDir()
            => Path.Combine(Path.GetTempPath(), "chesssage-agentcfg-" + Guid.NewGuid().ToString("N"));

        static string WriteJson(string dir, string name, string content)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception) { /* 清理失败不影响测试结论 */ }
        }

        static string FindRepoFile(string relative)
        {
            var starts = new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory };
            foreach (var start in starts)
            {
                if (string.IsNullOrEmpty(start)) continue;
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        static bool HasWarning(AgentRemakeConfig config, string needle)
        {
            foreach (var w in config.Warnings)
                if (w != null && w.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static void AssertContractKarmaCurve(AgentRemakeConfig c, string context)
        {
            Assert.AreEqual(6, c.KarmaCurve.Anchors.Count, context + ": 锚点数应回退六档");
            Assert.AreEqual(2500, c.KarmaCurve.Anchors[0].Token, context);
            Assert.AreEqual(1, c.KarmaCurve.Anchors[0].Karma, context);
            Assert.AreEqual(8000, c.KarmaCurve.Anchors[1].Token, context);
            Assert.AreEqual(16, c.KarmaCurve.Anchors[1].Karma, context);
            Assert.AreEqual(12000, c.KarmaCurve.Anchors[2].Token, context);
            Assert.AreEqual(32, c.KarmaCurve.Anchors[2].Karma, context);
            Assert.AreEqual(15000, c.KarmaCurve.Anchors[3].Token, context);
            Assert.AreEqual(48, c.KarmaCurve.Anchors[3].Karma, context);
            Assert.AreEqual(16000, c.KarmaCurve.Anchors[4].Token, context);
            Assert.AreEqual(55, c.KarmaCurve.Anchors[4].Karma, context);
            Assert.AreEqual(21000, c.KarmaCurve.Anchors[5].Token, context);
            Assert.AreEqual(91, c.KarmaCurve.Anchors[5].Karma, context);
        }

        static void AssertContractValues(AgentRemakeConfig c, string context)
        {
            AssertContractKarmaCurve(c, context);
            Assert.AreEqual(2500, c.KarmaCurve.FloorToken, context + ": floor");
            Assert.IsTrue(c.KarmaCurve.ExtendLastSlope, context + ": extend");
            Assert.AreEqual(1, c.KarmaCurve.Min, context + ": min");
            Assert.AreEqual(120, c.KarmaCurve.Max, context + ": max");

            Assert.That(c.Fluctuation.UniformMin, Is.EqualTo(0.8).Within(1e-12), context + ": uniform min");
            Assert.That(c.Fluctuation.UniformMax, Is.EqualTo(1.3).Within(1e-12), context + ": uniform max");
            Assert.AreEqual(20000, c.Fluctuation.SurgeTokenThreshold, context + ": surge 阈值");
            Assert.That(c.Fluctuation.SurgeProbability, Is.EqualTo(0.30).Within(1e-12), context + ": surge 概率");
            Assert.That(c.Fluctuation.SurgeMin, Is.EqualTo(1.25).Within(1e-12), context + ": surge min");
            Assert.That(c.Fluctuation.SurgeMax, Is.EqualTo(1.5).Within(1e-12), context + ": surge max");

            Assert.AreEqual(5, c.ChatCap, context + ": chat_cap");

            Assert.AreEqual(4, c.Limits.MaxRounds, context + ": max_rounds");
            Assert.AreEqual(3, c.Limits.RepeatToolCap, context + ": repeat_tool_cap");
            Assert.AreEqual(3, c.Limits.MaxToolErrors, context + ": max_tool_errors");
            Assert.AreEqual(60000, c.Limits.TokenHardStop, context + ": token_hard_stop");
            Assert.AreEqual(24000, c.Limits.ContextSoftCap, context + ": context_soft_cap");
            Assert.AreEqual(20, c.Limits.HardTimeoutS, context + ": hard_timeout_s");

            Assert.AreEqual("M2", c.Performance.EffortProfile, context + ": effort_profile");
            Assert.AreEqual("maximize", c.Performance.PrefixCache, context + ": prefix_cache");
            Assert.AreEqual("A", c.Performance.TimeoutAction, context + ": timeout_action");
            Assert.AreEqual(2048, c.Performance.ToolRoundMaxTokens, context + ": tool max_tokens");
            Assert.AreEqual(4096, c.Performance.FinalRoundMaxTokens, context + ": final max_tokens");

            Assert.AreEqual(20, c.GetRealmFlat("animal", "high_rank_piece"), context + ": 道级 flat");
            Assert.AreEqual(0, c.GetRealmFlat("hell", "gate"), context + ": 未配置道境");
        }

        sealed class FixedRandom : IRandom
        {
            readonly double _value;
            public FixedRandom(double value) { _value = value; }
            public double NextDouble() => _value;
        }

        sealed class ExplodingRandom : IRandom
        {
            public double NextDouble() => throw new InvalidOperationException("该路径不得消耗随机源");
        }
    }
}
