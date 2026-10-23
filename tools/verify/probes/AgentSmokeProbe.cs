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
    /// P1 AgentSmokeProbe（QA §4.4.1-P1 / E3-S4）：Agent 端到端冒烟。
    ///
    /// 【输入】内置 mock 脚本（net48 无 NUnit，只引 Core API）：
    ///   ① 正常流 3 轮：get_board_state → apply_config_patch → final 文本；
    ///   ② 顶格脚本：首轮 usage 拉到 T_eff ≈ 50K（投射 ≈302 &gt;&gt; 120）；
    ///   ③ 两跑副本：同一脚本跑两个引擎实例（种子不同，验证无随机/会话渗入前缀）。
    ///
    /// 【量化通过标准】全部满足才算 PASS：
    ///   ① 正常跑：工具序列 == [get_board_state, apply_config_patch]（逐项相等）；
    ///      patch 落地（modified_configs.board.width == 8）且 Schema 硬校验复验通过；
    ///      T_eff == Σ(prompt+completion) == 4720；karma == clamp(round(f_indep(T_eff)×roll))；
    ///      f 与独立推导值差 ≤ 1e-9；Confirm 后对局配置**真实改写**（马 moves +1）且业力按 preview 价扣减。
    ///   ② 顶格跑：progress 中至少一条 CapWarning=true 且 Growing=true（警示已发出）；
    ///      Transport 请求数 == 2（循环未提前拒绝，走到了 final）；结果 type=applied 非 error；
    ///      karma == 120（clamp 放行，非 &gt;120）；preview 可 Confirm 且 Applied=true。
    ///   ③ 两跑副本：首轮 system/工具 schema 序列化 SHA-256 相等。
    ///   ④ 零未捕获异常；探针墙钟 &lt; 5s。
    ///
    /// 【FAIL 判定】任一断言失败 / mock 脚本耗尽（抛「脚本已耗尽」）/ 抛异常 / 哈希不等。
    /// 【零 wall-clock 依赖】逻辑断言全部与时间无关；仅末尾用 Stopwatch 量探针自身预算（判定项 ④）。
    /// </summary>
    public static class AgentSmokeProbe
    {
        const string PatchHorseSideStep =
            "{\"operations\":[{\"target\":\"pieces_red\",\"patch\":[{\"op\":\"add\",\"path\":\"/pieces/horse/moves/-\"," +
            "\"value\":{\"kind\":\"jump\",\"to\":[1,0],\"block\":[],\"land\":\"any\",\"sym\":\"rotate4\"}}]}]}";

        /// <summary>正常流脚本（3 轮；T_eff = 1200+300+1800+400+900+120 = 4720）。</summary>
        static ScriptedTransport NormalScript() => new ScriptedTransport()
            .Tool("get_board_state", "{\"include\":[\"pieces\"]}", 1200, 300, callId: "c1")
            .Tool("apply_config_patch", PatchHorseSideStep, 1800, 400, callId: "c2")
            .Text("已把棋盘加宽到 8 列", 900, 120);

        /// <summary>顶格脚本（T_eff = 40000+8000+2000+300 = 50300 ⇒ f ≈ 302 ⇒ 恒 clamp 120）。</summary>
        static ScriptedTransport CapScript(string patch, string final) => new ScriptedTransport()
            .Tool("apply_config_patch", patch, 40000, 8000, callId: "cap1")
            .Tool("commit_preview", "{\"summary\":\"" + final + "\"}", 2000, 300, callId: "cap2");

        public static void Run()
        {
            var watch = Stopwatch.StartNew();

            var normal = NormalFlow();
            var confirm = ConfirmEndToEnd();
            var cap = CapPassThrough();
            var prefix = TwoRunPrefixStability();

            watch.Stop();
            ProbeEnv.Check(watch.Elapsed.TotalSeconds < 5.0,
                $"探针墙钟 {watch.Elapsed.TotalSeconds:0.00}s 超预算（< 5s）");

            Console.WriteLine(
                $"    · 正常流 seq=[{string.Join(",", normal.Sequence)}] T_eff={normal.TokenEff} karma={normal.Karma} " +
                $"roll={normal.Roll:0.0000} scope={normal.Scope} patch=pieces_red.horse.moves={normal.HorseMoves} schema=ok");
            Console.WriteLine(
                $"    · Confirm 落地：马 moves {confirm.MovesBefore}→{confirm.MovesAfter}，业力 {confirm.KarmaBefore}" +
                $"→{confirm.KarmaAfter}（cost={confirm.Cost}），改规栈深度={confirm.RuleUndoDepth}");
            Console.WriteLine(
                $"    · 顶格跑 capWarned={cap.CapWarned} 轮次={cap.Rounds} 请求数={cap.Requests} type={cap.Type} " +
                $"karma={cap.Karma}（Confirm 可执行={cap.Confirmable}）");
            Console.WriteLine($"    · 两跑前缀 SHA-256 相等={prefix.Identical}（system={prefix.SystemHash.Substring(0, 12)}…）");
            Console.WriteLine($"    · 探针墙钟 {watch.Elapsed.TotalSeconds:0.000}s（预算 5s）");
        }

        // ── ① 正常流 ────────────────────────────────────────────────

        sealed class NormalSummary
        {
            public List<string> Sequence;
            public int TokenEff;
            public int Karma;
            public double Roll;
            public string Scope;
            public int HorseMoves;
        }

        static NormalSummary NormalFlow()
        {
            var transport = NormalScript();
            var engine = AgentProbeKit.NewEngine(transport);
            var result = AgentProbeKit.Run(engine, "把棋盘加宽到 8 列");

            AgentAssert.Equal("applied", result.Type, "正常流结果类型");
            AgentAssert.Sequence(new[] { "get_board_state", "apply_config_patch" },
                AgentAssert.ToolNames(result), "正常流工具序列");
            AgentAssert.Equal(3, transport.Requests.Count, "正常流 LLM 轮次（mock 请求数）");
            AgentAssert.Equal(3, result.Rounds, "正常流 Rounds");

            // patch 落地 + Schema 硬校验复验（不是「写进去了就算」）
            var pieces = result.ModifiedConfigs?["pieces_red"] as JObject;
            AgentAssert.True(pieces != null, "正常流应有 pieces_red 切片变更");
            var horseMoves = pieces?["pieces"]?["horse"]?["moves"] as JArray;
            AgentAssert.True(horseMoves != null, "pieces_red.pieces.horse.moves 应存在");
            AgentAssert.Equal(2, horseMoves.Count, "马走法条数（原 1 条 + 新增横走 1 条）");
            var (ok, error) = SchemaValidator.HardValidateConfig("pieces_red", pieces);
            AgentAssert.True(ok, $"patch 落地后 Schema 复验失败：{error}");

            // token 账本：T_eff = Σ(prompt+completion)，thinking 已含在 completion
            var expectedTokens = (1200 + 300) + (1800 + 400) + (900 + 120);
            AgentAssert.Equal((long)expectedTokens, result.TokenEff, "T_eff（Σ prompt+completion）");
            AgentAssert.Equal(expectedTokens, (int)engine.Ledger.TokenEff, "账本 TokenEff 与结果一致");

            // 业力：独立推导 f + 引擎抽出的 roll + 作用域权重 ⇒ 必须逐值相等（不是「区间内」这种弱断言）
            AgentAssert.True(result.Karma != null && !result.Karma.ZeroWrite, "正常流应走 f 计价（非零写入路径）");
            var curve = engine.Formula.CurveValue(expectedTokens);
            var independent = AgentProbeKit.DesignContract.F(expectedTokens);
            AgentAssert.True(Math.Abs(curve - independent) < 1e-9,
                $"f(T) 与设计 §1.4 独立推导不一致：实现 {curve:0.000000} 期望 {independent:0.000000}");
            // E3-C：改既有棋子走法（add 到 /pieces/horse/moves/-）= rules 作用域（权重 3.35）。
            AgentAssert.Equal("rules", result.Scope, "正常流的主导作用域应为 rules（改规）");
            var expectedKarma = AgentProbeKit.DesignContract.ExpectedKarma(expectedTokens, result.Karma.Roll,
                scopeWeight: AgentProbeKit.DesignContract.ScopeWeightOf("rules"));
            AgentAssert.Equal(expectedKarma, result.EstimatedKarmaCost, "karma == clamp(round(f(T)×roll×scopeWeight))");
            AgentAssert.InRange(result.EstimatedKarmaCost, 1, 120, "karma 落 [1,120]");
            AgentAssert.InRange(result.Karma.Sigma, 0.8, 1.3, "σ 落 [0.8,1.3]");

            return new NormalSummary
            {
                Sequence = AgentAssert.ToolNames(result),
                TokenEff = (int)result.TokenEff,
                Karma = result.EstimatedKarmaCost,
                Roll = result.Karma.Roll,
                Scope = result.Scope,
                HorseMoves = horseMoves.Count,
            };
        }

        // ── ①b 两段式契约：Confirm 后对局真实改写 ────────────────────

        sealed class ConfirmSummary
        {
            public int MovesBefore, MovesAfter, KarmaBefore, KarmaAfter, Cost, RuleUndoDepth;
        }

        static ConfirmSummary ConfirmEndToEnd()
        {
            // 走真实 Samsara 门控：pieces_red 恒可用（t1b 才解锁的 board 作用域刻意不用，
            // 以免把「工具被门控拒绝」误读成「改规失败」——那属于 E1/L3 的用例）。
            var transport = new ScriptedTransport()
                .Tool("apply_config_patch", PatchHorseSideStep, 1800, 400, callId: "k1")
                .Tool("commit_preview", "{\"summary\":\"马可以横着走了\"}", 900, 200, callId: "k2");

            using (var harness = new CheatHarness(transport))
            {
                harness.SeedKarma(40);
                var silence = new ProgressCollector();

                var preview = harness.Controller
                    .SubmitAsync("让我的马可以横着走", CancellationToken.None, silence)
                    .GetAwaiter().GetResult();

                AgentAssert.True(preview != null, "Confirm 用例：preview 不得为 null");
                AgentAssert.True(preview.Ok, "Confirm 用例：preview 应可确认（Ok=true）");
                AgentAssert.True(preview.Cards.Count > 0, "Confirm 用例：应有 diff 卡片");
                AgentAssert.True(harness.Controller.LastAgentResult != null, "Confirm 用例：应记录 LastAgentResult");
                AgentAssert.Equal("applied", harness.Controller.LastAgentResult.Type, "Confirm 用例结果类型");

                var movesBefore = HorseMoveCount(harness);
                var karmaBefore = harness.Controller.Karma;

                var confirmed = harness.Controller.Confirm(preview);

                AgentAssert.True(confirmed.Applied, "Confirm 应 Applied=true");
                AgentAssert.Equal(preview.CostInfo?.Cost ?? -1, confirmed.Cost, "Confirm 计价 == preview 价");
                AgentAssert.Equal(karmaBefore + confirmed.Cost, confirmed.KarmaAfter,
                    "Confirm 后业力 == 前值 + 本次代价（唯一扣费点）");

                var movesAfter = HorseMoveCount(harness);
                AgentAssert.Equal(movesBefore + 1, movesAfter, "Confirm 后对局配置真实改写（马 moves +1）");
                AgentAssert.True(harness.Controller.RuleUndoDepth >= 1, "Confirm 后改规栈应入栈（可撤回）");

                return new ConfirmSummary
                {
                    MovesBefore = movesBefore,
                    MovesAfter = movesAfter,
                    KarmaBefore = karmaBefore,
                    KarmaAfter = confirmed.KarmaAfter,
                    Cost = confirmed.Cost,
                    RuleUndoDepth = harness.Controller.RuleUndoDepth,
                };
            }
        }

        static int HorseMoveCount(CheatHarness harness)
        {
            var moves = harness.Session.Config.PiecesA?["pieces"]?["horse"]?["moves"] as JArray;
            AgentAssert.True(moves != null, "对局配置里应有 pieces.horse.moves");
            return moves.Count;
        }

        // ── ② 顶格警示不拦截 + 终值 120 可 Confirm ───────────────────

        sealed class CapSummary
        {
            public bool CapWarned;
            public int Rounds;
            public int Requests;
            public int Karma;
            public string Type;
            public bool Confirmable;
        }

        static CapSummary CapPassThrough()
        {
            var transport = CapScript(PatchHorseSideStep, "大手笔改完了");
            var engine = AgentProbeKit.NewEngine(transport);
            var progress = new ProgressCollector();
            var result = AgentProbeKit.Run(engine, "大改棋盘和规则", progress: progress);

            AgentAssert.True(result.CapWarned, "顶格跑应置 CapWarning（投射 ≥ 单次上限）");
            AgentAssert.Equal(2, result.Rounds, "顶格跑应走满 2 轮（未被提前拒绝）");
            AgentAssert.Equal(2, transport.Requests.Count, "顶格跑 Transport 请求数应为 2（循环未提前停）");
            AgentAssert.Equal("applied", result.Type, "顶格跑结果应为 applied（非 error/rejected）");
            AgentAssert.Equal(120, result.EstimatedKarmaCost, "顶格跑终值应恰为 120（clamp 放行）");
            AgentAssert.True(result.Karma != null && result.Karma.Karma == 120, "KarmaResult 终值应恰为 120");
            AgentAssert.True(result.Karma.SingleMax == 120, "单次上限应为 120");

            var warned = progress.Snapshot.FindAll(p => p.CapWarning && p.Growing);
            AgentAssert.True(warned.Count > 0, "progress 中应有 CapWarning=true 且 Growing=true 的轮次（警示已发出）");
            AgentAssert.True(warned.Exists(p => p.ProjectedKarma >= 120),
                "顶格轮投射业力应 ≥120（警示判据）");
            var last = progress.Snapshot[progress.Snapshot.Count - 1];
            AgentAssert.True(!last.Growing, "收尾轮 progress 应 Growing=false");
            AgentAssert.Equal("wrap-up", last.CurrentPhase, "收尾轮 phase");

            // 终值 120 必须可确认（裁决 #3：clamp 放行，不是拒绝）
            var confirmTransport = CapScript(PatchHorseSideStep, "马可以横着走了");
            bool confirmable;
            using (var harness = new CheatHarness(confirmTransport))
            {
                harness.SeedKarma(0);
                var preview = harness.Controller
                    .SubmitAsync("造一个能拐弯的棋子", CancellationToken.None, new ProgressCollector())
                    .GetAwaiter().GetResult();
                AgentAssert.True(preview != null && preview.Ok, "顶格终值应可 Confirm（preview.Ok）");
                AgentAssert.Equal(120, preview.CostInfo?.Cost ?? -1, "顶格 preview 价应恰为 120（不得 >120）");
                AgentAssert.True(harness.Controller.LastAgentResult.CapWarned, "Controller 侧也应看到顶格警示");
                var confirmed = harness.Controller.Confirm(preview);
                AgentAssert.True(confirmed.Applied, "顶格终值 Confirm 应 Applied=true（clamp 放行）");
                AgentAssert.Equal(120, confirmed.Cost, "Confirm 代价应为 120");
                confirmable = confirmed.Applied;
            }

            return new CapSummary
            {
                CapWarned = result.CapWarned,
                Rounds = result.Rounds,
                Requests = transport.Requests.Count,
                Karma = result.EstimatedKarmaCost,
                Type = result.Type,
                Confirmable = confirmable,
            };
        }

        // ── ③ 两跑副本前缀字节级一致 ─────────────────────────────────

        sealed class PrefixSummary
        {
            public bool Identical;
            public string SystemHash;
        }

        static PrefixSummary TwoRunPrefixStability()
        {
            var a = new ScriptedTransport();
            var b = new ScriptedTransport();
            AppendNormal(a);
            AppendNormal(b);

            AgentProbeKit.Run(AgentProbeKit.NewEngine(a, seed: 20260101), "把棋盘加宽到 8 列");
            AgentProbeKit.Run(AgentProbeKit.NewEngine(b, seed: 987654321), "把棋盘加宽到 8 列");

            AgentAssert.True(a.Requests.Count > 0 && b.Requests.Count > 0, "两跑副本应各至少一轮请求");
            AgentAssert.Equal(a.Requests[0].SystemHash, b.Requests[0].SystemHash, "两跑副本 system 段 SHA-256");
            AgentAssert.Equal(a.Requests[0].PrefixHash, b.Requests[0].PrefixHash, "两跑副本首轮 messages SHA-256");
            AgentAssert.Equal(a.Requests[0].ToolsJson, b.Requests[0].ToolsJson, "两跑副本工具 schema 序列化");
            AgentAssert.Equal(AgentProbeKit.Sha256(a.Requests[0].ToolsJson),
                AgentProbeKit.Sha256(b.Requests[0].ToolsJson), "两跑副本工具 schema SHA-256");

            return new PrefixSummary { Identical = true, SystemHash = a.Requests[0].SystemHash };
        }

        static void AppendNormal(ScriptedTransport t)
        {
            t.Tool("get_board_state", "{\"include\":[\"pieces\"]}", 1200, 300, callId: "c1")
             .Tool("apply_config_patch", PatchHorseSideStep, 1800, 400, callId: "c2")
             .Text("已把棋盘加宽到 8 列", 900, 120);
        }

        /// <summary>线程安全进度收集器（引擎可能在其它线程 Report）。</summary>
        public sealed class ProgressCollector : IProgress<AgentProgress>
        {
            readonly object _lock = new object();
            readonly List<AgentProgress> _items = new List<AgentProgress>();

            public void Report(AgentProgress value)
            {
                if (value == null) return;
                lock (_lock) _items.Add(value);
            }

            public List<AgentProgress> Snapshot
            {
                get { lock (_lock) return new List<AgentProgress>(_items); }
            }
        }
    }
}
