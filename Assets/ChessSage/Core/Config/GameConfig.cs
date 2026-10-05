using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Config
{
    /// <summary>
    /// 一个棋类的完整配置集合（直接对应 legacy-web 中某棋类 configs/ 目录的 JSON 真源）。
    /// 提供 board_state 初始快照与规则引擎所需的四份配置。
    /// </summary>
    public sealed class GameConfig
    {
        public string Variant;
        /// <summary>阵营配色命名："red_black"（象棋/动物棋/跳棋）或 "black_white"（围棋/五子棋/黑白棋）。</summary>
        public string SideScheme;

        public JObject Board;
        public JObject PiecesA;   // red 或 black
        public JObject PiecesB;   // black 或 white
        public JObject Rules;
        public JObject UiConfig;
        public JObject BoardState;

        public bool IsRedBlack => SideScheme == "red_black";

        public GameConfig CloneShallow() => new GameConfig
        {
            Variant = Variant,
            SideScheme = SideScheme,
            Board = (JObject)Board.DeepClone(),
            PiecesA = (JObject)PiecesA.DeepClone(),
            PiecesB = (JObject)PiecesB.DeepClone(),
            Rules = (JObject)Rules.DeepClone(),
            UiConfig = UiConfig == null ? null : (JObject)UiConfig.DeepClone(),
            BoardState = (JObject)BoardState.DeepClone(),
        };
    }

    public static class VariantCatalog
    {
        public sealed class Info
        {
            public string Id;
            public string DisplayName;
            public string SideScheme;
            public Info(string id, string name, string scheme) { Id = id; DisplayName = name; SideScheme = scheme; }
        }

        public static readonly Info[] All =
        {
            new Info("xiangqi", "象棋 · 六道轮回", "red_black"),
            new Info("dongwuqi", "动物棋", "red_black"),
            new Info("tiaoqi", "中国跳棋", "red_black"),
            new Info("wuziqi", "五子棋", "black_white"),
            new Info("weiqi", "围棋", "black_white"),
            new Info("heibaiqi", "黑白棋", "black_white"),
        };

        public static Info Get(string id)
        {
            foreach (var i in All) if (i.Id == id) return i;
            return All[0];
        }
    }
}