using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>
    /// 配置 Schema 校验器。对齐 legacy-web/shared/schema_validator.py：
    /// 按配置名映射到 StreamingAssets/Schemas/*.schema.json，交由
    /// <see cref="ChessSage.Core.Json.JsonSchemaLite"/> 做 Draft-07 子集校验；
    /// board_state 额外做棋子坐标的棋盘边界检查（与 Python validate_board_state 一致）。
    ///
    /// Schema 目录解析顺序：<see cref="SchemaDir"/> 显式指定 &gt; 从应用基目录/当前目录
    /// 向上搜索 StreamingAssets/Schemas &gt; 向上搜索 Schemas。
    /// </summary>
    public static class SchemaValidator
    {
        /// <summary>显式指定的 Schema 目录；为空时按约定自动搜索。</summary>
        public static string SchemaDir;

        static readonly Dictionary<string, string> ConfigSchemaMap = new Dictionary<string, string>
        {
            ["board_state"] = "board_state.schema.json",
            ["board"] = "board.schema.json",
            ["pieces"] = "pieces.schema.json",
            ["pieces_red"] = "pieces.schema.json",
            ["pieces_black"] = "pieces.schema.json",
            ["pieces_white"] = "pieces.schema.json",
            ["rules"] = "rules.schema.json",
            ["ui_config"] = "ui_config.schema.json",
        };

        static readonly Dictionary<string, JObject> SchemaCache = new Dictionary<string, JObject>();

        static string ResolveSchemaDir()
        {
            if (!string.IsNullOrEmpty(SchemaDir) && Directory.Exists(SchemaDir)) return SchemaDir;

            var roots = new List<string>();
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir)) roots.Add(baseDir);
            try { roots.Add(Directory.GetCurrentDirectory()); } catch { /* 忽略 */ }

            var relativeCandidates = new[]
            {
                Path.Combine("StreamingAssets", "Schemas"),
                "Schemas",
            };

            foreach (var root in roots)
            {
                DirectoryInfo dir;
                try { dir = new DirectoryInfo(root); }
                catch { continue; }
                while (dir != null)
                {
                    foreach (var rel in relativeCandidates)
                    {
                        var candidate = Path.Combine(dir.FullName, rel);
                        if (Directory.Exists(candidate)) return candidate;
                    }
                    dir = dir.Parent;
                }
            }
            return null;
        }

        static JObject LoadSchema(string schemaName)
        {
            if (SchemaCache.TryGetValue(schemaName, out var cached)) return cached;

            var dir = ResolveSchemaDir();
            if (string.IsNullOrEmpty(dir))
                throw new FileNotFoundException($"未找到 Schema 目录（{schemaName}）");

            var path = Path.Combine(dir, schemaName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"未找到 Schema 文件: {path}");

            var schema = JObject.Parse(File.ReadAllText(path));
            SchemaCache[schemaName] = schema;
            return schema;
        }

        /// <summary>清空 Schema 缓存，强制下次重新读盘。</summary>
        public static void ClearCache()
        {
            SchemaCache.Clear();
        }

        /// <summary>按配置名校验，返回 (是否通过, 错误信息)；错误信息形如 "[路径] 描述"。</summary>
        public static (bool ok, string error) ValidateConfig(string configName, JObject configData)
        {
            if (!ConfigSchemaMap.TryGetValue(configName, out var schemaFile))
                return (false, $"未知的配置名称: {configName}");

            JObject schema;
            try
            {
                schema = LoadSchema(schemaFile);
            }
            catch (Exception e)
            {
                return (false, $"加载Schema文件失败: {e.Message}");
            }

            var errors = ChessSage.Core.Json.JsonSchemaLite.Validate(schema, configData);
            if (errors.Count == 0) return (true, "");
            return (false, string.Join("; ", errors));
        }

        /// <summary>校验棋盘状态：先过 Schema，再逐子检查坐标是否越界。</summary>
        public static (bool ok, string error) ValidateBoardState(JObject boardState)
        {
            var (valid, err) = ValidateConfig("board_state", boardState);
            if (!valid) return (false, err);

            var errors = new List<string>();
            var board = boardState?["board"] as JObject;
            int width = JsonHelpers.GetInt(board, "width", 9);
            int height = JsonHelpers.GetInt(board, "height", 10);
            var pieces = boardState?["pieces"] as JArray;
            if (pieces != null)
            {
                for (int i = 0; i < pieces.Count; i++)
                {
                    if (!(pieces[i] is JObject piece)) continue;
                    var position = piece["position"] as JArray;
                    if (position == null || position.Count != 2) continue;
                    int x = TokenToInt(position[0]);
                    int y = TokenToInt(position[1]);
                    if (x < 0 || x >= width)
                        errors.Add($"[pieces.{i}.position.0] {x} 超出棋盘范围 (0-{width - 1})");
                    if (y < 0 || y >= height)
                        errors.Add($"[pieces.{i}.position.1] {y} 超出棋盘范围 (0-{height - 1})");
                }
            }

            if (errors.Count > 0) return (false, string.Join("; ", errors));
            return (true, "");
        }

        static int TokenToInt(JToken token)
        {
            if (token == null) return 0;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            if (token.Type == JTokenType.Float) return (int)token.Value<double>();
            return 0;
        }

        /// <summary>校验棋子配置。</summary>
        public static (bool ok, string error) ValidatePieces(JObject pieces)
            => ValidateConfig("pieces", pieces);

        /// <summary>校验游戏规则配置。</summary>
        public static (bool ok, string error) ValidateRules(JObject rules)
            => ValidateConfig("rules", rules);

        /// <summary>校验棋盘外观配置。</summary>
        public static (bool ok, string error) ValidateBoard(JObject board)
            => ValidateConfig("board", board);

        /// <summary>校验界面配置。</summary>
        public static (bool ok, string error) ValidateUiConfig(JObject uiConfig)
            => ValidateConfig("ui_config", uiConfig);

        /// <summary>对 AI 生成的配置执行硬编码 Schema 校验（对齐 _hard_validate_config）。</summary>
        public static (bool ok, string error) HardValidateConfig(string configName, JObject configData)
        {
            switch (configName)
            {
                case "board_state": return ValidateBoardState(configData);
                case "pieces": return ValidatePieces(configData);
                case "rules": return ValidateRules(configData);
                case "ui_config": return ValidateUiConfig(configData);
                case "board": return ValidateBoard(configData);
                default: return (true, ""); // 未知类型跳过
            }
        }
    }
}