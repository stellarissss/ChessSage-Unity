using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>
    /// P3 TrajectoryReplayProbe（QA §4.4.1-P3 / E3-S4）：黄金轨迹只读回放。
    ///
    /// 【输入】<c>Assets/ChessSage/Tests/Fixtures/agent/*.json</c> 探针子集（6 条，覆盖
    /// 正常流 / 顶格放行 / 20s 收尾 / 幻觉自愈 / 零写入闲聊 / 轮次耗尽）。回放零网络、零 wall-clock：
    /// 20s 到点经 <see cref="CancellationToken"/> 模拟（夹具 <c>cancel_before_round</c>）。
    ///
    /// 【量化通过标准】全部满足才算 PASS：
    ///   ① 每轨：工具序列 == expected.tool_sequence（逐项相等）；result_type 匹配；
    ///      T_eff / rounds / writes / cap_warned / timed_out 逐值相等。
    ///   ② 终态深比较：modified_configs 的键集合 == expected.modified_keys，
    ///      且每个切片与**探针侧独立推导**的终态 <c>JToken.DeepEquals</c>（非字节比较，避键序噪声）。
    ///   ③ 业力：有 karma_exact 取 exact（固定种子可复现），并同时验 [karma_min,karma_max]。
    ///   ④ 失败轨迹的 error_code 序列逐项相等（幻觉轮必须留下 UNKNOWN_TOOL）。
    ///   ⑤ 确定性：同轨回放两次，除计时外逐位相等（type/karma/T_eff/trajectory/modified_configs）。
    ///   ⑥ 全部回放耗时 &lt; 10s。
    ///
    /// 【FAIL 判定】任一断言失败 / 夹具 version &gt; 探针支持版本（明确 FAIL，禁静默 skip）/
    /// 两次回放不一致 / 轨迹出现未知工具名（幻觉夹具的**预期**失败轮除外）/ 夹具缺失。
    /// </summary>
    public static class TrajectoryReplayProbe
    {
        /// <summary>本探针支持的夹具体式版本（夹具 version &gt; 此值 ⇒ FAIL，须先升级探针）。</summary>
        public const int SupportedVersion = 1;

        static readonly string[] Cases =
        {
            "trj_e1_01_normal_flow",
            "trj_e1_02_cap_pass_through",
            "trj_e1_03_timeout_partial",
            "trj_e1_04_hallucination_heal",
            "trj_e1_05_zero_write_fun",
            "trj_e1_06_rounds_exhausted_partial",
        };

        public static void Run()
        {
            var watch = Stopwatch.StartNew();
            var lines = new List<string>();

            foreach (var name in Cases)
            {
                var path = Path.Combine(ProbeEnv.FixturesDir, "agent", name + ".json");
                ProbeEnv.Check(File.Exists(path), $"夹具缺失：{path}");

                var fixture = JObject.Parse(File.ReadAllText(path));
                var id = fixture.Value<string>("id") ?? name;
                var version = fixture.Value<int?>("version") ?? 1;
                if (version > SupportedVersion)
                    throw new Exception(
                        $"{id} 夹具 version={version} 高于探针支持版本 {SupportedVersion}（明确 FAIL：请先升级探针再回放）");

                var first = Replay(fixture, id);
                var second = Replay(fixture, id);
                Determinism(first, second, id);

                lines.Add($"{id} v{version} type={first.Result.Type} rounds={first.Result.Rounds} " +
                          $"T_eff={first.Result.TokenEff} writes={first.Result.Writes} karma={first.Result.EstimatedKarmaCost}" +
                          $"{(first.Result.CapWarned ? " cap" : "")}{(first.Result.TimedOut ? " timeout" : "")} " +
                          $"seq=[{string.Join(",", AgentAssert.ToolNames(first.Result))}] 两跑一致");
            }

            watch.Stop();
            ProbeEnv.Check(watch.Elapsed.TotalSeconds < 10.0,
                $"回放耗时 {watch.Elapsed.TotalSeconds:0.00}s 超预算（< 10s）");

            foreach (var line in lines) Console.WriteLine("    · " + line);
            Console.WriteLine($"    · {Cases.Length} 轨 × 2 回放（确定性双跑），耗时 {watch.Elapsed.TotalSeconds:0.00}s（预算 10s）");
        }

        // ── 单轨回放 + 断言 ───────────────────────────────────────────

        sealed class ReplayOutcome
        {
            public AgentResult Result;
            public JObject ResultJson;
            public JObject Fixture;
            public JObject Modified;
        }

        static ReplayOutcome Replay(JObject fixture, string id)
        {
            var command = fixture.Value<string>("command") ?? string.Empty;
            var seed = fixture.Value<int?>("seed") ?? 0;
            var cancelBefore = fixture.Value<int?>("cancel_before_round") ?? 0;
            var rounds = (JArray)fixture["rounds"];
            var expected = (JObject)fixture["expected"];
            var allowed = fixture["allowed_tools"] as JArray ?? AgentProbeKit.AllTools();

            var transport = new ScriptedTransport();
            for (int i = 0; i < rounds.Count; i++)
                transport.Add((JObject)((JObject)rounds[i])["response"]);

            var cts = new CancellationTokenSource();
            if (cancelBefore > 0)
                transport.BeforeRound = round =>
                {
                    if (round < cancelBefore) return;
                    // 20s 到点：取消令牌 → 引擎按收尾策略 A 交付（虚拟时钟，无 wall-clock）
                    cts.Cancel();
                    throw new OperationCanceledException($"夹具 {id} 到点（cancel_before_round={cancelBefore}）");
                };

            var engine = AgentProbeKit.NewEngine(transport, seed);
            engine.ConfigValidator = (name, cfg) => (true, null);   // 夹具录制口径（Schema 另由 AgentSmokeProbe 覆盖）
            var context = new JObject
            {
                ["configs"] = AgentProbeKit.GoldenConfigs(),
                ["allowed_tools"] = allowed,
            };

            var json = engine.ProcessCommandAsync(command, context, cts.Token).GetAwaiter().GetResult();
            var result = engine.LastResult;
            ProbeEnv.Check(result != null, $"{id}: 引擎未留下 LastResult");
            roundtripDigest(id, rounds, transport);
            AssertFixture(fixture, id, result, json, transport, cancelBefore);
            return new ReplayOutcome
            {
                Result = result,
                ResultJson = json,
                Fixture = fixture,
                Modified = json["modified_configs"] as JObject,
            };
        }

        /// <summary>夹具 request_digest 校验：前缀漂移（prompt/schema 改动）会让夹具静默失效，必须报警。</summary>
        static void roundtripDigest(string id, JArray rounds, ScriptedTransport transport)
        {
            for (int i = 0; i < rounds.Count && i < transport.Requests.Count; i++)
            {
                var digest = ((JObject)rounds[i]).Value<string>("request_digest");
                if (string.IsNullOrEmpty(digest) || digest == "*") continue;
                var actual = ShortHash(transport.Requests[i].MessagesJson);
                if (actual != digest)
                    throw new Exception($"{id} 第 {i + 1} 轮 request_digest 漂移：夹具 {digest} 实际 {actual}（前缀被改动，须走夹具 version+1）");
            }
        }

        static string ShortHash(string text)
        {
            var full = AgentProbeKit.Sha256(text);
            return full.Substring(0, 16);
        }

        static void AssertFixture(JObject fixture, string id, AgentResult result, JObject json,
            ScriptedTransport transport, int cancelBeforeRound)
        {
            var expected = (JObject)fixture["expected"];

            // ① 序列 / 类型 / 账本
            var expectedSeq = Strings(expected["tool_sequence"]);
            AgentAssert.Sequence(expectedSeq, AgentAssert.ToolNames(result), $"{id} 工具序列");

            var expectedType = expected.Value<string>("result_type");
            if (!string.Equals(expectedType, result.Type, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"{id} 结果类型：期望 {expectedType} 实际 {result.Type}（{result.Message}）");

            AgentAssert.Equal(expected.Value<int?>("token_eff") ?? -1, (int)result.TokenEff, $"{id} T_eff");
            AgentAssert.Equal(expected.Value<int?>("rounds") ?? -1, result.Rounds, $"{id} 轮次");
            AgentAssert.Equal(expected.Value<int?>("writes") ?? -1, result.Writes, $"{id} 写入数");
            AgentAssert.Equal(expected.Value<bool?>("cap_warned") ?? false, result.CapWarned, $"{id} CapWarning");
            AgentAssert.Equal(expected.Value<bool?>("timed_out") ?? false, result.TimedOut, $"{id} TimedOut");

            // ② 终态深比较（键集合 + 独立推导的切片终态）
            var modifiedKeys = Strings(expected["modified_keys"]);
            var actualKeys = new List<string>();
            if (json["modified_configs"] is JObject modified)
                foreach (var p in modified.Properties()) actualKeys.Add(p.Name);
            AgentAssert.Sequence(modifiedKeys, actualKeys, $"{id} 变更切片键");
            DeepCompareTerminal(id, fixture, json["modified_configs"] as JObject);

            // ③ 业力
            var exact = expected.Value<int?>("karma_exact");
            if (exact.HasValue)
                AgentAssert.Equal(exact.Value, result.EstimatedKarmaCost, $"{id} 业力精确值（固定种子）");
            var min = expected.Value<int?>("karma_min") ?? 0;
            var max = expected.Value<int?>("karma_max") ?? 120;
            AgentAssert.InRange(result.EstimatedKarmaCost, min, max, $"{id} 业力区间");

            // ④ 失败轨迹错误码序列
            var expectedCodes = expected["trajectory_error_codes"] == null
                ? new List<string>()
                : Strings(expected["trajectory_error_codes"]);
            var actualCodes = new List<string>();
            foreach (var entry in result.Trajectory)
                if (!entry.Ok) actualCodes.Add(entry.ErrorCode ?? string.Empty);
            AgentAssert.Sequence(expectedCodes, actualCodes, $"{id} 失败错误码序列");

            // ⑤ 未知工具名守卫（幻觉夹具的「预期失败轮」例外——它正是要验自愈）
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var def in AgentToolSchemas.All) known.Add(def.Name);
            foreach (var entry in result.Trajectory)
                if (entry.Ok && !known.Contains(entry.ToolName))
                    throw new Exception($"{id} 轨迹出现未知工具名却被判成功：{entry.ToolName}");

            // ⑥ 脚本耗尽守卫：无取消的轨，实际请求数不得超过编排轮数
            //   （有 cancel_before_round 的轨，被取消的那一轮会留一次请求快照但不吃脚本——见 §13.6）
            var scripted = ((JArray)fixture["rounds"]).Count;
            if (cancelBeforeRound <= 0)
                ProbeEnv.Check(transport.Requests.Count <= scripted,
                    $"{id} 引擎请求了 {transport.Requests.Count} 轮，超过编排 {scripted} 轮（禁静默重试）");
            else
                ProbeEnv.Check(transport.Requests.Count <= scripted + 1,
                    $"{id} 引擎在被取消后又发起了额外请求（{transport.Requests.Count} > {scripted}+1）");
        }

        /// <summary>
        /// 终态深比较：探针侧独立推导期望终态（黄金夹具 + 夹具脚本的语义），与实现输出 DeepEquals。
        /// 不比较字节序（Newtonsoft 对象序无关），只比语义。
        /// </summary>
        static void DeepCompareTerminal(string id, JObject fixture, JObject modified)
        {
            var baseConfigs = AgentProbeKit.GoldenConfigs();
            switch (id)
            {
                case "TRJ-E1-01":
                case "TRJ-E1-02":
                {
                    var expectedBoard = (JObject)baseConfigs["board"].DeepClone();
                    expectedBoard["width"] = 8;
                    AgentAssert.DeepEquals(expectedBoard, modified?["board"], $"{id} 终态 board 切片");
                    break;
                }
                case "TRJ-E1-03":
                case "TRJ-E1-06":
                {
                    var expectedState = (JObject)baseConfigs["board_state"].DeepClone();
                    foreach (var piece in (JArray)expectedState["pieces"])
                        if (piece.Value<string>("id") == "r_chariot_1")
                            piece["position"] = new JArray(4, 8);
                    AgentAssert.DeepEquals(expectedState, modified?["board_state"], $"{id} 终态 board_state 切片");
                    break;
                }
                case "TRJ-E1-04":
                case "TRJ-E1-05":
                    AgentAssert.True(modified == null || !modified.HasValues,
                        $"{id} 零写入轨不应产生 modified_configs");
                    break;
                default:
                    throw new Exception($"{id} 缺少探针侧终态期望（新增夹具必须同步补 §5.2 断言，禁静默跳过）");
            }
        }

        static void Determinism(ReplayOutcome a, ReplayOutcome b, string id)
        {
            AgentAssert.Equal(a.Result.Type, b.Result.Type, $"{id} 两跑 type");
            AgentAssert.Equal(a.Result.EstimatedKarmaCost, b.Result.EstimatedKarmaCost, $"{id} 两跑 karma");
            AgentAssert.Equal(a.Result.TokenEff, b.Result.TokenEff, $"{id} 两跑 T_eff");
            AgentAssert.Equal(a.Result.Rounds, b.Result.Rounds, $"{id} 两跑 rounds");
            AgentAssert.Equal(a.Result.Writes, b.Result.Writes, $"{id} 两跑 writes");
            AgentAssert.Equal(a.Result.CapWarned, b.Result.CapWarned, $"{id} 两跑 CapWarning");
            AgentAssert.Equal(a.Result.TimedOut, b.Result.TimedOut, $"{id} 两跑 TimedOut");
            AgentAssert.Equal(a.Result.Message, b.Result.Message, $"{id} 两跑 message");
            AgentAssert.Equal(a.Result.Karma?.Roll ?? 0.0, b.Result.Karma?.Roll ?? 0.0, $"{id} 两跑 roll（种子可复现）");

            AgentAssert.Sequence(AgentAssert.ToolNames(a.Result), AgentAssert.ToolNames(b.Result), $"{id} 两跑工具序列");
            for (int i = 0; i < a.Result.Trajectory.Count; i++)
            {
                var x = a.Result.Trajectory[i];
                var y = b.Result.Trajectory[i];
                AgentAssert.Equal(x.ArgumentsJson, y.ArgumentsJson, $"{id} 两跑 trajectory[{i}].arguments");
                AgentAssert.Equal(x.ResultJson, y.ResultJson, $"{id} 两跑 trajectory[{i}].result");
                AgentAssert.Equal(x.Ok, y.Ok, $"{id} 两跑 trajectory[{i}].ok");
            }
            AgentAssert.DeepEquals(a.ResultJson, b.ResultJson, $"{id} 两跑结果契约 JObject（除计时外逐位相等）");
        }

        static List<string> Strings(JToken token)
        {
            var list = new List<string>();
            if (token is JArray array)
                foreach (var t in array) list.Add(t.Value<string>());
            return list;
        }
    }
}
