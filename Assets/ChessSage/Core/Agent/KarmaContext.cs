using System.Collections.Generic;

namespace ChessSage.Core.Agent
{
    /// <summary>一次业力计价的上下文（道境 / 单次上限 / 道级加数 / 减费技能开关 / 改动作用域）。</summary>
    public sealed class KarmaContext
    {
        public string Realm;
        public int SingleMax;
        public int Flat;
        public bool EfficiencyFraud;
        public bool HellHungryDiscount;
        public bool HeavenAsuraDiscount;

        /// <summary>
        /// 本次提交**成功写入**触及的配置作用域（E3-C）：ui_config / board_state / pieces / rules / objectives。
        /// <see cref="KarmaFormula"/> 取其中最大权重作为 scopeWeight（空/null = 中性 1.0，既有黄金值不变）。
        /// 集合语义：只记录真实产生变更的切片——读工具、commit_preview、undo、失败调用一律不入列。
        /// </summary>
        public IReadOnlyList<string> ScopeTags;
    }
}
