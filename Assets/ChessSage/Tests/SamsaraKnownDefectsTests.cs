using System.Collections.Generic;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// ✅ **已修复缺陷的回归护栏**（原 M5-B「已知缺陷」套件，缺陷 D-01）——已并入常规回归。
    ///
    /// 缺陷根因（D-01，P0）
    /// ================
    /// legacy Python 用 `x in list`（**值**比较）判断成员，C# 移植时对 `JArray` 直接使用了
    /// `array.Contains(value)`。Newtonsoft.Json 13.0.3 的 `JArray.Contains` 命中的是
    /// `ICollection<JToken>.Contains`，走 **JToken 引用相等**，故对每次新建的标量恒为 false：
    ///     new JArray { "hell" }.Contains("hell")                    → false
    ///     JToken.DeepEquals(new JValue("hell"), new JValue("hell")) → true
    /// 即值成员判定永远为假。受影响调用点（均已改用值比较 `JArrayExtensions.ContainsValue`）：
    ///   • SamsaraState.IsSandboxUnlocked / UnlockSandbox    (SamsaraState.cs:836/847)
    ///   • SamsaraState.IsBossDefeated / MarkBossDefeated    (SamsaraState.cs:729/739)
    ///   • ObjectiveSystem.CheckEvacuation                   (Objectives.cs:189)
    ///
    /// 修复前的可观测症状
    /// ================
    ///   1) 前端「沙盒已解锁」永远为 false（GetRealmProgress / GetFrontendState 走 IsSandboxUnlocked）；
    ///   2) 「首次击败 Boss」奖励每次结算都重复发放，且 bosses_defeated 反复写入重复项；
    ///   3) 撤离类目标 evacuated 恒为 0，永远无法完成。
    ///
    /// 防复发
    /// ====
    /// 本套件以 Python 原版（权威源）的期望行为锁定上述修复。**任何人若把 `ContainsValue`
    /// 回退成 `JArray.Contains`，这 6 条用例会立即变红**——请勿弱化断言或重新加回 [Explicit]。
    /// </summary>
    public sealed class SamsaraKnownDefectsTests
    {
        [Test]
        public void IsSandboxUnlocked_ShouldBeTrue_AfterUnlock()
        {
            using var f = new SamsaraFixture();
            f.Set("current_realm", "hell");
            f.Engine.UnlockSandbox("hell");

            // Python: realm in sandbox_unlocked → True
            Assert.IsTrue(f.Engine.IsSandboxUnlocked("hell"),
                "UnlockSandbox 后 IsSandboxUnlocked 应为 true；实际 sandbox_unlocked="
                + f.Engine.State.Get("sandbox_unlocked"));
        }

        [Test]
        public void RealmProgress_SandboxFlag_ShouldReflectUnlock()
        {
            using var f = new SamsaraFixture();
            f.Set("current_realm", "hell");
            f.Engine.UnlockSandbox("hell");

            var progress = f.Engine.Levels.GetRealmProgress("hell");
            Assert.IsTrue(progress["sandbox_unlocked"].Value<bool>(),
                "Python get_realm_progress.sandbox_unlocked 应为 true");
        }

        [Test]
        public void IsBossDefeated_ShouldBeTrue_AfterMark()
        {
            using var f = new SamsaraFixture();
            f.Engine.MarkBossDefeated("boss_hell");
            Assert.IsTrue(f.Engine.IsBossDefeated("boss_hell"),
                "MarkBossDefeated 后 IsBossDefeated 应为 true");

            var defeated = (JArray)f.Engine.State.Get("bosses_defeated");
            Assert.AreEqual(1, defeated.Count, "bosses_defeated 不应出现重复项");
        }

        [Test]
        public void MarkBossDefeated_ShouldBeIdempotent()
        {
            using var f = new SamsaraFixture();
            f.Engine.MarkBossDefeated("boss_hell");
            f.Engine.MarkBossDefeated("boss_hell");
            var defeated = (JArray)f.Engine.State.Get("bosses_defeated");
            Assert.AreEqual(1, defeated.Count, "重复标记同一 Boss 应幂等（Python list append 前有成员判断无效项）");
        }

        [Test]
        public void FirstBossDefeatBonus_ShouldBeGrantedOnlyOnce()
        {
            using var f = new SamsaraFixture();
            f.Set("current_realm", "hell");
            f.Set("level_karma", 50);

            var r1 = f.Engine.Progression.ResolveLevel(true, true, true);
            var r2 = f.Engine.Progression.ResolveLevel(true, true, true);

            Assert.IsTrue(ReasonsContain(r1, "首次击败Boss"), "首次结算应发放首次击败奖励");
            Assert.IsFalse(ReasonsContain(r2, "首次击败Boss"), "第二次结算不应再发放首次击败奖励");
        }

        [Test]
        public void Evacuation_ShouldCountPiecesInsideTargetRegion()
        {
            using var f = new SamsaraFixture();
            var objective = JObject.Parse(
                "{\"type\":\"evacuation\",\"pieces\":[\"piece1\"]," +
                "\"target_region\":{\"top_left\":[0,0],\"bottom_right\":[6,6]}}");
            var gameState = JObject.Parse(
                "{\"pieces\":[{\"type\":\"piece1\",\"is_alive\":true,\"position\":[1,1]}]}");

            var res = f.Engine.Objectives.Check(objective, gameState);
            // Python: 目标棋子位于目标区域内 → evacuated=1/1 → completed True, progress 100
            Assert.IsTrue(res["completed"].Value<bool>(), "1/1 棋子已撤离应判定完成");
            Assert.AreEqual(100.0, res["progress"].Value<double>(), 1e-9, "撤离进度应为 100");
        }

        static bool ReasonsContain(JObject result, string reason)
        {
            var reasons = result["bonus_reasons"] as JArray;
            if (reasons == null) return false;
            foreach (var r in reasons) if (r.Value<string>() == reason) return true;
            return false;
        }
    }
}
