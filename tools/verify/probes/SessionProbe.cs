using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Mechanism;

namespace ChessSage.Harness
{
    /// <summary>
    /// 对局会话探针：验证 GameSession 通过统一规则引擎驱动全部棋类，
    /// 覆盖「移动类」与「落子类」两条回合流程（玩家 / AI 交替、胜负、悔棋）。
    /// </summary>
    public static class SessionProbe
    {
        static GameSession NewSession(string variant, int seed = 2026)
        {
            var loader = new ConfigLoader(ProbeEnv.ConfigsRoot);
            var config = loader.LoadVariant(variant);
            return new GameSession(config, new MechanismEngine(config.Rules), aiSeed: seed);
        }

        public static void Run()
        {
            XiangqiFlow();
            PlacementFlow("wuziqi");
            PlacementFlow("heibaiqi");
            PlacementFlow("weiqi");
            Smoke("tiaoqi");
            Smoke("dongwuqi");
        }

        static void XiangqiFlow()
        {
            var s = NewSession("xiangqi");
            ProbeEnv.Check(s.CurrentTurn == "red", "象棋初始应为红方");
            ProbeEnv.Check(!s.IsAiTurn, "象棋红方应为玩家");
            ProbeEnv.Check(!s.IsPlacementGame, "象棋应为移动类");

            var move = s.MakePlayerMove("r_soldier_1", 0, 5);
            ProbeEnv.Check(move.Success, $"象棋玩家走子失败：{move.Message}");
            ProbeEnv.Check(s.CurrentTurn == "black", "走子后应轮到黑方");
            ProbeEnv.Check(s.IsAiTurn, "黑方应为 AI");

            var ai = s.MakeAiMove();
            ProbeEnv.Check(ai.Success, $"象棋 AI 走子失败：{ai.Message}");
            ProbeEnv.Check(s.CurrentTurn == "red", "AI 走子后应轮到红方");

            // 悔一步：撤销 AI 走子，回到黑方回合、玩家兵仍在 (0,5)。
            ProbeEnv.Check(s.UndoLastMove(), "悔棋应成功");
            ProbeEnv.Check(s.CurrentTurn == "black", "悔棋一次后应回到黑方回合");
            var soldier = s.State.GetPieceById("r_soldier_1");
            ProbeEnv.Check(soldier.X == 0 && soldier.Y == 5, "悔棋应还原 AI 走子");

            // 再悔一步：撤销玩家走子，兵回到 (0,6)。
            ProbeEnv.Check(s.UndoLastMove(), "连续悔棋应成功");
            ProbeEnv.Check(s.CurrentTurn == "red", "悔棋两步后应回到红方回合");
            soldier = s.State.GetPieceById("r_soldier_1");
            ProbeEnv.Check(soldier.X == 0 && soldier.Y == 6, "悔棋应还原兵的位置");
        }

        static void PlacementFlow(string variant)
        {
            var s = NewSession(variant);
            ProbeEnv.Check(s.IsPlacementGame, $"{variant} 应为落子类");

            var placements = s.GetValidPlacements();
            ProbeEnv.Check(placements.Count > 0, $"{variant} 开局应有合法落点");

            var p = placements[0];
            var place = s.MakePlayerPlace(p[0], p[1]);
            ProbeEnv.Check(place.Success, $"{variant} 玩家落子失败：{place.Message}");

            int guard = 0;
            while (s.IsAiTurn && !s.IsEnded && guard++ < 8)
            {
                var ai = s.MakeAiMove();
                ProbeEnv.Check(ai.Success, $"{variant} AI 落子失败：{ai.Message}");
            }
            ProbeEnv.Check(s.State.Pieces.Count >= 2, $"{variant} 双方应至少各落一子（实际 {s.State.Pieces.Count}）");
        }

        static void Smoke(string variant)
        {
            var s = NewSession(variant);
            ProbeEnv.Check(!s.IsPlacementGame, $"{variant} 应为移动类");
            var actions = s.Rules.GetAllActions(s.State, s.CurrentTurn);
            ProbeEnv.Check(actions.Count > 0, $"{variant} 开局应有合法动作");
        }
    }
}