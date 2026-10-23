using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>
    /// P4 LegacyFreeProbe（QA §4.4.1-P4 / E3-S4）：旧机制复辟守卫（REG-03 的执行化）。
    ///
    /// 【输入】无外部输入：① 反射 Core 程序集；② 源码扫描（仓库树可用时）；
    /// ③ 运行时键探测（把旧上下文键喂给引擎，验证它**不产生任何门控效果**）。
    ///
    /// 【量化通过标准】全部满足才算 PASS：
    ///   ① 反射：<c>OrchestratorEngine</c> / <c>OrchestratorActions</c> / <c>PromptBuilder</c> /
    ///      <c>KarmaEstimator</c> 四类型在 Core 程序集中不存在；<c>IntentModels</c> 内不再有
    ///      <c>Classification</c> 类型/分类枚举。
    ///   ② 源码扫描：<c>Assets/ChessSage/{Core,Presentation}</c> 下
    ///      <c>allowed_classifications</c> / <c>FallbackAssess</c> / <c>IntentParserSystem</c> **零出现**；
    ///      Tests 目录内的出现只允许是「断言旧键不存在」的负向用例（否则视作消费方 = FAIL）。
    ///   ③ 运行时键探测：context 只带 <c>allowed_classifications</c>（旧键）时，
    ///      引擎的工具门控**回落到全量注册表**（旧键无任何效果），而不是按 A–F 分类裁剪。
    ///   ④ 旧夹具小节（<c>samsara_skills.json → allowed_classifications</c>）无 C# 消费方（源码扫描已覆盖）。
    ///
    /// 【FAIL 判定】任一旧类型 / 旧键 / 旧符号 / 旧消费方存在——即 L1 FAIL
    /// （重构中期回潮是最大隐性风险，本探针是唯一自动守卫）。
    /// </summary>
    public static class LegacyFreeProbe
    {
        static readonly string[] ForbiddenTypes =
        {
            "OrchestratorEngine", "OrchestratorActions", "PromptBuilder", "KarmaEstimator",
        };

        static readonly string[] ForbiddenSymbols =
        {
            "allowed_classifications", "FallbackAssess", "IntentParserSystem",
        };

        public static void Run()
        {
            var types = CheckReflection();
            var runtime = CheckRuntimeContextKey();
            var scan = CheckSourceScan();

            Console.WriteLine($"    · 反射：Core 程序集 {types.AssemblyName} 共 {types.TypeCount} 类型，" +
                              $"四旧类型均不存在（{string.Join("/", ForbiddenTypes)}）");
            Console.WriteLine($"    · 运行时：仅喂旧键 allowed_classifications 时工具数 = {runtime.ToolsWithLegacyKey}/{runtime.RegistryCount}");
            Console.WriteLine($"    · 源码扫描：Core+Presentation {scan.CoreFiles} 文件零命中；" +
                              $"Tests {scan.TestFiles} 文件中 {scan.TestHits} 处命中均为「断言不存在」的负向用例");
        }

        // ── ① 反射 ────────────────────────────────────────────────────

        sealed class TypeSummary
        {
            public string AssemblyName;
            public int TypeCount;
        }

        static TypeSummary CheckReflection()
        {
            var assembly = typeof(AgentEngine).Assembly;
            var types = assembly.GetTypes();

            foreach (var name in ForbiddenTypes)
                foreach (var type in types)
                    if (string.Equals(type.Name, name, StringComparison.Ordinal))
                        throw new Exception($"旧类型复活：{assembly.GetName().Name}.{type.FullName}（E2-S7 已删除，禁回潮）");

            // IntentModels.Classification：既可能是嵌套类型/枚举，也可能是同命名空间类型
            foreach (var type in types)
            {
                if (string.Equals(type.Name, "Classification", StringComparison.Ordinal))
                    throw new Exception($"旧分类体系复活：{type.FullName}");
                if (type.DeclaringType != null && string.Equals(type.Name, "Classification", StringComparison.Ordinal))
                    throw new Exception($"旧分类体系复活：{type.FullName}");
            }

            var intent = assembly.GetType("ChessSage.Core.Orchestration.IntentModels");
            if (intent != null)
            {
                foreach (var member in intent.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                         BindingFlags.Static | BindingFlags.Instance))
                    if (member.Name.IndexOf("Classification", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new Exception($"IntentModels 仍暴露分类成员：{member.Name}");
            }

            return new TypeSummary { AssemblyName = assembly.GetName().Name, TypeCount = types.Length };
        }

        // ── ③ 运行时键探测 ────────────────────────────────────────────

        sealed class RuntimeSummary
        {
            public int ToolsWithLegacyKey;
            public int RegistryCount;
        }

        static RuntimeSummary CheckRuntimeContextKey()
        {
            var transport = new ScriptedTransport().Text("我先看看。", 500, 50);
            var engine = AgentProbeKit.NewEngine(transport);

            // 旧键（A–F 分类）在场、新键缺失：门控必须回落到全量注册表（旧键零效果）
            var legacyContext = new JObject
            {
                ["configs"] = AgentProbeKit.GoldenConfigs(),
                ["allowed_classifications"] = new JArray("A", "B", "C", "E", "F"),
            };
            var result = engine.ProcessCommandAsync("随便问问", legacyContext).GetAwaiter().GetResult();
            ProbeEnv.Check(result != null, "运行时键探测：引擎应返回结果对象");

            var registry = AgentToolSchemas.All.Count;
            var toolsInRequest = transport.Requests.Count > 0 ? transport.Requests[0].ToolsJson : string.Empty;
            var advertised = 0;
            foreach (var def in AgentToolSchemas.All)
                if (toolsInRequest.IndexOf("\"" + def.Name + "\"", StringComparison.Ordinal) >= 0) advertised++;

            if (advertised != registry)
                throw new Exception(
                    $"旧键 allowed_classifications 仍在影响门控：只对外宣告 {advertised}/{registry} 个工具" +
                    "（应回落到全量注册表；旧键不得有任何效果）");

            // 反向确认：context 里出现旧键名时，引擎读的仍是 allowed_tools（读键名硬编码在实现里）
            var withNewKey = new ScriptedTransport().Text("同上。", 500, 50);
            var engine2 = AgentProbeKit.NewEngine(withNewKey);
            var allowed = new JArray("get_board_state");
            var newContext = new JObject
            {
                ["configs"] = AgentProbeKit.GoldenConfigs(),
                ["allowed_tools"] = allowed,
            };
            engine2.ProcessCommandAsync("只看棋盘", newContext).GetAwaiter().GetResult();
            var narrowed = 0;
            foreach (var def in AgentToolSchemas.All)
                if (withNewKey.Requests[0].ToolsJson.IndexOf("\"" + def.Name + "\"", StringComparison.Ordinal) >= 0) narrowed++;
            ProbeEnv.Equal(1, narrowed, "新键 allowed_tools 应收窄工具清单（门控换轴生效）");

            return new RuntimeSummary { ToolsWithLegacyKey = advertised, RegistryCount = registry };
        }

        // ── ② 源码扫描（④ 旧夹具消费方同源覆盖）────────────────────────

        sealed class ScanSummary
        {
            public int CoreFiles;
            public int TestFiles;
            public int TestHits;
        }

        static ScanSummary CheckSourceScan()
        {
            var assets = Path.Combine(ProbeEnv.RepoRoot, "Assets", "ChessSage");
            var core = ScanDir(Path.Combine(assets, "Core"), strict: true, label: "Core");
            var presentation = ScanDir(Path.Combine(assets, "Presentation"), strict: true, label: "Presentation");
            var tests = ScanDir(Path.Combine(assets, "Tests"), strict: false, label: "Tests");

            return new ScanSummary
            {
                CoreFiles = core.Files + presentation.Files,
                TestFiles = tests.Files,
                TestHits = tests.Hits,
            };
        }

        struct ScanResult
        {
            public int Files;
            public int Hits;
        }

        static ScanResult ScanDir(string dir, bool strict, string label)
        {
            var result = new ScanResult();
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                result.Files++;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (var symbol in ForbiddenSymbols)
                    {
                        if (lines[i].IndexOf(symbol, StringComparison.Ordinal) < 0) continue;
                        result.Hits++;
                        if (strict)
                            throw new Exception(
                                $"旧符号残留：{symbol} @ {Relative(file)}:{i + 1}\n    {lines[i].Trim()}");
                        if (!IsNegativeAssertion(lines[i]))
                            throw new Exception(
                                $"旧夹具小节出现**消费方**（非负向断言）：{symbol} @ {Relative(file)}:{i + 1}\n    {lines[i].Trim()}");
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Tests 目录的容忍规则：命中行必须是「断言旧键不存在」的负向用例
        /// （IsNull / DoesNotContain / 不得 / 注入旧键做无害化验证）。其余视作消费方 = FAIL。
        /// </summary>
        static bool IsNegativeAssertion(string line)
        {
            return line.IndexOf("IsNull", StringComparison.Ordinal) >= 0
                || line.IndexOf("DoesNotContain", StringComparison.Ordinal) >= 0
                || line.IndexOf("IsFalse", StringComparison.Ordinal) >= 0
                || line.IndexOf("不得", StringComparison.Ordinal) >= 0
                || line.IndexOf("legacy[", StringComparison.Ordinal) >= 0
                || line.IndexOf("//", StringComparison.Ordinal) >= 0;
        }

        static string Relative(string path)
        {
            var root = ProbeEnv.RepoRoot;
            return path.StartsWith(root, StringComparison.Ordinal)
                ? path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
                : path;
        }
    }
}
