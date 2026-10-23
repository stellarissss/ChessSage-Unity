using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 编排层（Orchestration）纯逻辑测试。
    ///
    /// 【E2-S6 迁移说明】原 8 项中 4 项测 <c>KarmaEstimator</c>（旧「分类价目表 + LLM 评估」双层级流水线）：
    ///   `KarmaEstimator_FallbackTable_MatchesPythonGolden` / `KarmaEstimator_DetectionDelta_MatchesFormula` /
    ///   `KarmaEstimator_IncreaseDecrease_ConsistentWithSamsara` / `KarmaEstimator_SingleMax_Rejected`
    /// —— 随 E2-S7 删除 <c>KarmaEstimator.cs</c> 一并删除；其等价覆盖分别由
    ///   · `KarmaFormulaTests`（token→业力曲线 / 波动 / clamp / 折扣链，KRM-01…17）；
    ///   · `SamsaraKarmaTests`（RPG 层业力状态机：Increase/Decrease/单次上限）；
    ///   · `SamsaraDetectionTests`（Δ = C×O^α 与溢出结算）
    /// 承担（QA §4.2.4 登记在案）。
    ///
    /// 保留的 4 项 <c>SchemaValidator</c> 用例（校验器未替换，D-05 修复价值不随架构过期），
    /// 并接收自 `OrchestratorTests` 原样迁入的 1 项（错误串三要素）。
    /// </summary>
    public sealed class SamsaraOrchestrationTests
    {
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

        [Test]
        public void SchemaValidator_SurfacesCoordinateAndRange()
        {
            // 自 OrchestratorTests 原样迁入（E2-S6）：错误串必须「点名路径 + 数值 + 合法范围」，
            // 否则 UI 无法给出可操作建议（AGT-12 的可操作性前提）。
            var board = TestEnv.LoadConfig("xiangqi", "board_state");
            ((JArray)board["pieces"][3]["position"])[1] = 11;   // 纵坐标越界（高 10 ⇒ 0-9）

            var (ok, err) = SchemaValidator.ValidateBoardState(board);
            Assert.IsFalse(ok, "纵坐标越界应被拒");
            StringAssert.Contains("pieces.3.position.1", err, "错误须定位到具体棋子与轴");
            StringAssert.Contains("11", err, "错误须含非法数值");
            StringAssert.Contains("0-9", err, "错误须含合法范围");
        }
    }
}
