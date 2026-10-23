#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using ChessSage.Tools.E3;
using UnityEditor;
using UnityEngine;

namespace ChessSage.EditorTools
{
    /// <summary>
    /// E3 真实链路校准的 Editor 入口（REMAKE-IMPL-E3B）。
    ///
    /// batchmode：
    ///   Unity -batchmode -nographics -projectPath &lt;repo&gt; -executeMethod
    ///     ChessSage.EditorTools.E3AgentRunnerMenu.BatchRun
    ///   参数经环境变量注入（避免命令行明文）：
    ///     CHESSSAGE_E3_STAGE / TAG / BASE / BASE_URL / EFFORT / RUNS / SEED /
    ///     MAX_ROUNDS / TOOL_MAX_TOKENS / FINAL_MAX_TOKENS / ROOT / CONFIG / OUT / CALL
    ///
    /// 交互：ChessSage/E3 校准/… 菜单（结果写 Logs/e3/raw，不弹窗）。
    ///
    /// 单会话批量：一次编辑器启动内循环全部提交，避免 N 次冷启动；与
    /// <c>tools/verify/e3</c> 控制台宿主共用同一份 <see cref="E3AgentRunner"/> 实现。
    /// </summary>
    public static class E3AgentRunnerMenu
    {
        [MenuItem("ChessSage/E3 校准/冒烟（当前 base_url × 3）", priority = 200)]
        public static void MenuSmoke() => RunFromEnv("smoke", "smoke", exit: false);

        [MenuItem("ChessSage/E3 校准/性能基线（D/A/C × 3）", priority = 201)]
        public static void MenuPerf() => RunFromEnv("perf", "perf", exit: false);

        [MenuItem("ChessSage/E3 校准/Effort 矩阵（M2 × 5）", priority = 202)]
        public static void MenuEffort() => RunFromEnv("effort", "effort-m2", exit: false);

        [MenuItem("ChessSage/E3 校准/六档校准（6 × 20）", priority = 203)]
        public static void MenuCalib() => RunFromEnv("calib", "calib", exit: false);

        [MenuItem("ChessSage/E3 校准/三改齐发（× 5）", priority = 204)]
        public static void MenuMulti() => RunFromEnv("multi", "multi", exit: false);

        [MenuItem("ChessSage/E3 校准/体积测量（不调 API）", priority = 205)]
        public static void MenuInspect() => RunFromEnv("inspect", "inspect", exit: false);

        /// <summary>batchmode 入口（无参；参数全部经环境变量）。</summary>
        public static void BatchRun()
        {
            var stage = Env("CHESSSAGE_E3_STAGE", "smoke");
            var tag = Env("CHESSSAGE_E3_TAG", stage);
            var code = RunFromEnv(stage, tag, exit: true);
            if (code != 0)
                Debug.LogError($"[e3] 退出码 {code}");
        }

        static int RunFromEnv(string stage, string defaultTag, bool exit)
        {
            var args = new List<string>
            {
                "--stage", stage,
                "--tag", Env("CHESSSAGE_E3_TAG", defaultTag),
            };
            AddArg(args, "--base", Env("CHESSSAGE_E3_BASE"));
            AddArg(args, "--base-url", Env("CHESSSAGE_E3_BASE_URL"));
            AddArg(args, "--effort", Env("CHESSSAGE_E3_EFFORT"));
            AddArg(args, "--tiers", Env("CHESSSAGE_E3_TIERS"));
            AddArg(args, "--runs", Env("CHESSSAGE_E3_RUNS"));
            AddArg(args, "--seed", Env("CHESSSAGE_E3_SEED"));
            AddArg(args, "--max-rounds", Env("CHESSSAGE_E3_MAX_ROUNDS"));
            AddArg(args, "--tool-max-tokens", Env("CHESSSAGE_E3_TOOL_MAX_TOKENS"));
            AddArg(args, "--final-max-tokens", Env("CHESSSAGE_E3_FINAL_MAX_TOKENS"));
            AddArg(args, "--root", Env("CHESSSAGE_E3_ROOT"));
            AddArg(args, "--config", Env("CHESSSAGE_E3_CONFIG"));
            AddArg(args, "--out", Env("CHESSSAGE_E3_OUT"));
            if (Env("CHESSSAGE_E3_CALL") == "1") args.Add("--call");

            int code;
            try
            {
                code = E3AgentRunner.Run(args.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError("[e3] 未捕获异常: " + ex);
                code = 1;
            }

            Debug.Log($"[e3] stage={stage} 结束，退出码 {code}");
            if (exit) EditorApplication.Exit(code);
            return code;
        }

        static void AddArg(List<string> args, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            args.Add(name);
            args.Add(value);
        }

        static string Env(string name, string fallback = null)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }
    }
}
#endif
