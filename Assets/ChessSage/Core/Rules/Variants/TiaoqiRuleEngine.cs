using System.Collections.Generic;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules.Variants
{
    /// <summary>
    /// 中国跳棋规则引擎 —— step + hop（可连跳）双原子移动体系，不吃子。
    /// 逐语义移植自 legacy-web/tiaoqi/rule_engine.py。
    /// 坐标系统：[row, col] 双倍列坐标，Piece.X/Y 对应 Python 的 [row, col]；
    /// 邻接与跳跃由 board.json 的 adjacency 邻接表驱动，落点 = 2*相邻位 - 当前位。
    /// </summary>
    public sealed class TiaoqiRuleEngine : JumpRayRuleBase
    {
        readonly HashSet<string> _positions = new HashSet<string>();
        readonly Dictionary<string, List<int[]>> _adjacency = new Dictionary<string, List<int[]>>();
        readonly Dictionary<string, HashSet<string>> _campPositions = new Dictionary<string, HashSet<string>>();

        public TiaoqiRuleEngine(JObject board, JObject piecesRed, JObject piecesBlack, JObject rules)
            : base(board, piecesRed, piecesBlack, rules, "red", "black")
        {
            // 有效位置集合
            if (board["positions"] is JObject positions)
                foreach (var prop in positions.Properties()) _positions.Add(prop.Name);

            // 邻接表（hex6 六方向）
            if (board["adjacency"] is JObject adjacency)
                foreach (var prop in adjacency.Properties())
                {
                    var list = new List<int[]>();
                    if (prop.Value is JArray neighbors)
                        foreach (var n in neighbors)
                            if (n is JArray na && na.Count >= 2)
                                list.Add(new[] { na[0].Value<int>(), na[1].Value<int>() });
                    _adjacency[prop.Name] = list;
                }

            // 营区位置（红 rows 0-3、黑 rows 13-16）
            if (board["geometry"]?["camps"] is JObject camps)
                foreach (var sideProp in camps.Properties())
                {
                    var set = new HashSet<string>();
                    if (sideProp.Value["positions"] is JArray ps)
                        foreach (var p in ps)
                            if (p is JArray pa && pa.Count >= 2)
                                set.Add(Key(pa[0].Value<int>(), pa[1].Value<int>()));
                    _campPositions[sideProp.Name] = set;
                }
        }

        static string Key(int r, int c) => r + "," + c;

        List<int[]> Neighbors(int r, int c)
            => _adjacency.TryGetValue(Key(r, c), out var list) ? list : new List<int[]>();

        bool IsValidPos(int r, int c) => _positions.Contains(Key(r, c));

        bool IsInCamp(int r, int c, string side)
            => _campPositions.TryGetValue(side, out var set) && set.Contains(Key(r, c));

        // ══════════════════════════════════════════════════════════════
        // 合法移动：step + hop（连跳）
        // ══════════════════════════════════════════════════════════════

        public override List<int[]> GetValidMoves(Piece piece, BoardState boardState)
        {
            var result = new List<int[]>();
            if (string.IsNullOrEmpty(piece.Type)) return result;

            var moveDefs = GetMoveDefinitions(piece.Type, piece.Side);
            if (moveDefs.Count == 0) return result;

            var seen = new HashSet<string>();
            foreach (var moveDef in moveDefs)
            {
                var kind = moveDef["kind"]?.Value<string>();
                List<int[]> moves;
                if (kind == "step") moves = StepMoves(moveDef, piece, boardState);
                else if (kind == "hop") moves = HopMoves(moveDef, piece, boardState);
                else continue;

                foreach (var m in moves)
                    if (seen.Add(Key(m[0], m[1]))) result.Add(m);
            }
            return result;
        }

        /// <summary>step 原语：单步移动到相邻位。</summary>
        List<int[]> StepMoves(JObject moveDef, Piece piece, BoardState boardState)
        {
            var land = moveDef["land"]?.Value<string>() ?? "empty";
            var where = moveDef["where"] as JArray ?? new JArray();

            var moves = new List<int[]>();
            foreach (var neighbor in Neighbors(piece.X, piece.Y))
            {
                var target = GetPieceAt(neighbor[0], neighbor[1], boardState);
                if (land == "empty" && target != null) continue;
                if (land == "any" && target != null && target.Side == piece.Side) continue;
                if (where.Count > 0 && !WherePasses(where, piece, boardState, neighbor[0], neighbor[1])) continue;
                moves.Add(new[] { neighbor[0], neighbor[1] });
            }
            return moves;
        }

        /// <summary>hop 原语：跳过相邻棋子，落点为空的对称位置；chain 时递归搜索连跳终点。</summary>
        List<int[]> HopMoves(JObject moveDef, Piece piece, BoardState boardState)
        {
            var land = moveDef["land"]?.Value<string>() ?? "empty";
            var chain = moveDef["chain"]?.Value<bool>() ?? false;
            var where = moveDef["where"] as JArray ?? new JArray();

            int cr = piece.X, cc = piece.Y;
            var results = new List<int[]>();
            foreach (var neighbor in Neighbors(cr, cc))
            {
                // 相邻位必须有棋子（任意方）才能跳
                if (GetPieceAt(neighbor[0], neighbor[1], boardState) == null) continue;

                int lr = 2 * neighbor[0] - cr, lc = 2 * neighbor[1] - cc;
                if (!IsValidPos(lr, lc)) continue;

                var target = GetPieceAt(lr, lc, boardState);
                if (land == "empty" && target != null) continue;
                if (land == "any" && target != null && target.Side == piece.Side) continue;
                if (where.Count > 0 && !WherePasses(where, piece, boardState, lr, lc)) continue;

                results.Add(new[] { lr, lc });
                if (chain)
                {
                    // 每条邻接分支独立 visited，避免不同路径互相阻塞
                    var visited = new HashSet<string> { Key(cr, cc), Key(lr, lc) };
                    results.AddRange(HopChain(lr, lc, visited, boardState, piece, land, where));
                }
            }
            return results;
        }

        /// <summary>递归搜索连跳终点；被跳过的棋子保留原位，跳跃棋子自身原位视为已离开。</summary>
        List<int[]> HopChain(int cr, int cc, HashSet<string> visited, BoardState boardState,
            Piece piece, string land, JArray where)
        {
            var results = new List<int[]>();
            foreach (var neighbor in Neighbors(cr, cc))
            {
                var adjPiece = GetPieceAt(neighbor[0], neighbor[1], boardState);
                // 排除正在跳跃的棋子本身（其原位置视为空，不能作为跳板）
                if (adjPiece != null && adjPiece.Id == piece.Id) adjPiece = null;
                if (adjPiece == null) continue;

                int lr = 2 * neighbor[0] - cr, lc = 2 * neighbor[1] - cc;
                if (!IsValidPos(lr, lc)) continue;
                if (visited.Contains(Key(lr, lc))) continue;

                var target = GetPieceAt(lr, lc, boardState);
                if (land == "empty" && target != null) continue;
                if (land == "any" && target != null && target.Side == piece.Side) continue;
                if (where.Count > 0 && !WherePasses(where, piece, boardState, lr, lc)) continue;

                results.Add(new[] { lr, lc });
                visited.Add(Key(lr, lc));
                results.AddRange(HopChain(lr, lc, visited, boardState, piece, land, where));
            }
            return results;
        }

        // ══════════════════════════════════════════════════════════════
        // where 条件表达式（与 Python _eval_condition 等价，营区判定基于 camps）
        // ══════════════════════════════════════════════════════════════

        bool WherePasses(JArray conditions, Piece piece, BoardState boardState, int r, int c)
        {
            foreach (var cond in conditions)
                if (cond is JObject co && !ConditionPasses(co, piece, boardState, r, c)) return false;
            return true;
        }

        bool ConditionPasses(JObject cond, Piece piece, BoardState boardState, int r, int c)
        {
            if (cond.Count != 1) return true;
            string key = null;
            JToken value = null;
            foreach (var p in cond.Properties()) { key = p.Name; value = p.Value; }

            if (key == "not") return !ConditionPasses((JObject)value, piece, boardState, r, c);
            if (key == "and")
            {
                foreach (var sub in (JArray)value)
                    if (!ConditionPasses((JObject)sub, piece, boardState, r, c)) return false;
                return true;
            }
            if (key == "or")
            {
                foreach (var sub in (JArray)value)
                    if (ConditionPasses((JObject)sub, piece, boardState, r, c)) return true;
                return false;
            }

            (int r, int c) ResolvePos(JToken val)
            {
                string posVar = "$dest";
                if (val is JObject vo) posVar = vo["pos"]?.Value<string>() ?? "$dest";
                else if (val?.Type == JTokenType.String) posVar = val.Value<string>();
                return posVar == "$self" ? (piece.X, piece.Y) : (r, c);
            }

            if (key == "in_region")
            {
                string region = value is JObject vo ? vo["region"]?.Value<string>() ?? "$opponent_camp" : value.Value<string>();
                if (region == "$full_board") return true;
                if (region == "$opponent_camp") region = Opposite(piece.Side);
                var pos = ResolvePos(value);
                return IsInCamp(pos.r, pos.c, region);
            }
            if (key == "at_row")
            {
                int row;
                (int r, int c) pos;
                if (value is JObject vo) { pos = ResolvePos(value); row = vo["row"]?.Value<int>() ?? -1; }
                else { pos = (r, c); row = value.Value<int>(); }
                return pos.r == row;
            }
            if (key == "at_col")
            {
                int col;
                (int r, int c) pos;
                if (value is JObject vo) { pos = ResolvePos(value); col = vo["col"]?.Value<int>() ?? -1; }
                else { pos = (r, c); col = value.Value<int>(); }
                return pos.c == col;
            }
            return true;
        }

        // ══════════════════════════════════════════════════════════════
        // 动作枚举 / 执行 / 胜负判定
        // ══════════════════════════════════════════════════════════════

        public override List<GameAction> GetAllActions(BoardState boardState, string side)
        {
            var actions = new List<GameAction>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || p.Side != side) continue;
                foreach (var m in GetValidMoves(p, boardState))
                    actions.Add(new GameAction("move", p.Id, m[0], m[1]));
            }
            return actions;
        }

        public override ActionOutcome ApplyAction(BoardState boardState, GameAction action, string side)
        {
            var outcome = new ActionOutcome();
            if (action == null || action.Kind != "move")
            {
                outcome.Message = "不支持的动作";
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

            // 跳棋不吃子：仅移动落点
            piece.X = action.X;
            piece.Y = action.Y;
            outcome.Ok = true;
            return outcome;
        }

        public override GameOutcome CheckOutcome(BoardState boardState)
        {
            // all_in_camp：己方全部存活棋子进入对方营区（红在黑营 / 黑在红营）
            foreach (var side in Sides)
            {
                var opponent = Opposite(side);
                bool allIn = true;
                foreach (var p in boardState.Pieces)
                {
                    if (!p.IsAlive || p.Side != side) continue;
                    if (!IsInCamp(p.X, p.Y, opponent)) { allIn = false; break; }
                }
                if (allIn)
                    return new GameOutcome { Ended = true, Winner = side, Condition = "all_in_camp" };
            }

            // 困毙对方：当前行动方无任何合法移动
            var sideToMove = boardState.CurrentTurn;
            if (GetAllActions(boardState, sideToMove).Count == 0)
                return new GameOutcome { Ended = true, Winner = Opposite(sideToMove), Condition = "stalemate" };

            return GameOutcome.Ongoing;
        }
    }
}