using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>
    /// AI 接入配置唯一真源。对齐 legacy-web/shared/ai_config.py。
    ///
    /// 优先级（从高到低）：
    ///   API 密钥：环境变量 DEEPSEEK_API_KEY &gt; config.json["api_key"] &gt; DEFAULT_API_KEY
    ///   模型名  ：环境变量 DEEPSEEK_MODEL   &gt; config.json["model"]    &gt; DEFAULT_MODEL
    ///   Base URL：环境变量 DEEPSEEK_BASE_URL &gt; config.json["base_url"] &gt; DEFAULT_BASE_URL
    ///
    /// 密钥防泄露：静态存放的是「混淆串」（每 8 位十六进制后插入 1 位非十六进制迷惑字符），
    /// 运行时经 <see cref="UnmaskSecret"/>（剥离全部非十六进制字符并补回 sk- 前缀）还原为真实密钥。
    /// 该还原幂等，因此用真实密钥直接覆盖配置也能正常工作。
    /// </summary>
    public static class AiConfig
    {
        /// <summary>密钥前缀：剥离时先去掉，还原时补回。</summary>
        public const string SecretPrefix = "sk-";

        /// <summary>默认 Base URL（DeepSeek 兼容 OpenAI 的 chat/completions 接口）。</summary>
        public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

        /// <summary>默认模型：DeepSeek 网关的实际模型 ID。</summary>
        public const string DefaultModelName = "deepseek-flash";

        /// <summary>默认 API 密钥（**混淆形式**，含迷惑字符，不可直接调用）。</summary>
        public const string DefaultApiKeyMasked = "sk-b317699fz4b6c48a9wb3a093a5k5911c8a0";

        /// <summary>还原用的剥离正则：只保留十六进制字符（0-9 a-f A-F）。</summary>
        static readonly Regex UnmaskRe = new Regex("[^0-9a-fA-F]", RegexOptions.Compiled);

        static JObject _configCache;
        static bool _configLoaded;
        static string _configPath;

        /// <summary>配置文件向上遍历的起始目录；为空时从应用基目录/当前工作目录开始。</summary>
        public static string SearchRoot;

        /// <summary>把静态存放的「混淆密钥」还原为可直接调用的真实密钥。</summary>
        public static string UnmaskSecret(string secret)
        {
            var raw = (secret ?? string.Empty).Trim();
            if (raw.Length == 0) return string.Empty;

            if (raw.Length >= 3 && raw.Substring(0, 3).ToLowerInvariant() == SecretPrefix)
                raw = raw.Substring(3);

            var payload = UnmaskRe.Replace(raw, string.Empty);
            return SecretPrefix + payload;
        }

        /// <summary><see cref="UnmaskSecret"/> 的语义别名，供统一入口调用方使用。</summary>
        public static string NormalizeSecret(string secret) => UnmaskSecret(secret);

        /// <summary>自起始目录逐级向上，返回第一个存在的 config.json 路径；找不到返回 null。</summary>
        public static string FindConfigPath()
        {
            var start = SearchRoot;
            if (string.IsNullOrEmpty(start))
                start = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(start))
                start = Directory.GetCurrentDirectory();

            DirectoryInfo dir;
            try { dir = new DirectoryInfo(start); }
            catch { return null; }

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "config.json");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>读取并缓存 config.json；读取失败静默回落空对象。</summary>
        public static JObject LoadConfig()
        {
            if (_configLoaded) return _configCache;

            _configPath = FindConfigPath();
            if (string.IsNullOrEmpty(_configPath))
            {
                _configCache = new JObject();
                _configLoaded = true;
                return _configCache;
            }

            try
            {
                var text = File.ReadAllText(_configPath);
                var parsed = JToken.Parse(text);
                _configCache = parsed as JObject ?? new JObject();
            }
            catch
            {
                _configCache = new JObject();
            }
            _configLoaded = true;
            return _configCache;
        }

        /// <summary>读取 API 密钥（始终返回已还原的真实密钥）。</summary>
        public static string GetApiKey()
        {
            var envKey = (Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? string.Empty).Trim();
            if (envKey.Length > 0) return UnmaskSecret(envKey);

            var config = LoadConfig();
            var cfgKey = config["api_key"]?.Value<string>();
            if (!string.IsNullOrEmpty(cfgKey)) return UnmaskSecret(cfgKey.Trim());

            return UnmaskSecret(DefaultApiKeyMasked);
        }

        /// <summary>读取模型名。</summary>
        public static string GetModel()
        {
            var envModel = (Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? string.Empty).Trim();
            if (envModel.Length > 0) return envModel;

            var config = LoadConfig();
            var cfgModel = config["model"]?.Value<string>();
            if (!string.IsNullOrEmpty(cfgModel)) return cfgModel.Trim();

            return DefaultModelName;
        }

        /// <summary>读取 API Base URL。</summary>
        public static string GetBaseUrl()
        {
            var envUrl = (Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? string.Empty).Trim();
            if (envUrl.Length > 0) return envUrl;

            var config = LoadConfig();
            var cfgUrl = config["base_url"]?.Value<string>();
            if (!string.IsNullOrEmpty(cfgUrl)) return cfgUrl.Trim();

            return DefaultBaseUrl;
        }

        /// <summary>清空配置缓存，强制下次调用重新读盘（供运行中热更新密钥/模型使用）。</summary>
        public static void ReloadConfig()
        {
            _configCache = null;
            _configLoaded = false;
            _configPath = null;
        }

        /// <summary>
        /// 关闭推理的请求参数片段：业力评估、棋子估值等简单数值映射任务实测可显著降时延，
        /// 并避免 reasoning 吃光 max_tokens 导致 content 为空。
        /// </summary>
        public static JObject GetNoThinkParams()
        {
            return new JObject { ["thinking"] = new JObject { ["type"] = "disabled" } };
        }
    }
}