using System;
using System.Collections.Generic;
using System.IO;
using ChessSage.Core.Agent;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E0-S1：token 账本（T_eff 计量 + 日桶 + 持久化 + requests 遥测环）。
    /// 期望值独立推导：T_eff = Σ(prompt+completion)（thinking 全额含在 completion，DeepSeek 计费口径）。
    /// </summary>
    [TestFixture]
    public sealed class TokenLedgerTests
    {
        static LlmUsage Usage(int prompt, int completion, int reasoning = 0, int cached = 0)
            => new LlmUsage { PromptTokens = prompt, CompletionTokens = completion, ReasoningTokens = reasoning, CachedPromptTokens = cached };

        [Test]
        public void Add_AccumulatesTokenEff_ThinkingCountedOnce()
        {
            var ledger = new TokenLedger();
            ledger.Add(Usage(100, 200, reasoning: 150));
            ledger.Add(Usage(50, 80, reasoning: 60));
            ledger.Add(Usage(200, 300, reasoning: 250));

            // T_eff = (100+200)+(50+80)+(200+300) = 930；reasoning 是 completion 子集，不得另计。
            Assert.AreEqual(930, ledger.TokenEff, "T_eff = Σ(prompt+completion)");
            Assert.AreEqual(350, ledger.TotalPromptTokens);
            Assert.AreEqual(580, ledger.TotalCompletionTokens);
            Assert.AreEqual(460, ledger.TotalReasoningTokens, "thinking 分列仅作遥测，不进 T_eff");
            Assert.AreEqual(3, ledger.TotalCalls, "每轮 usage 恰好一次入账");
        }

        [Test]
        public void Add_RejectsNegativeUsage()
        {
            var ledger = new TokenLedger();
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Add(Usage(-1, 10)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Add(Usage(10, -1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Add(Usage(10, 0, reasoning: -1)));
        }

        [Test]
        public void DailyBuckets_FollowInjectedClock()
        {
            var day = new DateTime(2026, 10, 23, 8, 0, 0);
            var ledger = new TokenLedger(() => day);

            ledger.Add(Usage(100, 200));   // 2026-10-23
            ledger.Add(Usage(10, 20));     // 同日累计
            day = new DateTime(2026, 10, 24, 23, 59, 59);
            ledger.Add(Usage(5, 5));       // 跨日新桶

            Assert.AreEqual(2, ledger.DailyCount, "两个日桶");
            Assert.AreEqual(110, ledger.GetDaily(2026, 10, 23).PromptTokens);
            Assert.AreEqual(220, ledger.GetDaily(2026, 10, 23).CompletionTokens);
            Assert.AreEqual(2, ledger.GetDaily(2026, 10, 23).Calls);
            Assert.AreEqual(5, ledger.GetDaily(2026, 10, 24).PromptTokens);
            Assert.AreEqual(5, ledger.GetDaily(2026, 10, 24).CompletionTokens);
            Assert.AreEqual(1, ledger.GetDaily(2026, 10, 24).Calls);
        }

        [Test]
        public void Persistence_RoundTrip_IsStable()
        {
            var clock = new DateTime(2026, 10, 23, 12, 0, 0);
            var ledger = new TokenLedger(() => clock);
            ledger.Add(Usage(1000, 2000, reasoning: 1200, cached: 400));
            ledger.Add(Usage(500, 800, reasoning: 300, cached: 200));

            var r1 = ledger.BeginRequest("abc123def456");
            r1.Rounds = 3; r1.TotalMs = 12000; r1.Karma = 55; r1.Roll = 1.05; r1.Surged = false;
            r1.Writes = 1; r1.Tools = new[] { "get_board_state", "move_piece", "commit_preview" };
            r1.EffortProfile = "low×2+high"; r1.TimedOut = false; r1.CapWarned = false;
            r1.RoundMs = new[] { 3000, 4000, 5000 }; r1.TtftMs = 2200;
            ledger.EndRequest(r1);

            var first = ledger.ToJson().ToString(Newtonsoft.Json.Formatting.None);
            var restored = TokenLedger.FromJson(JObject.Parse(first), () => clock);
            var second = restored.ToJson().ToString(Newtonsoft.Json.Formatting.None);

            Assert.AreEqual(first, second, "ToJson→FromJson→ToJson 必须字节级一致");
            Assert.AreEqual(ledger.TokenEff, restored.TokenEff);
            Assert.AreEqual(ledger.TotalCalls, restored.TotalCalls);
            Assert.AreEqual(1, restored.Requests.Count, "requests 应完整往返");
            var back = restored.Requests[0];
            Assert.AreEqual("abc123def456", back.CommandHash);
            Assert.AreEqual(3, back.Rounds);
            Assert.AreEqual(12000, back.TotalMs);
            Assert.AreEqual(55, back.Karma);
            Assert.AreEqual(1.05, back.Roll);
            Assert.AreEqual(1, back.Writes);
            CollectionAssert.AreEqual(new[] { "get_board_state", "move_piece", "commit_preview" }, back.Tools);
            Assert.AreEqual("low×2+high", back.EffortProfile);
        }

        [Test]
        public void Persistence_LegacyFile_LoadsWithoutRequests()
        {
            // 既有 token_stats.json（无 requests 字段）必须无损加载——E1 迁移的兼容底线。
            var legacy = JObject.Parse(@"{
                'total_prompt_tokens': 12000,
                'total_completion_tokens': 30000,
                'total_calls': 7,
                'daily_stats': { '2026-10-20': { 'prompt_tokens': 12000, 'completion_tokens': 30000, 'calls': 7 } },
                'token_price_per_1k': 0.0015
            }".Replace('\'', '"'));

            var ledger = TokenLedger.FromJson(legacy);
            Assert.AreEqual(12000, ledger.TotalPromptTokens);
            Assert.AreEqual(30000, ledger.TotalCompletionTokens);
            Assert.AreEqual(42000, ledger.TokenEff);
            Assert.AreEqual(7, ledger.TotalCalls);
            Assert.AreEqual(0, ledger.Requests.Count);
            Assert.AreEqual(12000, ledger.GetDaily(2026, 10, 20).PromptTokens);
        }

        [Test]
        public void Requests_RingKeepsLast500()
        {
            var ledger = new TokenLedger(() => new DateTime(2026, 10, 23));
            const int N = 600;
            for (int i = 0; i < N; i++)
            {
                var r = ledger.BeginRequest($"cmd-{i:000}");
                ledger.EndRequest(r);
            }
            Assert.AreEqual(500, ledger.Requests.Count, "环形保留最近 500 条");
            Assert.AreEqual("cmd-100", ledger.Requests[0].CommandHash, "最旧被挤出");
            Assert.AreEqual("cmd-599", ledger.Requests[499].CommandHash, "最新在尾");
        }

        [Test]
        public void Requests_SerializedFields_MatchTelemetrySchema()
        {
            var ledger = new TokenLedger(() => new DateTime(2026, 10, 23, 9, 30, 0));
            var r = ledger.BeginRequest("deadbeefcafe");   // 真实时序：Begin → 各轮 Add → End
            ledger.Add(Usage(1000, 2000, reasoning: 1200, cached: 400));
            r.Rounds = 4; r.TtftMs = 2100; r.RoundMs = new[] { 1000, 2000, 3000, 4000 };
            r.TotalMs = 15000; r.Karma = 91; r.Roll = 1.3; r.Surged = true; r.Writes = 2;
            r.Tools = new[] { "get_board_state", "apply_config_patch", "commit_preview" };
            r.EffortProfile = "low×3+high"; r.TimedOut = true; r.CapWarned = true;
            ledger.EndRequest(r);

            var req = (JObject)ledger.ToJson()["requests"][0];
            var expectedKeys = new[]
            {
                "ts", "command_hash", "rounds", "ttft_ms", "round_ms", "total_ms",
                "prompt_tokens", "completion_tokens", "reasoning_tokens", "cached_prompt_tokens",
                "prefix_hit_rate", "t_eff", "karma", "roll", "surged", "writes", "tools",
                "effort_profile", "timed_out", "cap_warned",
            };
            foreach (var key in expectedKeys)
                Assert.IsTrue(req.ContainsKey(key), $"§13.7 字段缺失：{key}");

            Assert.AreEqual("2026-10-23T09:30:00", req["ts"].Value<string>(), "ts 为本地 ISO（仅遥测，不进 prompt）");
            Assert.AreEqual(1000, req["prompt_tokens"].Value<int>());
            Assert.AreEqual(2000, req["completion_tokens"].Value<int>());
            Assert.AreEqual(1200, req["reasoning_tokens"].Value<int>());
            Assert.AreEqual(400, req["cached_prompt_tokens"].Value<int>());
            Assert.AreEqual(0.4, req["prefix_hit_rate"].Value<double>(), 1e-9, "Σcached/Σprompt");
            Assert.AreEqual(3000, req["t_eff"].Value<int>(), "本提交 T_eff");
            Assert.AreEqual(91, req["karma"].Value<int>());
            Assert.AreEqual(1.3, req["roll"].Value<double>());
            Assert.AreEqual(true, req["surged"].Value<bool>());
            Assert.AreEqual(2, req["writes"].Value<int>());
            CollectionAssert.AreEqual(new[] { "get_board_state", "apply_config_patch", "commit_preview" },
                req["tools"].ToObject<string[]>());
            Assert.AreEqual("low×3+high", req["effort_profile"].Value<string>());
            Assert.AreEqual(true, req["timed_out"].Value<bool>());
            Assert.AreEqual(true, req["cap_warned"].Value<bool>());
        }

        [Test]
        public void EndRequest_DerivesTokenEffAndPrefixHitRate()
        {
            // 派生字段以账本口径为准（防调用方填错污染校准回归）。
            var ledger = new TokenLedger();
            var r = ledger.BeginRequest("hash00000001");       // Begin → 各轮 Add → End
            ledger.Add(Usage(1000, 2000, reasoning: 1200, cached: 400));
            ledger.Add(Usage(500, 800, reasoning: 100, cached: 100));
            r.PromptTokens = 999; r.CompletionTokens = 999; r.CachedPromptTokens = 999; // 调用方乱填
            ledger.EndRequest(r);

            Assert.AreEqual(4300, r.TokenEff, "t_eff 应取本次提交的 Σ(prompt+completion)");
            Assert.AreEqual(500.0 / 1500.0, r.PrefixHitRate, 1e-9, "prefix_hit_rate = 500/1500");
        }

        [Test]
        public void BeginRequest_RejectsEmptyHash()
        {
            var ledger = new TokenLedger();
            Assert.Throws<ArgumentException>(() => ledger.BeginRequest(""));
            Assert.Throws<ArgumentException>(() => ledger.BeginRequest(null));
        }

        [Test]
        public void EndRequest_RejectsForeignRecord()
        {
            var ledger = new TokenLedger();
            var other = new TokenLedger();
            var r = other.BeginRequest("foreign00001");
            Assert.Throws<ArgumentException>(() => ledger.EndRequest(r), "他人账本的记录不得入栈");
        }

        [Test]
        public void GetStats_MatchesLegacyDisplayShape()
        {
            var now = new DateTime(2026, 10, 23, 10, 0, 0);
            var ledger = new TokenLedger(() => now);
            ledger.Add(Usage(1000, 2000, reasoning: 800, cached: 500));
            ledger.Add(Usage(500, 800, reasoning: 200, cached: 100));

            var stats = ledger.GetStats();
            Assert.AreEqual(1500, stats["total_prompt_tokens"].Value<long>());
            Assert.AreEqual(2800, stats["total_completion_tokens"].Value<long>());
            Assert.AreEqual(4300, stats["total_tokens"].Value<long>());
            Assert.AreEqual(2, stats["total_calls"].Value<long>());
            Assert.AreEqual(1500, stats["today_prompt_tokens"].Value<long>());
            Assert.AreEqual(2800, stats["today_completion_tokens"].Value<long>());
            Assert.AreEqual(4300, stats["today_tokens"].Value<long>());
            Assert.AreEqual(2, stats["today_calls"].Value<long>());
            // 0.0015 美元/千 token：4300 × 0.0015 / 1000 = 0.00645 ⇒ 4 位小数 0.0065（银行家舍入由 Math.Round 默认）
            Assert.AreEqual(Math.Round(4300 * 0.0015 / 1000, 4), stats["estimated_cost_usd"].Value<double>());
            Assert.AreEqual(0.0015, stats["token_price_per_1k_usd"].Value<double>());
        }

        [Test]
        public void SaveAndLoad_RoundTripsThroughDisk()
        {
            var root = Path.Combine(Path.GetTempPath(), "chesssage-ledger-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new DateTime(2026, 10, 23, 14, 0, 0);
                var ledger = new TokenLedger(() => clock);
                ledger.Add(Usage(700, 900, reasoning: 400, cached: 300));
                var r = ledger.BeginRequest("disk00000001");
                r.Karma = 32; r.Rounds = 3; r.Writes = 1;
                ledger.EndRequest(r);
                ledger.Save(root);

                var expectedPath = Path.Combine(root, "configs", "token_stats.json");
                Assert.IsTrue(File.Exists(expectedPath), "持久化路径须为 {BaseDir}/configs/token_stats.json");

                var restored = TokenLedger.Load(root, () => clock);
                Assert.AreEqual(ledger.TokenEff, restored.TokenEff);
                Assert.AreEqual(1, restored.Requests.Count);
                Assert.AreEqual(32, restored.Requests[0].Karma);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void Load_MissingFile_ReturnsEmptyLedger()
        {
            var root = Path.Combine(Path.GetTempPath(), "chesssage-ledger-empty-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.IsFalse(Directory.Exists(root));
                var ledger = TokenLedger.Load(root);
                Assert.AreEqual(0, ledger.TokenEff);
                Assert.AreEqual(0, ledger.TotalCalls);
                Assert.AreEqual(0, ledger.Requests.Count);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void Load_CorruptFile_ReturnsEmptyLedger()
        {
            // 损坏 JSON 不得崩溃（遥测文件可丢失，主流程不可断）——E0-S5 同款兜底哲学。
            var root = Path.Combine(Path.GetTempPath(), "chesssage-ledger-bad-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "configs"));
                File.WriteAllText(Path.Combine(root, "configs", "token_stats.json"), "{ 这不是 json");
                var ledger = TokenLedger.Load(root);
                Assert.AreEqual(0, ledger.TokenEff);
                Assert.AreEqual(0, ledger.Requests.Count);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void NewLedger_SerializesLegacyShape_WithEmptyRequests()
        {
            var json = new TokenLedger().ToJson();
            Assert.AreEqual(0, json["total_prompt_tokens"].Value<int>());
            Assert.AreEqual(0, json["total_completion_tokens"].Value<int>());
            Assert.AreEqual(0, json["total_calls"].Value<int>());
            Assert.IsNotNull(json["daily_stats"]);
            Assert.AreEqual(0.0015, json["token_price_per_1k"].Value<double>());
            Assert.IsNotNull(json["requests"]);
            Assert.AreEqual(0, (json["requests"] as JArray).Count);
        }
    }
}
