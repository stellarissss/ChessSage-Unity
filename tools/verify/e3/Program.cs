using System;
using System.Collections.Generic;

namespace ChessSage.Tools.E3.Host
{
    /// <summary>
    /// 引擎外宿主：与 Unity Editor 入口共用同一份 <see cref="E3AgentRunner"/>。
    ///
    /// 两种调用形态：
    ///   ① 显式命令行参数（<c>--stage calib --tiers ...</c>）；
    ///   ② 无参 + 环境变量（与 Editor batchmode 完全同一套键，便于 CI/校准脚本复用）：
    ///      CHESSSAGE_E3_STAGE / TAG / BASE / BASE_URL / EFFORT / TIERS / RUNS / SEED /
    ///      MAX_ROUNDS / TOOL_MAX_TOKENS / FINAL_MAX_TOKENS / ROOT / CONFIG / OUT / CALL。
    /// 环境变量中绝不包含密钥——密钥只由 Core 的 AiConfig 从 DEEPSEEK_API_KEY / config.json 读取。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
                args = ArgsFromEnv();
            return E3AgentRunner.Run(args);
        }

        static string[] ArgsFromEnv()
        {
            var stage = Env("CHESSSAGE_E3_STAGE", "smoke");
            var args = new List<string>
            {
                "--stage", stage,
                "--tag", Env("CHESSSAGE_E3_TAG", stage),
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
            return args.ToArray();
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
