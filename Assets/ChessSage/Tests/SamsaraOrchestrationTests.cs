using System.Threading.Tasks;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 编排层（Orchestration）纯逻辑测试（M5-B 顺带项）。
    ///
    /// 说明：<c>OrchestratorEngine</c> 依赖 LLM 网络调用，不做联网测试。
    /// 本类只覆盖**离线可判定**的纯逻辑：
    ///   • KarmaEstimator 的业力公式/价目表与 samsara 业力模型的一致性（Δ = C × O^α）；
    ///   • SchemaValidator 的 Schema 校验通过/拒绝路径（含棋盘越界检查）。
    /// </summary>
    public sealed class SamsaraOrchestrationTests
    {
        // ── KarmaEstimator 与 samsara 业力公式一致性 ─────────────────

        [Test]
        public void KarmaEstimator_FallbackTable_MatchesPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_karma");
            var est = new KarmaEstimator(gameType: "xiangqi");
            foreach (var tk in ((JObject)fx["assessor_fallback"]).Properties())
            {
                Assert.AreEqual(tk.Value.Value<int>(), est.FallbackAssess(tk.Name), "fallback " + tk.Name);
            }
        }

        [Test]
        public void KarmaEstimator_DetectionDelta_MatchesFormula()
        {
            var est = new KarmaEstimator(gameType: "xiangqi");
            var mods = new JObject { ["detection_coefficient"] = 0.1, ["detection_alpha"] = 1.5 };
            // 独立推导：Δ = C × O^α
            Assert.AreEqual(100.0, est.CalculateDetectionDelta(100, mods), 1e-9, "O=100");
            Assert.AreEqual(51.2, est.CalculateDetectionDelta(64, mods), 1e-9, "O=64");
            Assert.AreEqual(0.8, est.CalculateDetectionDelta(4, mods), 1e-9, "O=4");
            Assert.AreEqual(0.0, est.CalculateDetectionDelta(0, mods), 1e-9, "O=0");
        }

        [Test]
        public void KarmaEstimator_IncreaseDecrease_ConsistentWithSamsara()
        {
            var est = new KarmaEstimator(gameType: "xiangqi");
            est.SetLocalKarmaState(100, 120, 120);

            var inc = est.IncreaseKarma(40);
            Assert.AreEqual(40, inc["actual_increased"].Value<int>(), "actual_increased");
            Assert.IsTrue(inc["is_overdraft"].Value<bool>(), "跨阈值应溢出");
            Assert.AreEqual(20.0, inc["overdraft_amount"].Value<double>(), 1e-9, "溢出量");
            Assert.AreEqual(140, inc["new_karma"].Value<int>(), "新业力");

            var dec = est.DecreaseKarma(20, new JObject { ["karma_recover_multiplier"] = 1.3 });
            Assert.AreEqual(26, dec, "int(20×1.3)=26");
            Assert.AreEqual(114, est.GetLocalKarma(), "114");
        }

        [Test]
        public void KarmaEstimator_SingleMax_Rejected()
        {
            var est = new KarmaEstimator(gameType: "xiangqi");
            est.SetLocalKarmaState(50, 120, 120);
            var r = est.IncreaseKarma(130);
            Assert.AreEqual(0, r["actual_increased"].Value<int>(), "超单次上限应拒绝");
            Assert.IsFalse(r["success"].Value<bool>(), "success=false");
            Assert.AreEqual(50, est.GetLocalKarma(), "业力不变");
        }

        // ── SchemaValidator 校验路径 ─────────────────────────────────

        [Test]
        public void SchemaValidator_ValidBoardState_Passes()
        {
            SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
            SchemaValidator.ClearCache();

            var valid = JObject.Parse(
                "{\"pieces\":[{\"id\":\"p1\",\"type\":\"horse\",\"side\":\"red\",\"position\":[1,1],\"is_alive\":true}]," +
                "\"current_turn\":\"red\",\"game_status\":{\"state\":\"playing\",\"winner\":null}}");
            var (ok, err) = SchemaValidator.ValidateBoardState(valid);
            Assert.IsTrue(ok, "合法棋盘状态应通过：" + err);
        }

        [Test]
        public void SchemaValidator_OutOfBoundsBoard_Rejected()
        {
            SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
            SchemaValidator.ClearCache();

            var oob = JObject.Parse(
                "{\"board\":{\"width\":9,\"height\":10}," +
                "\"pieces\":[{\"id\":\"p1\",\"type\":\"horse\",\"side\":\"red\",\"position\":[20,0],\"is_alive\":true}]," +
                "\"current_turn\":\"red\",\"game_status\":{\"state\":\"playing\"}}");
            var (ok, err) = SchemaValidator.ValidateBoardState(oob);
            Assert.IsFalse(ok, "越界坐标应被拒绝");
            StringAssert.Contains("超出棋盘范围", err, "错误信息应包含越界说明");
        }

        [Test]
        public void SchemaValidator_MissingRequired_Rejected()
        {
            SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
            SchemaValidator.ClearCache();

            // 缺 current_turn
            var missing = JObject.Parse(
                "{\"pieces\":[],\"game_status\":{\"state\":\"playing\"}}");
            var (ok, err) = SchemaValidator.ValidateBoardState(missing);
            Assert.IsFalse(ok, "缺必填字段应被拒绝");
            StringAssert.Contains("current_turn", err, "错误信息应指出缺失字段");
        }

        [Test]
        public void SchemaValidator_UnknownConfigName_Rejected()
        {
            SchemaValidator.SchemaDir = TestEnv.SchemasRoot;
            SchemaValidator.ClearCache();
            var (ok, err) = SchemaValidator.ValidateConfig("no_such_config", new JObject());
            Assert.IsFalse(ok, "未知配置名应被拒绝");
            StringAssert.Contains("未知的配置名称", err, "错误信息应说明未知配置名");
        }
    }
}
