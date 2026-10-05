using ChessSage.Core.Config;
using ChessSage.Core.Rules.Variants;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules
{
    /// <summary>
    /// 规则引擎工厂：按棋类 id 构造对应的 <see cref="VariantRuleBase"/> 实现。
    /// 让对局会话 / AI / 编排器都只依赖统一的规则引擎接口，不再硬编码象棋。
    /// </summary>
    public static class RuleEngineFactory
    {
        public static VariantRuleBase Create(GameConfig config)
            => Create(config.Variant, config.Board, config.PiecesA, config.PiecesB, config.Rules);

        public static VariantRuleBase Create(string variant, JObject board, JObject piecesA, JObject piecesB, JObject rules)
        {
            switch (variant)
            {
                case "wuziqi": return new WuziqiRuleEngine(board, piecesA, piecesB, rules);
                case "weiqi": return new WeiqiRuleEngine(board, piecesA, piecesB, rules);
                case "heibaiqi": return new HeibaiqiRuleEngine(board, piecesA, piecesB, rules);
                case "tiaoqi": return new TiaoqiRuleEngine(board, piecesA, piecesB, rules);
                case "dongwuqi": return new DongwuqiRuleEngine(board, piecesA, piecesB, rules);
                default: return new XiangqiRuleEngine(board, piecesA, piecesB, rules);
            }
        }
    }
}