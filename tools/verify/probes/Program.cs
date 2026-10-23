using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ChessSage.Harness
{
    /// <summary>
    /// 探针运行器。
    ///
    /// 【注册制（E3-S4）】探针必须**显式注册**进 <see cref="Registered"/> 才会被执行——
    /// 顺序即输出顺序（棋类黄金 → agent 六探针）。反射仍会扫描程序集，但只用于
    /// **完整性校验**：发现任何名为 <c>*Probe</c> 却未注册的类型 ⇒ 立即判定门禁失败
    /// （防止「改了类名于是静默不跑」这类最隐蔽的门禁失效）。
    ///
    /// 用法：
    ///   mono harness.exe                 # 跑全部已注册探针
    ///   mono harness.exe AgentSmoke      # 只跑名字包含该串的探针
    /// 退出码：0 = 全通过；1 = 有探针失败或存在未注册探针。
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// 已注册探针（顺序即执行顺序）。新增探针必须在此加一行——未注册的 *Probe 会让门禁变红。
        /// </summary>
        static readonly Type[] Registered =
        {
            // ── 棋类规则黄金（L1 既有 7 个）──
            typeof(DongwuqiProbe),
            typeof(HeibaiqiProbe),
            typeof(SessionProbe),
            typeof(TiaoqiProbe),
            typeof(WeiqiProbe),
            typeof(WuziqiProbe),
            typeof(XiangqiProbe),
            // ── E3-S4：Agent 层 6 个（QA §4.4.1-P1…P6）──
            typeof(AgentSmokeProbe),
            typeof(KarmaAnchorProbe),
            typeof(TrajectoryReplayProbe),
            typeof(LegacyFreeProbe),
            typeof(PrefixStabilityProbe),
            typeof(LoopBudgetProbe),
        };

        public static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;

            var unregistered = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsClass && t.Name.EndsWith("Probe", StringComparison.Ordinal))
                .Where(t => Array.IndexOf(Registered, t) < 0)
                .OrderBy(t => t.Name)
                .ToList();

            foreach (var type in unregistered)
                Console.WriteLine($"  UNREGISTERED  {type.Name}：形如探针但未在 Program.Registered 注册（门禁不会执行它）");

            int passed = 0, failed = unregistered.Count;
            foreach (var type in Registered)
            {
                if (filter != null && type.Name.IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    Console.WriteLine($"  FAIL  {type.Name}: 缺少 public static Run()");
                    failed++;
                    continue;
                }

                try
                {
                    method.Invoke(null, null);
                    Console.WriteLine($"  PASS  {type.Name}");
                    passed++;
                }
                catch (TargetInvocationException tie)
                {
                    Console.WriteLine($"  FAIL  {type.Name}: {tie.InnerException?.Message}");
                    failed++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  FAIL  {type.Name}: {ex.Message}");
                    failed++;
                }
            }

            Console.WriteLine($"探针汇总：{passed} 通过 / {failed} 失败（已注册 {Registered.Length} 个）");
            return failed == 0 ? 0 : 1;
        }
    }
}
