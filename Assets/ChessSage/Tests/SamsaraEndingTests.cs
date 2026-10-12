using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 结局系统测试（EndingSystem）。
    ///
    /// 覆盖：五种结局达成条件、互斥性与判定优先级（exposed &gt; true_me &gt; enlightenment /
    /// corruption / samsara → alignment 兜底）、边界条件（业力 100/200、|悟道-堕落| 3、悟道值 9）、
    /// 无 final_choice 的兜底、no_cheat_final 的作用。
    /// </summary>
    public sealed class SamsaraEndingTests
    {
        static readonly string[] Realms = { "hell", "hungry", "animal", "human", "asura", "heaven" };

        [Test]
        public void EndingCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_endings");
            foreach (var tk in (JArray)fx["cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                ApplySetup(f, (JObject)c["setup"]);

                var fc = c["setup"]["final_choice"].Type == JTokenType.Null
                    ? null : c["setup"]["final_choice"].Value<string>();
                var flags = (JObject)c["flags"];

                Assert.AreEqual(flags["exposed"].Value<bool>(), f.Engine.Endings.CheckExposedEnding(), name + ": exposed");
                Assert.AreEqual(flags["true_me"].Value<bool>(), f.Engine.Endings.CheckTrueMeEnding(fc), name + ": true_me");
                Assert.AreEqual(flags["enlightenment"].Value<bool>(), f.Engine.Endings.CheckEnlightenmentEnding(fc), name + ": enlightenment");
                Assert.AreEqual(flags["corruption"].Value<bool>(), f.Engine.Endings.CheckCorruptionEnding(fc), name + ": corruption");
                Assert.AreEqual(flags["samsara"].Value<bool>(), f.Engine.Endings.CheckSamsaraEnding(fc), name + ": samsara");

                var expectedId = c["expected_ending_id"].Type == JTokenType.Null
                    ? null : c["expected_ending_id"].Value<string>();
                var actualId = f.Engine.EvaluateEndingId(fc, c["setup"]["no_cheat_final"].Value<bool>());
                Assert.AreEqual(expectedId, actualId, name + ": 判定结局 id");
            }
        }

        [Test]
        public void EndingsMeta_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_endings");
            using var f = new SamsaraFixture();
            foreach (var tk in ((JObject)fx["endings_meta"]).Properties())
            {
                var data = f.Engine.Endings.GetEndingData(tk.Name);
                Assert.AreEqual(tk.Value["name"].Value<string>(), data["name"].Value<string>(), tk.Name + ": name");
                Assert.AreEqual(tk.Value["type"].Value<string>(), data["type"].Value<string>(), tk.Name + ": type");
            }
        }

        [Test]
        public void Preview_MatchesPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_endings");
            var c = (JObject)fx["preview_enlight_case"];
            using var f = new SamsaraFixture();
            f.Set("level_karma", c["karma"].Value<int>());
            foreach (var p in ((JObject)c["align"]).Properties())
                f.Obj("alignment")[p.Name] = p.Value;

            var preview = f.Engine.Endings.GetEndingPreview();
            var exp = (JObject)c["expected"];
            Assert.AreEqual(exp["predicted"].Value<string>(), preview["predicted"].Value<string>(), "preview.predicted");
            Assert.AreEqual(exp["name"].Value<string>(), preview["name"].Value<string>(), "preview.name");
        }

        // ── 优先级 / 互斥（独立断言） ───────────────────────────────

        [Test]
        public void Priority_ExposedBeatsEverything()
        {
            using var f = new SamsaraFixture();
            // 同时满足 exposed / true_me / enlightenment 条件 → exposed 优先
            f.Set("prayer_count", 1);
            f.Obj("detection_state")["exposure_path_triggered"] = true;
            f.Obj("tiandao_boss_state")["defeated"] = true;
            foreach (var r in Realms) f.Obj("memory_fragments_unlocked")[r] = true;
            f.Obj("alignment")["enlightenment"] = 9;
            f.Set("level_karma", 50);

            Assert.AreEqual("exposed", f.Engine.EvaluateEndingId("enlightenment", true), "识破结局优先级最高");
        }

        [Test]
        public void Priority_TrueMeBeatsEnlightenment()
        {
            using var f = new SamsaraFixture();
            foreach (var r in Realms) f.Obj("memory_fragments_unlocked")[r] = true;
            f.Obj("alignment")["enlightenment"] = 9;
            f.Set("level_karma", 50);

            Assert.IsTrue(f.Engine.Endings.CheckTrueMeEnding("enlightenment"), "真我条件满足");
            Assert.IsTrue(f.Engine.Endings.CheckEnlightenmentEnding("enlightenment"), "悟道条件亦满足");
            Assert.AreEqual("true_me", f.Engine.EvaluateEndingId("enlightenment", true), "真我优先级高于悟道");
        }

        [Test]
        public void NoCheatFinal_False_SkipsTrueMe()
        {
            using var f = new SamsaraFixture();
            foreach (var r in Realms) f.Obj("memory_fragments_unlocked")[r] = true;
            f.Obj("alignment")["enlightenment"] = 9;
            f.Set("level_karma", 50);

            Assert.AreEqual("enlightenment", f.Engine.EvaluateEndingId("enlightenment", false), "最终战有作弊则跳过真我");
        }

        [Test]
        public void PrayerWithoutBossBattle_CannotDetermine()
        {
            using var f = new SamsaraFixture();
            f.Set("prayer_count", 1);
            f.Obj("alignment")["enlightenment"] = 5;
            f.Set("level_karma", 50);
            Assert.IsNull(f.Engine.EvaluateEndingId(null, true), "已祈求但未完成天道Boss战 → 无法判定");
        }

        [Test]
        public void FallbackByAlignment_WhenNoFinalChoice()
        {
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["enlightenment"] = 5;
                f.Set("level_karma", 150); // 业力 150 使悟道/轮回条件不满足 → 走兜底
                Assert.AreEqual("enlightenment", f.Engine.EvaluateEndingId(null, true), "兜底：悟道值高");
            }
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["corruption"] = 5;
                f.Set("level_karma", 150);
                Assert.AreEqual("corruption", f.Engine.EvaluateEndingId(null, true), "兜底：堕落值高");
            }
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["enlightenment"] = 3;
                f.Obj("alignment")["corruption"] = 3;
                Assert.AreEqual("samsara", f.Engine.EvaluateEndingId(null, true), "兜底：均衡 → 轮回");
            }
        }

        [Test]
        public void KarmaBoundaries_AreRespected()
        {
            // 悟道：业力必须 < 100
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["enlightenment"] = 5;
                f.Set("level_karma", 99);
                Assert.IsTrue(f.Engine.Endings.CheckEnlightenmentEnding("enlightenment"), "业力 99");
            }
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["enlightenment"] = 5;
                f.Set("level_karma", 100);
                Assert.IsFalse(f.Engine.Endings.CheckEnlightenmentEnding("enlightenment"), "业力 100 阻断悟道");
            }
            // 堕落：业力必须 > 200
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["corruption"] = 5;
                f.Set("level_karma", 201);
                Assert.IsTrue(f.Engine.Endings.CheckCorruptionEnding("corruption"), "业力 201");
            }
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["corruption"] = 5;
                f.Set("level_karma", 200);
                Assert.IsFalse(f.Engine.Endings.CheckCorruptionEnding("corruption"), "业力 200 阻断堕落");
            }
        }

        [Test]
        public void AlignmentBoundaries_AreRespected()
        {
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["enlightenment"] = 3; // e-c = 3 → 满足
                f.Set("level_karma", 50);
                Assert.IsTrue(f.Engine.Endings.CheckEnlightenmentEnding("enlightenment"), "e-c=3 满足");
            }
            using (var f = new SamsaraFixture())
            {
                f.Obj("alignment")["enlightenment"] = 2; // e-c = 2 → 不满足
                f.Set("level_karma", 50);
                Assert.IsFalse(f.Engine.Endings.CheckEnlightenmentEnding("enlightenment"), "e-c=2 不满足");
            }
        }

        static void ApplySetup(SamsaraFixture f, JObject setup)
        {
            f.Set("prayer_count", setup["prayer"].Value<int>());
            f.Set("level_karma", setup["karma"].Value<int>());
            foreach (var p in ((JObject)setup["align"]).Properties())
                f.Obj("alignment")[p.Name] = p.Value;
            if (setup["frags_all"].Value<bool>())
                foreach (var r in Realms) f.Obj("memory_fragments_unlocked")[r] = true;
            if (setup["realms_all"].Value<bool>())
                foreach (var r in Realms) f.Obj("realm_progress")[r]["completed"] = true;
            if (setup["exposure"].Value<bool>())
                f.Obj("detection_state")["exposure_path_triggered"] = true;
            if (setup["boss_defeated"].Value<bool>())
                f.Obj("tiandao_boss_state")["defeated"] = true;
        }
    }
}
