using System;
using System.Collections.Generic;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules.Variants
{
    /// <summary>
    /// 象棋规则引擎。移动原语完全复用 <see cref="JumpRayRuleBase"/>（jump/ray + 条件表达式），
    /// 本类只补充象棋特有的将军 / 将死 / 将帅被吃判定，避免与移动逻辑重复实现。
    /// </summary>
    public sealed class XiangqiRuleEngine : JumpRayRuleBase
    {
        public XiangqiRuleEngine(JObject board, JObject piecesRed, JObject piecesBlack, JObject rules)
            : base(board, piecesRed, piecesBlack, rules, "red", "black")
        {
            KingTypes.Add("general");
        }

        public override string VariantId => "xiangqi";
        public override bool SupportsCheckRules => true;

        // ══════════════════════════════════════════════════════════════
        // 动作枚举 / 执行 / 胜负
        // ══════════════════════════════════════════════════════════════

        public override List<GameAction> GetAllActions(BoardState boardState, string side)
        {
            var actions = new List<GameAction>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || p.Side != side) continue;
                foreach (var pos in GetValidMoves(p, boardState))
                    actions.Add(new GameAction("move", p.Id, pos[0], pos[1]));
            }
            return actions;
        }

        public override ActionOutcome ApplyAction(BoardState boardState, GameAction action, string side)
        {
            var outcome = new ActionOutcome();
            if (action == null || action.Kind != "move")
            {
                outcome.Message = "象棋动作必须为 move";
                return outcome;
            }

            var piece = boardState.GetPieceById(action.PieceId);
            if (piece == null || !piece.IsAlive || piece.Side != side)
            {
                outcome.Message = "棋子不存在或不属于该方";
                return outcome;
            }

            bool legal = false;
            foreach (var m in GetValidMoves(piece, boardState))
                if (m[0] == action.X && m[1] == action.Y) { legal = true; break; }
            if (!legal)
            {
                outcome.Message = "非法移动";
                return outcome;
            }

            var target = GetPieceAt(action.X, action.Y, boardState);
            piece.X = action.X;
            piece.Y = action.Y;
            if (target != null && !IsInvulnerable(target))
            {
                target.IsAlive = false;
                outcome.CapturedIds.Add(target.Id);
            }

            outcome.Ok = true;
            outcome.Message = "走棋成功";
            return outcome;
        }

        public override GameOutcome CheckOutcome(BoardState boardState)
        {
            var captured = IsGeneralCaptured(boardState);
            if (captured != null)
                return new GameOutcome { Ended = true, Winner = captured, Condition = "general_captured" };

            foreach (var side in Sides)
                if (IsCheckmate(side, boardState))
                    return new GameOutcome { Ended = true, Winner = Opposite(side), Condition = "checkmate" };

            return GameOutcome.Ongoing;
        }

        // ══════════════════════════════════════════════════════════════
        // 将军 / 将死 / 将帅被吃
        // ══════════════════════════════════════════════════════════════

        public override bool IsInCheck(string side, BoardState boardState)
        {
            Piece general = null;
            foreach (var p in boardState.Pieces)
                if (p.IsAlive && KingTypes.Contains(p.Type) && p.Side == side) { general = p; break; }
            if (general == null) return false;

            int gx = general.X, gy = general.Y;

            // 将帅照面（飞将）
            Piece otherGeneral = null;
            foreach (var p in boardState.Pieces)
                if (p.IsAlive && KingTypes.Contains(p.Type) && p.Side != side) { otherGeneral = p; break; }

            if (otherGeneral != null && otherGeneral.X == gx)
            {
                int ogY = otherGeneral.Y;
                int yMin = Math.Min(gy, ogY), yMax = Math.Max(gy, ogY);
                bool blocked = false;
                for (int y = yMin + 1; y < yMax; y++)
                {
                    var mid = boardState.GetPieceAt(gx, y);
                    if (mid != null && mid.Id != general.Id && mid.Id != otherGeneral.Id) { blocked = true; break; }
                }
                if (!blocked) return true;
            }

            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || p.Side == side) continue;
                foreach (var m in GetValidMoves(p, boardState))
                    if (m[0] == gx && m[1] == gy) return true;
            }
            return false;
        }

        public override bool IsCheckmate(string side, BoardState boardState)
        {
            if (!IsInCheck(side, boardState)) return false;

            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || p.Side != side) continue;
                foreach (var move in GetValidMoves(p, boardState))
                {
                    int ox = p.X, oy = p.Y;
                    p.X = move[0]; p.Y = move[1];
                    Piece captured = null;
                    foreach (var q in boardState.Pieces)
                        if (!ReferenceEquals(q, p) && q.IsAlive && q.X == move[0] && q.Y == move[1])
                        {
                            captured = q;
                            if (!IsInvulnerable(captured)) captured.IsAlive = false;
                            break;
                        }
                    bool stillCheck = IsInCheck(side, boardState);
                    p.X = ox; p.Y = oy;
                    if (captured != null) captured.IsAlive = true;
                    if (!stillCheck) return false;
                }
            }
            return true;
        }

        public override string IsGeneralCaptured(BoardState boardState)
        {
            string winner = null;
            var alive = new HashSet<string>();
            foreach (var side in Sides) alive.Add(side);
            foreach (var p in boardState.Pieces)
                if (KingTypes.Contains(p.Type) && p.IsAlive) alive.Remove(p.Side);

            // 恰有一方将帅存活时，另一方获胜。
            foreach (var dead in alive)
                winner = Opposite(dead);
            return winner;
        }
    }
}