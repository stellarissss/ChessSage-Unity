using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using ChessSage.Core.Agent;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E1-S2：AgentPrompts 组装器（前缀字节级稳定是硬约束，§13.2 R1-R7）。
    /// A1 同输入两请求前缀 SHA-256 相等；A3 调用方集合插入序无关；
    /// A4 无时钟/无请求变内容进前缀（签名即证：BuildSystem 不收 command）；
    /// system+schema ≈1,500 token/轮（设计侧 §6.2 约定）。
    /// </summary>
    [TestFixture]
    public sealed class AgentPromptsTests
    {
        static readonly string[] Allowed =
        {
            "get_board_state", "get_config_slice", "get_karma_state",
            "move_piece", "apply_config_patch", "set_objective",
            "undo_last_change", "commit_preview",
        };

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

        [Test]
        public void BuildSystem_SameInput_Sha256Identical()
        {
            var a = AgentPrompts.BuildSystem(Allowed);
            var b = AgentPrompts.BuildSystem(Allowed);
            Assert.AreEqual(Sha256(a), Sha256(b), "同输入必须字节级一致（DeepSeek 前缀缓存前提 R1）");
        }

        [Test]
        public void BuildSystem_InsertionOrderIrrelevant()
        {
            var shuffled = new List<string>(Allowed);
            shuffled.Reverse();
            var forward = AgentPrompts.BuildSystem(Allowed);
            var backward = AgentPrompts.BuildSystem(shuffled);
            Assert.AreEqual(forward, backward, "调用方集合排列不得影响输出（注册表固定序 canonical 序列化 R3）");
        }

        [Test]
        public void BuildSystem_ListsAllowedToolNames_WithoutDuplicatingSchemas()
        {
            var system = AgentPrompts.BuildSystem(Allowed);
            foreach (var name in Allowed)
                StringAssert.Contains(name, system, "允许的工具名必须出现在系统提示（门控信号）");
            StringAssert.DoesNotContain("\"parameters\"", system,
                "E3-S2 体积优化：schema 只经 tools 数组下发一次，system 内不得重复嵌入");
        }

        [Test]
        public void BuildSystem_FiltersToAllowedToolsOnly()
        {
            var limited = new[] { "get_board_state", "move_piece", "commit_preview" };
            var system = AgentPrompts.BuildSystem(limited);

            StringAssert.Contains("get_board_state", system);
            StringAssert.Contains("move_piece", system);
            StringAssert.DoesNotContain("apply_config_patch", system, "未解锁工具不得出现在系统提示（门控换轴 ADR-6）");
        }

        [Test]
        public void BuildSystem_StaysWithinTokenBudget()
        {
            // E3-S2 体积优化目标：system（散文+工具名）+ tools schema ≈1,500 token/轮。
            // UTF8 字节/4 对中文低估约 2.5×；这里用粗估守住「不重复 schema」的结构性预算，
            // 精确 token 由 E3 冒烟的真实 prompt_tokens 回归（Logs/e3/raw/inspect-*.json）。
            var system = AgentPrompts.BuildSystem(Allowed);
            var tools = AgentToolSchemas.SerializeTools(AgentToolSchemas.ForAllowedTools(Allowed));
            var systemEst = AgentPrompts.EstimateTokens(system);
            var toolsEst = AgentPrompts.EstimateTokens(tools);
            Assert.LessOrEqual(systemEst, 500, $"system 粗估 {systemEst}，散文段过大");
            Assert.LessOrEqual(toolsEst, 1100, $"tools 粗估 {toolsEst}，schema 未压缩");
            Assert.Greater(systemEst, 100, "system 过小意味着门控/流程段缺失");
        }

        [Test]
        public void BuildSystem_NoClockOrRequestVaryingContent()
        {
            // R1/R2：system 只在提交前组装一次，且不得含请求变内容——签名即证（无 command 参数）。
            // 这里兜底断言输出不含时间戳样式文本。
            var system = AgentPrompts.BuildSystem(Allowed);
            Assert.IsFalse(system.Contains("2026-"), "系统提示不得含时间戳");
            Assert.IsFalse(system.Contains("DateTime"), "系统提示不得含时钟引用");
        }

        [Test]
        public void BuildUser_ContainsCommandAndBoardSummary()
        {
            var user = AgentPrompts.BuildUser("把我的马变成炮", "- 棋盘 9x10；当前轮到 红方\n- 红方 16 子");
            StringAssert.Contains("把我的马变成炮", user);
            StringAssert.Contains("棋盘 9x10", user, "user = 指令 + BoardSummarizer 紧凑摘要（§5.6 R4）");
        }

        [Test]
        public void BuildUser_SameInput_Sha256Identical()
        {
            var a = AgentPrompts.BuildUser("cmd", "summary");
            var b = AgentPrompts.BuildUser("cmd", "summary");
            Assert.AreEqual(Sha256(a), Sha256(b));
        }

        [Test]
        public void BuildUser_NullInputs_DoNotThrow()
        {
            Assert.DoesNotThrow(() => AgentPrompts.BuildUser(null, null));
            Assert.DoesNotThrow(() => AgentPrompts.BuildUser("", ""));
        }

        [Test]
        public void EstimateTokens_MatchesCharsOverRoughBound()
        {
            // 4 字节/token 粗估：ASCII 100 字符 ≈ 25 token。
            Assert.AreEqual(25, AgentPrompts.EstimateTokens(new string('a', 100)));
        }
    }
}
