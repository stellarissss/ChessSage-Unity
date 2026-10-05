using System;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 业障模型（对应 legacy-web/samsara/karma.py 的 KarmaSystem）：
    /// 作弊增加业力，下棋消业减少业力。
    /// </summary>
    public sealed class KarmaSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _events;

        public KarmaSystem(SamsaraState state, string karmaEventsPath)
        {
            _state = state;
            _events = LoadEvents(karmaEventsPath);
        }

        private static JObject LoadEvents(string path)
        {
            var loaded = SamsaraJson.LoadObject(path);
            return loaded ?? DefaultEvents();
        }

        private static JObject DefaultEvents()
        {
            return new JObject
            {
                ["xiangqi"] = new JObject
                {
                    ["capture_pawn"] = 8,
                    ["capture_medium"] = 15,
                    ["capture_rook"] = 25,
                    ["check"] = 20,
                    ["checkmate"] = 35,
                    ["pawn_cross"] = 10,
                    ["captured"] = 5,
                },
                ["wuziqi"] = new JObject
                {
                    ["three"] = 10,
                    ["four"] = 20,
                    ["block_three"] = 8,
                    ["block_four"] = 18,
                    ["double_three"] = 15,
                    ["win"] = 35,
                },
                ["weiqi"] = new JObject
                {
                    ["capture_small"] = 10,
                    ["capture_large"] = 20,
                    ["life"] = 15,
                    ["captured"] = 5,
                    ["corner"] = 12,
                    ["endgame"] = 8,
                },
                ["dongwuqi"] = new JObject
                {
                    ["capture_normal"] = 10,
                    ["capture_overrank"] = 25,
                    ["captured"] = 5,
                    ["approach"] = 12,
                    ["win"] = 35,
                },
                ["tiaoqi"] = new JObject
                {
                    ["jump_3"] = 10,
                    ["jump_5"] = 20,
                    ["home"] = 15,
                    ["single_move"] = 3,
                    ["all_home"] = 35,
                },
                ["heibaiqi"] = new JObject
                {
                    ["flip_small"] = 8,
                    ["flip_medium"] = 15,
                    ["flip_large"] = 25,
                    ["corner"] = 20,
                    ["flipped"] = 5,
                    ["win"] = 35,
                },
            };
        }

        /// <summary>消业：下棋事件减少业力，返回实际减少量。</summary>
        public int Recover(string gameType, string eventType, JObject eventData = null)
        {
            var gameEvents = _events[gameType] as JObject;
            var baseAmount = SamsaraJson.GetInt(gameEvents, eventType, 0);
            if (baseAmount <= 0) return 0;
            var modifiers = _state.GetSkillModifiers();
            var multiplier = SamsaraJson.GetDouble(modifiers, "karma_recover_multiplier", 1.0);
            var amount = (int)(baseAmount * multiplier);
            _state.DecreaseKarma(amount);
            return amount;
        }

        /// <summary>作弊：增加业力。返回 (actual, isOverdraft, overshootAmount)。</summary>
        public Tuple<int, bool, double> Consume(int amount, bool allowOverdraft = true)
        {
            var maxSingle = _state.GetInt("karma_single_max", 120);
            var modifiers = _state.GetSkillModifiers();
            maxSingle += SamsaraJson.GetInt(modifiers, "karma_single_max_bonus", 0);
            if (amount > maxSingle) return Tuple.Create(0, false, 0.0);
            var result = _state.IncreaseKarma(amount);
            return result;
        }

        /// <summary>退还业力（作弊失败时全额退还）。</summary>
        public void Refund(int amount)
        {
            _state.DecreaseKarma(amount);
        }

        /// <summary>返回当前业力摘要（current / max / single_max / initial）。</summary>
        public JObject GetState()
        {
            var modifiers = _state.GetSkillModifiers();
            return new JObject
            {
                ["current"] = _state.GetKarma(),
                ["max"] = _state.GetInt("karma_max", 120) + SamsaraJson.GetInt(modifiers, "karma_max_bonus", 0),
                ["single_max"] = _state.GetInt("karma_single_max", 120) + SamsaraJson.GetInt(modifiers, "karma_single_max_bonus", 0),
                ["initial"] = _state.GetInt("initial_karma", 50) - SamsaraJson.GetInt(modifiers, "initial_karma_reduction", 0),
            };
        }

        public bool CanCheat()
        {
            return _state.GetKarma() >= 0;
        }
    }
}