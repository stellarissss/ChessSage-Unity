using System.Collections.Generic;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 技能树测试（SkillSystem + SamsaraState.GetSkillModifiers）。
    ///
    /// 覆盖：4 分支 × 3 层结构、前置解锁条件、非法解锁被拒、点数消耗、
    /// 解锁效果数值（修饰符）、解锁前后的可用列表与作弊分类。
    /// </summary>
    public sealed class SamsaraSkillTests
    {
        static readonly string[] BranchIds = { "karma_capacity", "stealth", "cheat_mastery", "realm_insight" };

        [Test]
        public void TreeShape_MatchesPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_skills");
            using var f = new SamsaraFixture();
            var tree = f.Engine.Skills.GetSkillTree();

            var branches = (IEnumerable<JToken>)fx["branches"];
            var branchList = new List<string>();
            foreach (var b in branches) branchList.Add(b.Value<string>());
            Assert.AreEqual(branchList, new List<string>(BranchIds), "分支数量/顺序");

            var shape = (JObject)fx["tree_shape"];
            foreach (var branchProp in shape.Properties())
            {
                var branch = branchProp.Name;
                var tiers = (JObject)branchProp.Value;
                foreach (var tierProp in tiers.Properties())
                {
                    var tier = tierProp.Name;
                    var expectedIds = new List<string>();
                    foreach (var id in (JArray)tierProp.Value) expectedIds.Add(id.Value<string>());

                    var actualIds = TierIds(tree, branch, tier);
                    Assert.AreEqual(expectedIds, actualIds, $"{branch}.tier{tier} 技能 id");
                }
            }
        }

        [Test]
        public void Costs_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_skills");
            using var f = new SamsaraFixture();
            var tree = f.Engine.Skills.GetSkillTree();
            foreach (var prop in ((JObject)fx["costs"]).Properties())
            {
                var id = prop.Name;
                Assert.AreEqual(prop.Value.Value<int>(), FindCost(tree, id), $"{id} 消耗");
            }
        }

        [Test]
        public void UnlockCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_skills");
            foreach (var tk in (JArray)fx["unlock_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.SetSkills(ToStrings((JArray)c["pre_skills"]));
                f.Set("skill_points", c["skill_points"].Value<int>());

                var ok = f.Engine.Skills.UnlockSkill(c["skill_id"].Value<string>(), c["tier"].Value<int>());

                Assert.AreEqual(c["expected_success"].Value<bool>(), ok, name + ": 解锁结果");
                Assert.AreEqual(c["expected_points_after"].Value<int>(), f.Engine.GetSkillPoints(), name + ": 技能点");
                Assert.AreEqual(c["expected_unlocked"].Value<bool>(),
                    f.Engine.State.IsSkillUnlocked(c["skill_id"].Value<string>()), name + ": 解锁状态");
            }
        }

        [Test]
        public void AvailableSkills_FromFresh_MatchesPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_skills");
            using var f = new SamsaraFixture();
            var ids = new List<string>();
            foreach (var t in f.Engine.Skills.GetAvailableSkills()) ids.Add(t["id"].Value<string>());
            ids.Sort(System.StringComparer.Ordinal);

            var expected = new List<string>();
            foreach (var t in (JArray)fx["available_from_fresh"]) expected.Add(t.Value<string>());
            CollectionAssert.AreEqual(expected, ids, "初始可用技能列表（排除默认已解锁的 stealth_t1）");
        }

        [Test]
        public void AllowedClassifications_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_skills");
            var exp = (JObject)fx["allowed_classifications"];

            using (var f = new SamsaraFixture())
            {
                f.SetSkills(new[] { "stealth_t1" });
                CollectionAssert.AreEqual(ToStrings((JArray)exp["fresh"]), Sorted(f), "默认分类");
            }
            using (var f = new SamsaraFixture())
            {
                f.SetSkills(new[] { "stealth_t1", "cheat_mastery_t1a" });
                CollectionAssert.AreEqual(ToStrings((JArray)exp["with_t1a"]), Sorted(f), "解锁 C+ 后");
            }
            using (var f = new SamsaraFixture())
            {
                f.SetSkills(new[] { "stealth_t1", "cheat_mastery_t1a", "cheat_mastery_t1b" });
                CollectionAssert.AreEqual(ToStrings((JArray)exp["with_t1a_t1b"]), Sorted(f), "解锁 C+ 与 D 后");
            }
        }

        [Test]
        public void ModifierCases_MatchPythonGolden()
        {
            var fx = SamsaraFixture.Fixture("samsara_skills");
            foreach (var tk in (JArray)fx["modifier_cases"])
            {
                var c = (JObject)tk;
                var name = c["name"].Value<string>();
                using var f = new SamsaraFixture();
                f.SetSkills(ToStrings((JArray)c["skills"]));
                var used = ToStrings((JArray)c["used_one_time"]);
                if (used.Count > 0) f.Set("one_time_skill_usage", BuildUsage(used));

                SamsaraFixture.AssertModifiers((JObject)c["expected"], f.Engine.State.GetSkillModifiers(), name);
            }
        }

        // ── 结构性 / 前置条件（独立断言，不依赖夹具） ────────────────

        [Test]
        public void Tier2_RequiresTier1_InSameBranch()
        {
            using var f = new SamsaraFixture();
            f.SetSkills(new[] { "stealth_t1" }); // 有 stealth T1，但 karma_capacity T1 未解锁
            f.Set("skill_points", 5);

            Assert.IsFalse(f.Engine.Skills.UnlockSkill("karma_capacity_t2b", 2), "跨分支前置不满足应被拒");
            Assert.AreEqual(5, f.Engine.GetSkillPoints(), "拒绝时不应扣点");

            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t1", 1), "先解锁 T1");
            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t2b", 2), "T1 解锁后 T2 可解锁");
        }

        [Test]
        public void Unlock_IsIdempotent_AndWhitelistsOnlyTreeIds()
        {
            using var f = new SamsaraFixture();
            f.SetSkills(new[] { "stealth_t1" });
            f.Set("skill_points", 5);

            Assert.IsFalse(f.Engine.Skills.UnlockSkill("stealth_t1", 1), "重复解锁应被拒（幂等短路）");
            Assert.AreEqual(5, f.Engine.GetSkillPoints(), "重复解锁不扣点");
            Assert.IsFalse(f.Engine.Skills.UnlockSkill("not_a_skill", 1), "不在技能树中的 id 应被拒");
        }

        static List<string> Sorted(SamsaraFixture f)
        {
            var list = new List<string>(f.Engine.State.GetAllowedClassifications());
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }

        static JObject BuildUsage(IEnumerable<string> ids)
        {
            var o = new JObject();
            foreach (var id in ids) o[id] = new JObject { ["used_at"] = "2026-01-01T00:00:00", ["context"] = "test" };
            return o;
        }

        static List<string> TierIds(JObject tree, string branch, string tier)
        {
            var t = (JObject)tree[branch]["tiers"][tier];
            var list = new List<string>();
            if (t["options"] != null)
                foreach (var o in (JArray)t["options"]) list.Add(o["id"].Value<string>());
            else
                list.Add(t["id"].Value<string>());
            return list;
        }

        static int FindCost(JObject tree, string id)
        {
            foreach (var branchProp in tree.Properties())
            {
                var tiers = (JObject)branchProp.Value["tiers"];
                foreach (var tierProp in tiers.Properties())
                {
                    var t = (JObject)tierProp.Value;
                    if (t["options"] != null)
                    {
                        foreach (var o in (JArray)t["options"])
                            if (o["id"].Value<string>() == id) return o["cost"].Value<int>();
                    }
                    else if (t["id"].Value<string>() == id) return t["cost"].Value<int>();
                }
            }
            return -1;
        }

        static List<string> ToStrings(JArray arr)
        {
            var list = new List<string>();
            if (arr != null)
                foreach (var t in arr) list.Add(t.Value<string>());
            return list;
        }
    }
}
