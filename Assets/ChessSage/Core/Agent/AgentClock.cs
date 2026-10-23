using System.Diagnostics;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 单调毫秒时钟——延迟遥测（§13.7 ttft_ms / round_ms / total_ms）的**唯一时间源**。
    ///
    /// 【为什么不用 DateTime】
    /// ① DateTime 受系统时钟调整（NTP 回拨/夏令时）影响，可能产出负延迟；
    /// ② 测试需要把 20s 压到毫秒级且**零 wall-clock**（QA §4.3 flaky 政策）；
    /// ③ 单调计数器在 Mono(net48 harness) 与 .NET 6(dotnet test) 上语义一致。
    ///
    /// 【可注入点】<see cref="AgentEngine.Clock"/>——生产注入 <see cref="StopwatchClock"/>，
    /// 测试注入虚拟时钟（手动推进），因此遥测断言完全确定、不依赖真实耗时。
    /// </summary>
    public interface IMonotonicClock
    {
        /// <summary>自时钟创建（或虚拟基准点）起经过的毫秒数；单调不减。</summary>
        long ElapsedMs { get; }
    }

    /// <summary>生产时钟：<see cref="Stopwatch"/>（单调、不受系统时间调整影响）。</summary>
    public sealed class StopwatchClock : IMonotonicClock
    {
        readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public long ElapsedMs => _stopwatch.ElapsedMilliseconds;
    }
}
