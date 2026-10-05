using System.Collections.Generic;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules.Variants
{
    /// <summary>
    /// 五子棋规则引擎 —— 落子类游戏（棋子不移动）。
    /// 逐语义移植自 legacy-web/wuziqi/rule_engine.py 与 main.py：
    ///   · 合法落点为全棋盘空点（main.py /api/valid_moves 的无 piece_id 分支）；
    ///   · 胜负检测为横/竖/斜五连珠（check_five_in_a_row），棋盘下满判和（is_game_over）。
    /// </summary>
    public sealed class WuziqiRuleEngine : JumpRayRuleBase
    {
        public WuziqiRuleEngine(JObject board, JObject piecesBlack, JObject piecesWhite, JObject rules)
            : base(board, piecesBlack, piecesWhite, rules, "black", "white") { }

        /// <summary>五子棋为落子类游戏。</summary>
        public override bool IsPlacementGame => true;

        // ══════════════════════════════════════════════════════════════
        // 合法落点
        // ══════════════════════════════════════════════════════════════

        /// <summary>返回全棋盘可落子的空点。遍历顺序与 Python 一致（x 外层、y 内层）。</summary>
        public override List<int[]> GetValidPlacements(BoardState boardState, string side)
        {
            var moves = new List<int[]>();
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    if (GetPieceAt(x, y, boardState) == null)
                        moves.Add(new[] { x, y });
            return moves;
        }

        /// <summary>枚举当前方全部落子动作。</summary>
        public override List<GameAction> GetAllActions(BoardState boardState, string side)
        {
            var actions = new List<GameAction>();
            foreach (var p in GetValidPlacements(boardState, side))
                actions.Add(new GameAction("place", null, p[0], p[1]));
            return actions;
        }

        // ══════════════════════════════════════════════════════════════
        // 落子
        // ══════════════════════════════════════════════════════════════

        /// <summary>执行落子：写入一枚棋子并追加 move_history。回合切换由调用方负责。</summary>
        public override ActionOutcome ApplyAction(BoardState boardState, GameAction action, string side)
        {
            var outcome = new ActionOutcome();
            if (action == null || action.Kind != "place")
            {
                outcome.Ok = false;
                outcome.Message = "五子棋仅支持 place 落子";
                return outcome;
            }

            int x = action.X, y = action.Y;
            if (!InBounds(x, y))
            {
                outcome.Ok = false;
                outcome.Message = "位置超出棋盘范围";
                return outcome;
            }
            if (GetPieceAt(x, y, boardState) != null)
            {
                outcome.Ok = false;
                outcome.Message = "该位置已有棋子";
                return outcome;
            }

            string label = side == "black" ? "●" : "○";
            string id = side + "_stone_" + x + "_" + y + "_" + boardState.Pieces.Count;
            var piece = new Piece(id, "stone", label, side, x, y)
            {
                IsAlive = true,
                CustomProperties = new JObject(),
            };
            boardState.Pieces.Add(piece);

            boardState.MoveHistory.Add(new JObject
            {
                ["piece_id"] = id,
                ["from"] = null,
                ["to"] = new JArray(x, y),
                ["captured"] = null,
            });

            outcome.Ok = true;
            return outcome;
        }

        // ══════════════════════════════════════════════════════════════
        // 胜负判定
        // ══════════════════════════════════════════════════════════════

        public override GameOutcome CheckOutcome(BoardState boardState)
        {
            var winner = CheckFiveInARow(boardState);
            if (winner != null)
                return new GameOutcome { Ended = true, Winner = winner, Condition = "five_in_a_row", Draw = false };

            int totalCells = Width * Height;
            int alive = 0;
            foreach (var p in boardState.Pieces)
                if (p.IsAlive) alive++;

            if (alive >= totalCells)
                return new GameOutcome { Ended = true, Winner = null, Condition = "stalemate", Draw = true };

            return new GameOutcome { Ended = false, Winner = null, Condition = null, Draw = false };
        }

        /// <summary>
        /// 五连珠检测：逐格沿四个方向统计连续同色子数，≥5 即获胜。
        /// 扫描顺序（x 升序 → y 升序 → 方向 水平/垂直/\//）与 Python 完全一致。
        /// </summary>
        public string CheckFiveInARow(BoardState boardState)
        {
            // 位置 → 阵营（活子；重复坐标取后写入者，与 Python dict 行为一致）
            var boardSide = new Dictionary<(int x, int y), string>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive) continue;
                boardSide[(p.X, p.Y)] = p.Side;
            }

            var directions = new[] { (1, 0), (0, 1), (1, 1), (1, -1) };

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {
                    if (!boardSide.TryGetValue((x, y), out var side) || side == null) continue;

                    foreach (var (dx, dy) in directions)
                    {
                        int count = 1;
                        int nx = x + dx, ny = y + dy;
                        while (InBounds(nx, ny) && boardSide.TryGetValue((nx, ny), out var s1) && s1 == side)
                        {
                            count++;
                            nx += dx;
                            ny += dy;
                        }
                        nx = x - dx; ny = y - dy;
                        while (InBounds(nx, ny) && boardSide.TryGetValue((nx, ny), out var s2) && s2 == side)
                        {
                            count++;
                            nx -= dx;
                            ny -= dy;
                        }

                        if (count >= 5) return side;
                    }
                }

            return null;
        }
    }
}