using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// agent_remake.json 加载器（E0-S5）。校准重拟合只改 json、不动代码——本类是唯一入口。
    ///
    /// 候选链（按序，第一个「存在且可解析」的文件胜出）：
    ///   1. <paramref name="explicitPath"/> 调用方显式指定（Unity 侧传
    ///      Application.streamingAssetsPath/Agent/agent_remake.json——Core 零引擎依赖，见不到它）；
    ///   2. 环境变量 CHESSSAGE_AGENT_CONFIG（运维/调试覆盖）；
    ///   3. 环境变量 CHESSSAGE_ASSETS_ROOT 下的 StreamingAssets/Agent/ 与 Agent/；
    ///   4. 自动探测：从进程目录/当前目录向上找含 Assets/ChessSage/Core/Agent 的工程根，
    ///      取其 Assets/StreamingAssets/Agent/agent_remake.json（编辑器/CI 直跑场景）；
    ///   5. 零候选或全部失败 ⇒ <see cref="AgentRemakeConfig.Default"/>（内置合同值）。
    ///
    /// 兜底语义：显式候选（1/2/3）缺失或不可读会记告警；自动探测（4）缺失静默
    /// （探测本来就可能落空）；任何候选解析失败一律记告警。兜底不等于静默——
    /// <see cref="AgentRemakeConfig.Warnings"/> 供遥测与校准审计。
    ///
    /// 兼容 jsonc：JsonTextReader 默认跳过 // 与 /* */ 注释；禁日期隐式解析
    /// （JsonSerializerSettings.DateParseHandling.None）——ISO 日期串若被转成 JTokenType.Date，
    /// 字符串字段的字节级往返会被破坏（TokenLedger 持久化同款坑）。
    /// </summary>
    public static class AgentConfigLoader
    {
        /// <summary>配置文件名（StreamingAssets/Agent/ 下）。</summary>
        public const string ConfigFileName = "agent_remake.json";

        /// <summary>环境变量：显式配置路径（最高优先的运维覆盖入口）。</summary>
        public const string EnvVarConfigPath = "CHESSSAGE_AGENT_CONFIG";

        /// <summary>环境变量：资源根目录（候选 3 的拼装基准）。</summary>
        public const string EnvVarAssetsRoot = "CHESSSAGE_ASSETS_ROOT";

        /// <summary>向上探测的最大层数（防止异常深的目录树拖慢启动）。</summary>
        const int MaxProbeDepth = 8;

        /// <summary>
        /// 加载配置。
        /// </summary>
        /// <param name="explicitPath">调用方显式指定的配置路径（最高优先级）。</param>
        /// <param name="probeRoots">
        /// 覆盖自动探测起点：null = 自动（从进程目录/当前目录向上找工程根）；
        /// 空序列 = 完全禁用自动探测（测试/受控宿主用）；非空 = 只在给定根下查找。
        /// </param>
        public static AgentRemakeConfig Load(string explicitPath = null, IEnumerable<string> probeRoots = null)
        {
            var loaderWarnings = new List<string>();
            int attempted = 0;
            foreach (var candidate in CandidatePaths(explicitPath, probeRoots))
            {
                attempted++;
                string text;
                try
                {
                    text = File.ReadAllText(candidate.FilePath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    // 显式候选（调用方/env 指定）缺失要让调用方看见；自动探测落空是正常路径，静默。
                    if (candidate.Explicit)
                        loaderWarnings.Add($"候选配置 {candidate.FilePath} 不可读（{ex.GetType().Name}），已跳过");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    // 空/纯空白文本：DeserializeObject 会静默返回 null，但这是坏文件，必须与字面 "null" 区分告警
                    loaderWarnings.Add($"配置文件 {candidate.FilePath} 内容为空，已跳过");
                    continue;
                }

                JObject root;
                try
                {
                    // DeserializeObject<JObject> 而非 JObject.Parse：后者在本版 Newtonsoft 上没有
                    // DateParseHandling 开关，且会把 ISO 日期串解析成 JTokenType.Date，破坏字符串字段。
                    // JsonTextReader 默认跳过 // 与 /* */ 注释（jsonc），DateParseHandling.None 保住纯字符串。
                    root = JsonConvert.DeserializeObject<JObject>(text, new JsonSerializerSettings
                    {
                        DateParseHandling = DateParseHandling.None,
                    });
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException)
                {
                    loaderWarnings.Add($"配置文件 {candidate.FilePath} 解析失败（{ex.Message}），已跳过");
                    continue;
                }

                if (root == null)
                {
                    // JSON 文本 "null"：DeserializeObject<JObject> 返回 C# null 而不抛错
                    loaderWarnings.Add($"配置文件 {candidate.FilePath} 内容为 null，已跳过");
                    continue;
                }

                return AgentRemakeConfig.FromJson(root)
                    .WithSource(candidate.FilePath)
                    .WithExtraWarnings(loaderWarnings);
            }

            loaderWarnings.Add(
                $"未找到可用的 {ConfigFileName}（已尝试 {attempted} 个候选），已使用内置默认参数");
            return AgentRemakeConfig.Default.WithExtraWarnings(loaderWarnings);
        }

        /// <summary>候选路径链（惰性求值——命中即止，不探测后续路径）。</summary>
        static IEnumerable<Candidate> CandidatePaths(string explicitPath, IEnumerable<string> probeRoots)
        {
            if (!string.IsNullOrEmpty(explicitPath))
                yield return new Candidate(explicitPath, @explicit: true);

            var envPath = Environment.GetEnvironmentVariable(EnvVarConfigPath);
            if (!string.IsNullOrEmpty(envPath))
                yield return new Candidate(envPath, @explicit: true);

            var assetsRoot = Environment.GetEnvironmentVariable(EnvVarAssetsRoot);
            if (!string.IsNullOrEmpty(assetsRoot))
            {
                yield return new Candidate(
                    Path.Combine(assetsRoot, "StreamingAssets", "Agent", ConfigFileName), @explicit: true);
                yield return new Candidate(
                    Path.Combine(assetsRoot, "Agent", ConfigFileName), @explicit: true);
            }

            var roots = probeRoots ?? AutoProbeRoots();
            foreach (var root in roots)
                yield return new Candidate(
                    Path.Combine(root, "Assets", "StreamingAssets", "Agent", ConfigFileName), @explicit: false);

            if (probeRoots == null)
            {
                // 自动模式附加：以当前工作目录相对的常见布局（Unity 编辑器 cwd = 工程根）。
                yield return new Candidate(
                    Path.Combine("Assets", "StreamingAssets", "Agent", ConfigFileName), @explicit: false);
                yield return new Candidate(
                    Path.Combine("StreamingAssets", "Agent", ConfigFileName), @explicit: false);
                yield return new Candidate(ConfigFileName, @explicit: false);
            }
        }

        /// <summary>从进程目录与当前目录向上探测工程根（含 Assets/ChessSage/Core/Agent）。</summary>
        static IEnumerable<string> AutoProbeRoots()
        {
            var seen = new List<string>();
            foreach (var start in StartDirectories())
            {
                var dir = start;
                for (int depth = 0; depth < MaxProbeDepth && dir != null; depth++, dir = SafeParent(dir))
                {
                    string full;
                    try { full = dir.FullName; }
                    catch (Exception) { break; }
                    if (IsProjectRoot(full))
                    {
                        if (!seen.Contains(full))
                        {
                            seen.Add(full);
                            yield return full;
                        }
                    }
                }
            }
        }

        static IEnumerable<DirectoryInfo> StartDirectories()
        {
            var starts = new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory };
            foreach (var start in starts)
            {
                if (string.IsNullOrEmpty(start)) continue;
                DirectoryInfo dir = null;
                try { dir = new DirectoryInfo(start); }
                catch (Exception) { /* 非法起点直接跳过 */ }
                if (dir != null) yield return dir;
            }
        }

        static DirectoryInfo SafeParent(DirectoryInfo dir)
        {
            try { return dir.Parent; }
            catch (Exception) { return null; }
        }

        static bool IsProjectRoot(string dir)
        {
            try
            {
                return Directory.Exists(Path.Combine(dir, "Assets", "ChessSage", "Core", "Agent"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>候选路径 + 是否显式指定（显式候选缺失要告警，探测落空静默）。</summary>
        struct Candidate
        {
            public readonly string FilePath;
            public readonly bool Explicit;

            public Candidate(string filePath, bool @explicit)
            {
                FilePath = filePath;
                Explicit = @explicit;
            }
        }
    }
}
