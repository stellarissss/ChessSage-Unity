using System.Collections.Generic;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// 技能树「视图」测试（T5）：覆盖 SkillSystem.GetSkillTreeView / MeetsPrerequisites
    /// 与 SamsaraEngine.CanUnlockSkill。
    ///
    /// 这是 RPG 表现层（Presentation/Samsara/SkillTreePanel）的唯一数据源：
    /// 界面不自己算前置，只读 <c>state</c>（unlocked / available / locked）。
    /// 因此这里必须钉死「什么状态算可解锁」，避免界面把锁定节点画成可点。
    ///
    /// 注意：新档默认已解锁 stealth_t1（见 SamsaraState.InitDefaults 的 starter），
    /// 故隐形断言把它的第 2 层视为 available。
    /// </summary>
    public sealed class SamsaraSkillViewTests
    {
        // ── 结构 / 覆盖 ────────────────────────────────────────────────

        [Test]
        public void View_HasAllFourBranches_AndThreeTiersEach()
        {
            using var f = new SamsaraFixture();
            var view = f.Engine.Skills.GetSkillTreeView();

            foreach (var branch in new[] { "karma_capacity", "stealth", "cheat_mastery", "realm_insight" })
            {
                Assert.IsNotNull(view[branch], $"缺少分支 {branch}");
                var tiers = (JObject)view[branch]["tiers"];
                Assert.AreEqual(3, tiers.Count, $"{branch} 应有 3 层");
            }
        }

        [Test]
        public void View_PreservesIdsAndCosts_ParityWithGetSkillTree()
        {
            using var f = new SamsaraFixture();
            var view = f.Engine.Skills.GetSkillTreeView();
            var raw = f.Engine.Skills.GetSkillTree();

            foreach (var branchProp in view.Properties())
            {
                var vTiers = (JObject)branchProp.Value["tiers"];
                var rTiers = (JObject)raw[branchProp.Name]["tiers"];
                foreach (var tierProp in vTiers.Properties())
                {
                    var vIds = TierIds((JObject)tierProp.Value);
                    var rIds = TierIds((JObject)rTiers[tierProp.Name]);
                    CollectionAssert.AreEqual(rIds, vIds, $"{branchProp.Name}.tier{tierProp.Name} id 顺序");
                }
            }
        }

        // ── 新档状态矩阵 ──────────────────────────────────────────────

        [Test]
        public void FreshTree_Tier1Available_DeepTiersLocked()
        {
            using var f = new SamsaraFixture();
            var view = f.Engine.Skills.GetSkillTreeView();

            // 除默认已解锁的 stealth_t1 外，其余第 1 层都应 available。
            AssertState(view, "karma_capacity", "1", "karma_capacity_t1", "available");
            AssertState(view, "stealth", "1", "stealth_t1", "unlocked");
            AssertState(view, "cheat_mastery", "1", "cheat_mastery_t1a", "available");
            AssertState(view, "cheat_mastery", "1", "cheat_mastery_t1b", "available");
            AssertState(view, "realm_insight", "1", "realm_insight_t1a", "available");
            AssertState(view, "realm_insight", "1", "realm_insight_t1b", "available");

            // stealth 第 1 层已解锁 → 第 2 层 available，第 3 层仍 locked。
            AssertState(view, "stealth", "2", "stealth_t2a", "available");
            AssertState(view, "stealth", "2", "stealth_t2b", "available");
            AssertState(view, "stealth", "3", "stealth_t3a", "locked");

            // 未解锁第 1 层的分支：第 2/3 层全部 locked。
            AssertState(view, "karma_capacity", "2", "karma_capacity_t2a", "locked");
            AssertState(view, "karma_capacity", "3", "karma_capacity_t3a", "locked");
        }

        [Test]
        public void UnlockTier1_MakesTier2Available_Tier3StillLocked()
        {
            using var f = new SamsaraFixture();
            f.Set("skill_points", 5);

            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t1", 1));
            var view = f.Engine.Skills.GetSkillTreeView();

            AssertState(view, "karma_capacity", "1", "karma_capacity_t1", "unlocked");
            AssertState(view, "karma_capacity", "2", "karma_capacity_t2a", "available");
            AssertState(view, "karma_capacity", "2", "karma_capacity_t2b", "available");
            AssertState(view, "karma_capacity", "3", "karma_capacity_t3a", "locked");
        }

        [Test]
        public void UnlockTier2Option_MakesTier3Available()
        {
            using var f = new SamsaraFixture();
            f.Set("skill_points", 5);

            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t1", 1));
            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t2a", 2));

            var view = f.Engine.Skills.GetSkillTreeView();
            AssertState(view, "karma_capacity", "3", "karma_capacity_t3a", "available");
            AssertState(view, "karma_capacity", "3", "karma_capacity_t3b", "available");
        }

        [Test]
        public void Prereq_IsPerBranch_NotGlobal()
        {
            using var f = new SamsaraFixture();
            f.Set("skill_points", 5);
            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t1", 1));

            var view = f.Engine.Skills.GetSkillTreeView();
            // 解锁 A 分支第 1 层，不应让 B 分支第 2 层变 available。
            AssertState(view, "cheat_mastery", "2", "cheat_mastery_t2a", "locked");
            AssertState(view, "realm_insight", "2", "realm_insight_t2a", "locked");
        }

        // ── 纯判定 API ────────────────────────────────────────────────

        [Test]
        public void MeetsPrerequisites_Tier1_AlwaysTrue()
        {
            using var f = new SamsaraFixture();
            foreach (var id in new[]
            {
                "karma_capacity_t1", "stealth_t1",
                "cheat_mastery_t1a", "cheat_mastery_t1b",
                "realm_insight_t1a", "realm_insight_t1b"
            })
            {
                Assert.IsTrue(f.Engine.Skills.MeetsPrerequisites(id, 1), id + " 第 1 层应恒可解锁");
            }
        }

        [Test]
        public void MeetsPrerequisites_UnknownSkill_ReturnsFalse_ForDeepTier()
        {
            using var f = new SamsaraFixture();
            Assert.IsFalse(f.Engine.Skills.MeetsPrerequisites("not_a_skill", 2));
        }

        [Test]
        public void CanUnlockSkill_MatchesActualUnlock_PositivelyAndNegatively()
        {
            using var f = new SamsaraFixture();
            f.Set("skill_points", 10);

            // 反例：前置不满足 → CanUnlock=false 且实际 UnlockSkill 失败。
            Assert.IsFalse(f.Engine.CanUnlockSkill("karma_capacity_t2a", 2), "前置不满足时 CanUnlock 应为 false");
            Assert.IsFalse(f.Engine.Skills.UnlockSkill("karma_capacity_t2a", 2), "前置不满足时实际解锁应失败");

            // 正例：先解 T1，再解 T2 → CanUnlock 与实际结果一致。
            Assert.IsTrue(f.Engine.CanUnlockSkill("karma_capacity_t1", 1));
            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t1", 1));
            Assert.IsTrue(f.Engine.CanUnlockSkill("karma_capacity_t2a", 2));
            Assert.IsTrue(f.Engine.Skills.UnlockSkill("karma_capacity_t2a", 2));
        }

        [Test]
        public void ViewState_ForEveryNode_IsOneOfThreeTokens()
        {
            using var f = new SamsaraFixture();
            var view = f.Engine.Skills.GetSkillTreeView();
            var allowed = new HashSet<string> { "unlocked", "available", "locked" };

            int count = 0;
            foreach (var branchProp in view.Properties())
            {
                var tiers = (JObject)branchProp.Value["tiers"];
                foreach (var tierProp in tiers.Properties())
                {
                    var t = (JObject)tierProp.Value;
                    var options = t["options"] as JArray;
                    if (options != null)
                    {
                        foreach (var o in options) { Assert.IsTrue(allowed.Contains((string)o["state"]), (string)o["id"]); count++; }
                    }
                    else
                    {
                        Assert.IsTrue(allowed.Contains((string)t["state"]), (string)t["id"]); count++;
                    }
                }
            }
            Assert.Greater(count, 0, "技能树不应为空");
        }

        // ── 辅助 ──────────────────────────────────────────────────────

        static void AssertState(JObject view, string branch, string tier, string id, string expected)
        {
            var node = FindNode(view, branch, tier, id);
            Assert.IsNotNull(node, $"找不到节点 {branch}.tier{tier}.{id}");
            Assert.AreEqual(expected, (string)node["state"], $"{id} 的状态");
            Assert.AreEqual(branch, (string)node["branch"], $"{id}.branch");
            Assert.AreEqual(int.Parse(tier), (int)node["tier"], $"{id}.tier");
        }

        static JObject FindNode(JObject view, string branch, string tier, string id)
        {
            var t = (JObject)view[branch]["tiers"][tier];
            if (t == null) return null;
            var options = t["options"] as JArray;
            if (options != null)
            {
                foreach (var o in options)
                    if ((string)o["id"] == id) return (JObject)o;
                return null;
            }
            return (string)t["id"] == id ? t : null;
        }

        static List<string> TierIds(JObject tier)
        {
            var list = new List<string>();
            var options = tier["options"] as JArray;
            if (options != null)
                foreach (var o in options) list.Add((string)o["id"]);
            else
                list.Add((string)tier["id"]);
            return list;
        }
    }
}
