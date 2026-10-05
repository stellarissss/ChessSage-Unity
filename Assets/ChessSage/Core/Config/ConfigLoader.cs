using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Config
{
    /// <summary>
    /// 配置加载器。从 legacy-web 同步而来的 StreamingAssets/Configs 目录读取 JSON 真源。
    /// 目录约定（由 tools/sync-configs.sh 生成）：
    ///   Configs/global/*.json            —— 关卡/RPG 全局配置
    ///   Configs/{variant}/board.json 等  —— 各棋类配置
    ///   Configs/Schemas/*.schema.json    —— JSON Schema（配置校验）
    /// </summary>
    public sealed class ConfigLoader
    {
        public string RootPath { get; }

        public ConfigLoader(string rootPath)
        {
            RootPath = rootPath;
        }

        public string VariantDir(string variant) => Path.Combine(RootPath, variant);
        public string GlobalDir => Path.Combine(RootPath, "global");
        public string SchemaDir => Path.Combine(RootPath, "Schemas");

        JObject LoadJson(string path)
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
        }

        JObject LoadVariantJson(string variant, string fileName)
            => LoadJson(Path.Combine(VariantDir(variant), fileName + ".json"));

        public GameConfig LoadVariant(string variantId)
        {
            var info = VariantCatalog.Get(variantId);
            bool bw = info.SideScheme == "black_white";

            // legacy 各棋类的棋子配置文件命名并不统一：黑白棋用 pieces_white，
            // 五子棋/围棋却把白方放在 pieces_red（文件内 side 字段才是真源）。
            var config = new GameConfig
            {
                Variant = info.Id,
                SideScheme = info.SideScheme,
                Board = LoadVariantJson(info.Id, "board"),
                PiecesA = LoadVariantJson(info.Id, bw ? "pieces_black" : "pieces_red"),
                PiecesB = bw
                    ? LoadVariantJson(info.Id, "pieces_white") ?? LoadVariantJson(info.Id, "pieces_red")
                    : LoadVariantJson(info.Id, "pieces_black"),
                Rules = LoadVariantJson(info.Id, "rules"),
                UiConfig = LoadVariantJson(info.Id, "ui_config"),
                BoardState = LoadVariantJson(info.Id, "board_state"),
            };
            if (config.Board == null) throw new FileNotFoundException($"缺少 {info.Id}/board.json");
            if (config.PiecesA == null) throw new FileNotFoundException($"缺少 {info.Id} 先手方棋子配置");
            if (config.PiecesB == null) throw new FileNotFoundException($"缺少 {info.Id} 后手方棋子配置");
            if (config.Rules == null) config.Rules = new JObject();
            if (config.BoardState == null) throw new FileNotFoundException($"缺少 {info.Id}/board_state.json");
            return config;
        }

        public JObject LoadGlobal(string name) => LoadJson(Path.Combine(GlobalDir, name + ".json"));

        public JObject LoadSchema(string name) => LoadJson(Path.Combine(SchemaDir, name + ".schema.json"));

        public IReadOnlyList<string> ListAvailableVariants()
        {
            var result = new List<string>();
            foreach (var info in VariantCatalog.All)
                if (File.Exists(Path.Combine(VariantDir(info.Id), "board.json"))) result.Add(info.Id);
            return result;
        }
    }
}