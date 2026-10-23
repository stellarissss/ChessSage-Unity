using System;
using System.Collections.Generic;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>
    /// P5 PrefixStabilityProbe（QA §4.4.1 / §2.5-S11 / 工程 §13.2 R1–R7 / E3-S4）：
    /// 前缀缓存最大化的**可观测代理**——字节级稳定性与增量追加。
    ///
    /// 【输入】无外部夹具：直接用 <see cref="AgentPrompts"/> + <see cref="AgentToolSchemas"/> 组装，
    /// 并用 mock 传输捕获引擎实际发出的 messages 序列（每轮快照）。
    ///
    /// 【量化通过标准】全部满足才算 PASS：
    ///   A1 同输入两请求：system+user 序列化 SHA-256 **字节级相等**（含两个不同会话种子的引擎实例）；
    ///   A2 同请求相邻两轮：第 N+1 轮 messages 的前 N 轮条目与第 N 轮**逐条 DeepEquals**（只追加、无重排/改写），
    ///      且条数严格递增；
    ///   A3 工具 schema canonical：同一 allowed 集合、不同插入顺序 → 序列化字节 SHA-256 相等；
    ///   A4 <see cref="BoardSummarizer"/> 同快照同输出（确定性摘要）；
    ///   A5 禁时钟/随机/会话 id 渗入前缀：system 段不含会话种子串、不含 GUID 形状、不含时间戳形状，
    ///      两次组装字节相等（纯函数）。
    ///
    /// 【FAIL 判定】任一哈希不等 / 前缀被改写（第 N 轮任一消息字节变化）/ 条数未递增 /
    /// schema 随插入序变化 / 摘要非确定 / 前缀出现种子/时间戳/GUID。
    /// </summary>
    public static class PrefixStabilityProbe
    {
        const int SeedA = 20260101;
        const int SeedB = 987654321;

        public static void Run()
        {
            var a1 = CheckRequestBytesStable();
            var a2 = CheckAppendOnly();
            var a3 = CheckCanonicalToolOrder();
            var a4 = CheckSummarizerDeterministic();
            var a5 = CheckNoClockOrSeedLeak();

            Console.WriteLine($"    · A1 同输入两请求前缀 SHA-256 相等={a1.Equal}（{a1.Hash.Substring(0, 16)}…，seed {SeedA} vs {SeedB}）");
            Console.WriteLine($"    · A2 增量追加：{a2.Rounds} 轮，消息数 {a2.Counts}，前缀逐条一致={a2.AppendOnly}");
            Console.WriteLine($"    · A3 工具 schema 插入序无关：2 序 × SHA-256 相等={a3.Equal}（{a3.Hash.Substring(0, 16)}…，{a3.ToolCount} 工具）");
            Console.WriteLine($"    · A4 摘要确定性={a4}（同快照同输出，长度固定）");
            Console.WriteLine($"    · A5 无时钟/随机/会话 id 渗入={a5}");
        }

        // ── A1 ────────────────────────────────────────────────────────

        sealed class StableSummary
        {
            public bool Equal;
            public string Hash;
        }

        static StableSummary CheckRequestBytesStable()
        {
            var allowed = AllToolNames();

            var first = SerializePrefix(allowed, "把棋盘加宽到 8 列");
            var second = SerializePrefix(allowed, "把棋盘加宽到 8 列");
            var firstHash = AgentProbeKit.Sha256(first);
            ProbeEnv.Equal(firstHash, AgentProbeKit.Sha256(second), "A1 组装两次的前缀字节");

            // 真实引擎路径：两个不同会话种子的引擎，首轮实际请求必须字节相等（种子不得渗入前缀）
            var engineA = new ScriptedTransport().Text("好的。", 100, 20);
            var engineB = new ScriptedTransport().Text("好的。", 100, 20);
            AgentProbeKit.Run(AgentProbeKit.NewEngine(engineA, seed: SeedA), "把棋盘加宽到 8 列");
            AgentProbeKit.Run(AgentProbeKit.NewEngine(engineB, seed: SeedB), "把棋盘加宽到 8 列");

            ProbeEnv.Equal(engineA.Requests[0].MessagesJson, engineB.Requests[0].MessagesJson,
                "A1 两引擎首轮 messages 字节");
            ProbeEnv.Equal(engineA.Requests[0].ToolsJson, engineB.Requests[0].ToolsJson,
                "A1 两引擎首轮 tools 字节");
            ProbeEnv.Equal(AgentProbeKit.Sha256(engineA.Requests[0].MessagesJson + "|" + engineA.Requests[0].ToolsJson),
                AgentProbeKit.Sha256(engineB.Requests[0].MessagesJson + "|" + engineB.Requests[0].ToolsJson),
                "A1 两引擎「system+user+tools」联合 SHA-256");

            return new StableSummary { Equal = true, Hash = firstHash };
        }

        static string SerializePrefix(IReadOnlyList<string> allowed, string command)
        {
            var messages = new List<LlmMessage>
            {
                new LlmMessage { Role = "system", Content = AgentPrompts.BuildSystem(allowed) },
                new LlmMessage
                {
                    Role = "user",
                    Content = AgentPrompts.BuildUser(command,
                        BoardSummarizer.Summarize(
                            AgentProbeKit.GoldenConfigs()["board_state"] as JObject,
                            AgentProbeKit.GoldenConfigs()["board"] as JObject)),
                },
            };
            return LlmClient.SerializeMessages(messages).ToString(Formatting.None);
        }

        // ── A2 ────────────────────────────────────────────────────────

        sealed class AppendSummary
        {
            public int Rounds;
            public string Counts;
            public bool AppendOnly = true;
        }

        static AppendSummary CheckAppendOnly()
        {
            var transport = new ScriptedTransport()
                .Tool("get_board_state", "{}", 1200, 200, callId: "a1")
                .Tool("apply_config_patch",
                    "{\"operations\":[{\"target\":\"board\",\"patch\":[{\"op\":\"replace\",\"path\":\"/width\",\"value\":8}]}]}",
                    1400, 300, callId: "a2")
                .Text("改好了。", 1600, 120);

            var engine = AgentProbeKit.NewEngine(transport);
            AgentProbeKit.Run(engine, "把棋盘加宽到 8 列");

            ProbeEnv.Check(transport.Requests.Count >= 2, "A2 至少需要两轮请求才能验增量追加");

            var counts = new List<int>();
            JArray previous = null;
            for (int i = 0; i < transport.Requests.Count; i++)
            {
                var current = JArray.Parse(transport.Requests[i].MessagesJson);
                counts.Add(current.Count);

                if (previous != null)
                {
                    if (current.Count <= previous.Count)
                        throw new Exception(
                            $"A2 第 {i + 1} 轮消息数 {current.Count} 未严格递增（上轮 {previous.Count}）——历史被改写/压缩");
                    for (int j = 0; j < previous.Count; j++)
                        AgentAssert.DeepEquals(previous[j], current[j],
                            $"A2 第 {i + 1} 轮第 {j} 条消息前缀被改写（append-only 契约 R5/R6）");
                }
                previous = current;
            }

            return new AppendSummary
            {
                Rounds = transport.Requests.Count,
                Counts = string.Join("→", counts),
                AppendOnly = true,
            };
        }

        // ── A3 ────────────────────────────────────────────────────────

        sealed class CanonicalSummary
        {
            public bool Equal;
            public string Hash;
            public int ToolCount;
        }

        static CanonicalSummary CheckCanonicalToolOrder()
        {
            var forward = new List<string>
            {
                "get_board_state", "get_config_slice", "get_karma_state", "move_piece",
                "apply_config_patch", "set_objective", "undo_last_change", "commit_preview",
            };
            var reversed = new List<string>(forward);
            reversed.Reverse();

            var jsonForward = AgentToolSchemas.SerializeTools(AgentToolSchemas.ForAllowedTools(forward));
            var jsonReversed = AgentToolSchemas.SerializeTools(AgentToolSchemas.ForAllowedTools(reversed));

            ProbeEnv.Equal(jsonForward, jsonReversed, "A3 工具 schema 序列化（正序 vs 逆序，字节级）");
            ProbeEnv.Equal(AgentProbeKit.Sha256(jsonForward), AgentProbeKit.Sha256(jsonReversed), "A3 工具 schema SHA-256");

            var subsetA = new List<string> { "commit_preview", "get_board_state", "apply_config_patch" };
            var subsetB = new List<string> { "apply_config_patch", "commit_preview", "get_board_state" };
            ProbeEnv.Equal(
                AgentToolSchemas.SerializeTools(AgentToolSchemas.ForAllowedTools(subsetA)),
                AgentToolSchemas.SerializeTools(AgentToolSchemas.ForAllowedTools(subsetB)),
                "A3 子集工具 schema 序列化（插入序无关）");

            return new CanonicalSummary
            {
                Equal = true,
                Hash = AgentProbeKit.Sha256(jsonForward),
                ToolCount = AgentToolSchemas.All.Count,
            };
        }

        // ── A4 ────────────────────────────────────────────────────────

        static bool CheckSummarizerDeterministic()
        {
            var configs = AgentProbeKit.GoldenConfigs();
            var first = BoardSummarizer.Summarize(configs["board_state"] as JObject, configs["board"] as JObject);
            var second = BoardSummarizer.Summarize(configs["board_state"] as JObject, configs["board"] as JObject);
            ProbeEnv.Equal(first, second, "A4 BoardSummarizer 同快照同输出");

            // 摘要不得含时间戳形状（20xx-xx-xx / xx:xx:xx）
            ProbeEnv.Check(!ContainsTimestamp(first), "A4 摘要出现时间戳形状（摘要必须确定性）");
            ProbeEnv.Check(!string.IsNullOrEmpty(first), "A4 摘要不应为空（否则 A4 是空转断言）");
            return true;
        }

        // ── A5 ────────────────────────────────────────────────────────

        static bool CheckNoClockOrSeedLeak()
        {
            var allowed = AllToolNames();
            var system = AgentPrompts.BuildSystem(allowed);
            var again = AgentPrompts.BuildSystem(allowed);

            ProbeEnv.Equal(system, again, "A5 system 两次组装字节相等（纯函数）");
            ProbeEnv.Check(system.IndexOf(SeedA.ToString(), StringComparison.Ordinal) < 0,
                $"A5 system 渗入会话种子 {SeedA}");
            ProbeEnv.Check(system.IndexOf(SeedB.ToString(), StringComparison.Ordinal) < 0,
                $"A5 system 渗入会话种子 {SeedB}");
            ProbeEnv.Check(!ContainsGuidLike(system), "A5 system 渗入 GUID/会话 id 形状");
            ProbeEnv.Check(!ContainsTimestamp(system), "A5 system 渗入时间戳形状（R1/R2）");
            ProbeEnv.Check(!ContainsRoundMarker(system), "A5 system 渗入轮次标记（R7：轮次变参数不进消息体）");
            return true;
        }

        static string[] AllToolNames()
        {
            var names = new List<string>();
            foreach (var def in AgentToolSchemas.All) names.Add(def.Name);
            return names.ToArray();
        }

        static bool ContainsTimestamp(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i + 8 <= text.Length; i++)
            {
                // yyyy-MM-dd 或 HH:mm:ss 形状
                if (char.IsDigit(text[i]) && char.IsDigit(text[i + 1]) && char.IsDigit(text[i + 2]) &&
                    char.IsDigit(text[i + 3]) && text[i + 4] == '-' && char.IsDigit(text[i + 5]) &&
                    char.IsDigit(text[i + 6]) && text[i + 7] == '-')
                    return true;
                if (char.IsDigit(text[i]) && char.IsDigit(text[i + 1]) && text[i + 2] == ':' &&
                    char.IsDigit(text[i + 3]) && char.IsDigit(text[i + 4]) && text[i + 5] == ':')
                    return true;
            }
            return false;
        }

        static bool ContainsGuidLike(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            const string pattern = "xxxxxxxx-xxxx-";
            for (int i = 0; i + pattern.Length <= text.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    var c = text[i + j];
                    if (pattern[j] == 'x')
                    {
                        if (!Uri.IsHexDigit(c)) { ok = false; break; }
                    }
                    else if (c != pattern[j]) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        static bool ContainsRoundMarker(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("第 1 轮", StringComparison.Ordinal) >= 0
                || text.IndexOf("round=1", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("rounds_used", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
