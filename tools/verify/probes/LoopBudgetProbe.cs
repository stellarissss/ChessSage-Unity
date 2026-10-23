using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ChessSage.Core.Agent;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;

namespace ChessSage.Harness
{
    /// <summary>
    /// P6 LoopBudgetProbe（QA §2.5-S12 / §4.4 / 工程 §13.1 / E3-S4）：轮次与延迟预算 + 遥测完整性。
    ///
    /// 【输入】mock 传输 + **虚拟时钟**（<see cref="VirtualClock"/>，每轮推进固定毫秒），
    /// 全程零 wall-clock、零 sleep；20s 到点经 <see cref="CancellationToken"/> 模拟（组合根 CancelAfter 的替身）。
    ///
    /// 【量化通过标准】全部满足才算 PASS：
    ///   ① maxRounds 熔断：生产配置 4 轮 ⇒ 引擎恰好停在第 4 轮（请求数 4、Rounds=4），
    ///      停止原因 ROUNDS_EXHAUSTED，有写入 ⇒ partial（不再多请求一轮）。
    ///   ② hardTokenBudget：T_eff 越过 60K 即刻停（TOKEN_BUDGET），T_eff 如实记录（不截断造假），
    ///      且该轮工具未派发（沙箱只含此前已完成轮的写入）。
    ///   ③ 20s 取消令牌：虚拟时钟推进到 &gt;20s 的那一轮被取消 ⇒ TimedOut=true、Rounds 只计**已完成**轮，
    ///      有写入 ⇒ partial 半成品（收尾策略 A）。
    ///   ④ 遥测五字段完整性（§13.7 requests 数组）：ttft_ms &gt; 0；round_ms.Length == rounds 且逐项 ≥ 0；
    ///      total_ms ≥ Σ round_ms；prefix_hit_rate ∈ [0,1] 且 == Σcached/Σprompt；rounds == 实际轮次。
    ///   ⑤ 探针自身墙钟 &lt; 2s（证明无 sleep：虚拟时钟不推进真实时间）。
    ///
    /// 【FAIL 判定】未在预算处熔断 / 熔断后仍派发工具 / TimedOut 未置位 / 轮次计数含未完成轮 /
    /// 任一遥测字段缺失或为负 / a 与 b 的关系不成立 / 探针耗时超预算。
    /// </summary>
    public static class LoopBudgetProbe
    {
        /// <summary>写工具样例（pieces_red 恒可用，且能过真实 pieces Schema）。</summary>
        const string PatchHorseSideStep =
            "{\"operations\":[{\"target\":\"pieces_red\",\"patch\":[{\"op\":\"add\",\"path\":\"/pieces/horse/moves/-\"," +
            "\"value\":{\"kind\":\"jump\",\"to\":[1,0],\"block\":[],\"land\":\"any\",\"sym\":\"rotate4\"}}]}]}";

        /// <summary>虚拟时钟下「20s 到点」的判定阈值（与 CheatCommandPanel.HardTimeoutSeconds 同值）。</summary>
        const long HardTimeoutMs = 20000;

        public static void Run()
        {
            var watch = Stopwatch.StartNew();

            var rounds = CheckMaxRoundsFuse();
            var budget = CheckTokenHardStop();
            var timeout = CheckVirtualTimeout();
            var telemetry = CheckTelemetry(rounds.Engine);

            watch.Stop();
            ProbeEnv.Check(watch.Elapsed.TotalSeconds < 2.0,
                $"探针墙钟 {watch.Elapsed.TotalSeconds:0.00}s 超预算（< 2s，虚拟时钟不得 sleep）");

            Console.WriteLine($"    · maxRounds 熔断：MaxRounds={rounds.MaxRounds} 请求数={rounds.Requests} Rounds={rounds.Rounds} " +
                              $"stop={rounds.StopReason} type={rounds.Type}");
            Console.WriteLine($"    · token 硬停：TokenHardStop={budget.Limit} T_eff={budget.TokenEff} Rounds={budget.Rounds} " +
                              $"stop={budget.StopReason} 工具派发={budget.ToolsDispatched}");
            Console.WriteLine($"    · 20s 到点（虚拟时钟 {timeout.VirtualMs}ms）：TimedOut={timeout.TimedOut} " +
                              $"Rounds={timeout.Rounds} 请求数={timeout.Requests} type={timeout.Type}");
            Console.WriteLine($"    · 遥测五字段：rounds={telemetry.Rounds} ttft_ms={telemetry.TtftMs} " +
                              $"round_ms=[{telemetry.RoundMs}] total_ms={telemetry.TotalMs} prefix_hit_rate={telemetry.PrefixHitRate:0.0000}");
            Console.WriteLine($"    · 探针墙钟 {watch.Elapsed.TotalSeconds:0.000}s（虚拟时钟，无 sleep）");
        }

        // ── ① maxRounds 熔断 ──────────────────────────────────────────

        sealed class RoundsSummary
        {
            public int MaxRounds;
            public int Requests;
            public int Rounds;
            public string StopReason;
            public string Type;
            public AgentEngine Engine;
        }

        static RoundsSummary CheckMaxRoundsFuse()
        {
            // 每轮固定虚拟耗时 900ms（虚拟时钟，非真实等待）——遥测断言因此可精确比对
            var transport = new ScriptedTransport { VirtualRoundMs = 900 };
            transport
                .Tool("apply_config_patch", PatchHorseSideStep, 1200, 300, cached: 600, callId: "r1")
                .Tool("get_config_slice", "{\"target\":\"board\"}", 1400, 200, cached: 700, callId: "r2")
                .Tool("get_board_state", "{}", 1500, 200, cached: 700, callId: "r3")
                .Tool("get_karma_state", "{}", 1500, 200, cached: 700, callId: "r4");

            var engine = AgentProbeKit.NewEngine(transport);
            var result = AgentProbeKit.Run(engine, "多读几遍再改");

            var maxRounds = engine.Config.Limits.MaxRounds;
            ProbeEnv.Equal(4, maxRounds, "生产配置 max_rounds（§13.1 由 20s 反推值）");
            ProbeEnv.Equal(maxRounds, transport.Requests.Count, "熔断：LLM 请求数应恰为 maxRounds（不得多请求一轮）");
            ProbeEnv.Equal(maxRounds, result.Rounds, "熔断：Rounds 应等于 maxRounds");
            ProbeEnv.Equal("ROUNDS_EXHAUSTED", result.ErrorCode, "熔断停止原因");
            ProbeEnv.Equal("partial", result.Type, "熔断且有写入 ⇒ partial（收尾策略 A）");
            ProbeEnv.Equal(1, result.Writes, "熔断时写入数应如实记录");

            return new RoundsSummary
            {
                MaxRounds = maxRounds,
                Requests = transport.Requests.Count,
                Rounds = result.Rounds,
                StopReason = result.ErrorCode,
                Type = result.Type,
                Engine = engine,
            };
        }

        // ── ② token 硬停 ──────────────────────────────────────────────

        sealed class BudgetSummary
        {
            public int Limit;
            public long TokenEff;
            public int Rounds;
            public string StopReason;
            public int ToolsDispatched;
        }

        static BudgetSummary CheckTokenHardStop()
        {
            // 每轮 31,000 token：第 2 轮结束 T_eff=62,000 > 60,000 ⇒ 立刻停，且第 2 轮工具不得派发
            var transport = new ScriptedTransport()
                .Tool("apply_config_patch", PatchHorseSideStep, 20000, 11000, callId: "b1")
                .Tool("get_board_state", "{}", 20000, 11000, callId: "b2")
                .Tool("get_board_state", "{}", 20000, 11000, callId: "b3")
                .Tool("get_board_state", "{}", 20000, 11000, callId: "b4");

            var engine = AgentProbeKit.NewEngine(transport);
            var result = AgentProbeKit.Run(engine, "大改一场");

            var limit = engine.Config.Limits.TokenHardStop;
            ProbeEnv.Equal(60000, limit, "生产配置 token_hard_stop");
            ProbeEnv.Check(result.TokenEff > limit,
                $"T_eff {result.TokenEff} 应越过硬停线 {limit}（否则用例空转）");
            ProbeEnv.Equal("TOKEN_BUDGET", result.ErrorCode, "硬停停止原因");
            ProbeEnv.Equal(2, result.Rounds, "硬停：Rounds 应为 2（第 2 轮越线后即停）");
            ProbeEnv.Equal(2, transport.Requests.Count, "硬停：不应发起第 3 轮请求");
            ProbeEnv.Equal(1, result.Trajectory.Count,
                "硬停：第 2 轮工具不得派发（轨迹只应含第 1 轮的 1 次调用）");
            ProbeEnv.Equal("partial", result.Type, "硬停且有写入 ⇒ partial");

            return new BudgetSummary
            {
                Limit = limit,
                TokenEff = result.TokenEff,
                Rounds = result.Rounds,
                StopReason = result.ErrorCode,
                ToolsDispatched = result.Trajectory.Count,
            };
        }

        // ── ③ 20s 到点（虚拟时钟 + 取消令牌）────────────────────────

        sealed class TimeoutSummary
        {
            public long VirtualMs;
            public bool TimedOut;
            public int Rounds;
            public int Requests;
            public string Type;
        }

        static TimeoutSummary CheckVirtualTimeout()
        {
            var transport = new ScriptedTransport { VirtualRoundMs = 6000 };
            transport
                .Tool("apply_config_patch", PatchHorseSideStep, 1200, 300, callId: "t1")
                .Tool("get_board_state", "{}", 1300, 200, callId: "t2")
                .Tool("get_board_state", "{}", 1400, 200, callId: "t3")
                .Tool("get_board_state", "{}", 1500, 200, callId: "t4");

            var cts = new CancellationTokenSource();
            transport.BeforeRound = round =>
            {
                // 组合根 CancelAfter(20s) 的等价物：虚拟时钟越过硬顶即取消（零 wall-clock）
                if (transport.Clock.Ms <= HardTimeoutMs) return;
                cts.Cancel();
                throw new OperationCanceledException(
                    $"虚拟时钟 {transport.Clock.Ms}ms 越过 {HardTimeoutMs}ms 硬顶（第 {round} 轮）");
            };

            var engine = AgentProbeKit.NewEngine(transport);
            var result = AgentProbeKit.Run(engine, "把棋盘加宽再挪子", cts.Token);

            ProbeEnv.Check(result.TimedOut, "20s 到点应置 TimedOut");
            ProbeEnv.Equal(3, result.Rounds, "到点：Rounds 只计已完成（未发起/被取消的那轮不计）");
            ProbeEnv.Equal(4, transport.Requests.Count, "到点：第 4 轮请求已发起但被取消");
            ProbeEnv.Equal("TIMEOUT", result.ErrorCode, "到点停止原因");
            ProbeEnv.Equal("partial", result.Type, "到点有写入 ⇒ partial 半成品（策略 A）");
            ProbeEnv.Check(result.EstimatedKarmaCost > 0 && result.EstimatedKarmaCost <= 120,
                $"到点半成品价应落 (0,120]，实际 {result.EstimatedKarmaCost}");
            ProbeEnv.Check(transport.Clock.Ms > HardTimeoutMs, "虚拟时钟应越过硬顶（判定 ③ 非空转）");

            return new TimeoutSummary
            {
                VirtualMs = transport.Clock.Ms,
                TimedOut = result.TimedOut,
                Rounds = result.Rounds,
                Requests = transport.Requests.Count,
                Type = result.Type,
            };
        }

        // ── ④ 遥测五字段 ──────────────────────────────────────────────

        sealed class TelemetrySummary
        {
            public int Rounds;
            public long TtftMs;
            public string RoundMs;
            public long TotalMs;
            public double PrefixHitRate;
        }

        static TelemetrySummary CheckTelemetry(AgentEngine engine)
        {
            var requests = engine.Ledger.Requests;
            ProbeEnv.Check(requests.Count > 0, "遥测：账本应至少记录一次提交");
            var record = requests[requests.Count - 1];

            // 落盘形状（§13.7 键名契约）：token_stats.json 的 requests 条目必须含这五个键
            var json = engine.Ledger.ToJson();
            var ring = json["requests"] as JArray;
            ProbeEnv.Check(ring != null && ring.Count > 0, "遥测：token_stats.json 应含 requests 数组");
            var persisted = (JObject)ring[ring.Count - 1];
            foreach (var key in new[] { "ttft_ms", "round_ms", "total_ms", "prefix_hit_rate", "rounds" })
                ProbeEnv.Check(persisted[key] != null, $"遥测：落盘条目缺少字段 {key}");

            ProbeEnv.Check(record.Rounds > 0, "遥测：rounds 应为正");
            ProbeEnv.Equal(engine.LastResult.Rounds, record.Rounds, "遥测：rounds 与结果对象一致");

            ProbeEnv.Check(record.TtftMs > 0,
                "遥测：ttft_ms 未被填充（首字延迟必须可观测，§13.7；0 等于该字段死掉）");
            ProbeEnv.Equal(900L, record.TtftMs, "遥测：ttft_ms 应等于首轮虚拟耗时（P0 代理口径）");
            ProbeEnv.Check(record.TtftProxy, "遥测：P0 非流式应标注 ttft_proxy=true（§13.5 降级口径）");
            ProbeEnv.Check((persisted["ttft_proxy"] != null) && persisted.Value<bool>("ttft_proxy"),
                "遥测：落盘 ttft_proxy 应为 true");

            ProbeEnv.Check(record.RoundMs != null && record.RoundMs.Length == record.Rounds,
                $"遥测：round_ms 长度 {record.RoundMs?.Length ?? -1} 应等于 rounds {record.Rounds}");

            long sum = 0;
            var parts = new List<string>();
            foreach (var ms in record.RoundMs ?? new int[0])
            {
                ProbeEnv.Check(ms >= 0, $"遥测：round_ms 出现负值 {ms}");
                sum += ms;
                parts.Add(ms.ToString());
            }

            ProbeEnv.Check(record.TotalMs >= sum,
                $"遥测：total_ms {record.TotalMs} 应 ≥ Σ round_ms {sum}（总延迟含本地开销）");
            ProbeEnv.Equal(sum, record.TotalMs, "遥测：虚拟时钟下 total_ms 应恰等于 Σ round_ms（本地开销为 0）");
            ProbeEnv.Check(record.TotalMs > 0, "遥测：total_ms 未被填充（0 等于该字段死掉）");

            ProbeEnv.Check(record.PrefixHitRate >= 0.0 && record.PrefixHitRate <= 1.0,
                $"遥测：prefix_hit_rate {record.PrefixHitRate} 应落 [0,1]");
            var expectedRate = record.PromptTokens > 0
                ? (double)record.CachedPromptTokens / record.PromptTokens
                : 0.0;
            ProbeEnv.Check(Math.Abs(record.PrefixHitRate - expectedRate) < 1e-9,
                $"遥测：prefix_hit_rate {record.PrefixHitRate} 应 == Σcached/Σprompt {expectedRate}");
            ProbeEnv.Check(record.CachedPromptTokens > 0,
                "遥测：cached_prompt_tokens 应被统计（本用例每轮注入 cached tokens，为 0 说明分列丢失）");

            return new TelemetrySummary
            {
                Rounds = record.Rounds,
                TtftMs = record.TtftMs,
                RoundMs = string.Join(",", parts),
                TotalMs = record.TotalMs,
                PrefixHitRate = record.PrefixHitRate,
            };
        }
    }
}
