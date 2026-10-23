using System;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 可注入随机源——业力波动 σ、激增触发、减费折扣抽签的唯一出口（工程侧 §7.1/§7.4）。
    ///
    /// 【设计要点】
    /// 1. 纯接口、零引擎依赖：单测可钉死序列（FixedRandom/ScriptedRandom），
    ///    生产用 <see cref="DeterministicRng"/> 按「会话种子 + 请求序号」复现（QA 回放）。
    /// 2. 只暴露 <see cref="NextDouble"/>：消费点全部是概率比较（&lt; 阈值）与区间映射，
    ///    不需要整数/选择类 API——保持最小面，防止滥用破坏可复现性。
    /// </summary>
    public interface IRandom
    {
        /// <summary>返回 [0,1) 上的均匀分布 double。</summary>
        double NextDouble();
    }
}
