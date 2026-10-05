using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ChessSage.Harness
{
    /// <summary>
    /// 探针运行器：反射发现所有名为 *Probe 的类型上的 public static Run()，
    /// 逐个执行并汇总结果。探针断言失败请抛出异常。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            var probeTypes = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsClass && t.Name.EndsWith("Probe"))
                .OrderBy(t => t.Name)
                .ToList();

            int passed = 0, failed = 0;
            foreach (var type in probeTypes)
            {
                if (filter != null && !type.Name.Contains(filter)) continue;
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null) continue;

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

            Console.WriteLine($"探针汇总：{passed} 通过 / {failed} 失败");
            return failed == 0 ? 0 : 1;
        }
    }
}