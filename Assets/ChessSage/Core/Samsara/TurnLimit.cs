using System;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 回合上限系统（对应 legacy-web/samsara/turn_limit.py 的 TurnLimitSystem）。
    /// 读取状态中的 turn_limit / current_turn，提供回合推进、剩余与进度查询。
    /// </summary>
    public sealed class TurnLimitSystem
    {
        private readonly SamsaraState _state;

        public TurnLimitSystem(SamsaraState state)
        {
            _state = state;
        }

        /// <summary>回合 +1 并返回是否已达上限。</summary>
        public bool Tick()
        {
            _state.IncrementTurn();
            return IsOver();
        }

        /// <summary>剩余回合数（不小于 0）。</summary>
        public int GetRemaining()
        {
            var limit = _state.GetInt("turn_limit", 20);
            var current = _state.GetInt("current_turn", 0);
            return Math.Max(0, limit - current);
        }

        /// <summary>重置回合上限与当前回合。</summary>
        public void Reset(int limit = 20)
        {
            _state.SetTurnLimit(limit);
            _state.ResetTurn();
        }

        /// <summary>是否已达回合上限。</summary>
        public bool IsOver()
        {
            return GetRemaining() <= 0;
        }

        /// <summary>回合进度百分比（0-100）。</summary>
        public double GetProgress()
        {
            var limit = _state.GetInt("turn_limit", 20);
            var current = _state.GetInt("current_turn", 0);
            return Math.Min(100, (int)(current / (double)limit * 100));
        }
    }
}