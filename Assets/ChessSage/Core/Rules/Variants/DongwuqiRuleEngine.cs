using System;
using System.Collections.Generic;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules.Variants
{
    /// <summary>
    /// 动物棋（斗兽棋）规则引擎 —— 逐语义移植自 legacy-web/dongwuqi/rule_engine.py。
    /// 特有规则：等级吃子（rank，鼠吃象循环）、水域（water，鼠独入水）、
    /// 陷阱（trap，踩敌方陷阱即死 + 降级为 0）、兽穴（den，进入对方兽穴获胜）、
    /// 狮虎跳河（ray 的 path_constraint）。
    /// </summary>
    public sealed class DongwuqiRuleEngine : JumpRayRuleBase
    {
        readonly Dictionary<string, HashSet<(int x, int y)>> _regionCells = new Dictionary<string, HashSet<(int x, int y)>>();
        readonly HashSet<string> _terrainTypes = new HashSet<string>();

        // 动物棋标准陷阱格（斗兽棋标准 6 格）：未由 board_state 表示时按此兜底合成。
        static readonly (int x, int y, string side)[] StandardTraps =
        {
            (2, 0, "black"), (4, 0, "black"), (3, 1, "black"),
            (2, 8, "red"), (4, 8, "red"), (3, 7, "red"),
        };

        public DongwuqiRuleEngine(JObject board, JObject piecesRed, JObject piecesBlack, JObject rules)
            : base(board, piecesRed, piecesBlack, rules, "red", "black")
        {
            BuildRegionCells();

            // 地形棋子类型：category == "terrain"（陷阱等场地原语，不参与移动/吃子）
            foreach (var kv in PiecesBySide)
                foreach (var prop in kv.Value.Properties())
                    if (prop.Value is JObject po && po["category"]?.Value<string>() == "terrain")
                        _terrainTypes.Add(prop.Name);
            foreach (var kv in CustomPiecesBySide)
                foreach (var cp in kv.Value)
                    if (cp is JObject c && c["category"]?.Value<string>() == "terrain" &&
                        c["type"]?.Value<string>() is string t)
                        _terrainTypes.Add(t);
        }

        void BuildRegionCells()
        {
            if (Geometry["regions"] is not JObject regions) return;
            foreach (var prop in regions.Properties())
            {
                if (prop.Value is not JObject r || r["cells"] is not JArray cells) continue;
                var set = new HashSet<(int x, int y)>();
                foreach (var c in cells)
                    if (c is JArray a && a.Count >= 2) set.Add((a[0].Value<int>(), a[1].Value<int>()));
                if (set.Count > 0) _regionCells[prop.Name] = set;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 区域查询（动物棋特有）
        // ══════════════════════════════════════════════════════════════

        public bool IsInWater(int x, int y)
            => _regionCells.TryGetValue("water", out var s) && s.Contains((x, y));

        bool IsInOwnDen(int x, int y, string side)
            => _regionCells.TryGetValue(side == "red" ? "den_red" : "den_black", out var s) && s.Contains((x, y));

        public bool IsInEnemyDen(int x, int y, string side)
            => _regionCells.TryGetValue(side == "red" ? "den_black" : "den_red", out var s) && s.Contains((x, y));

        public override bool IsInRegion(int x, int y, string regionRef, string side)
        {
            if (regionRef == "$full_board") return true;
            if (_regionCells.TryGetValue(regionRef, out var set)) return set.Contains((x, y));
            return base.IsInRegion(x, y, regionRef, side);
        }

        bool IsTerrainPiece(Piece p) => p != null && p.Type != null && _terrainTypes.Contains(p.Type);

        // ══════════════════════════════════════════════════════════════
        // 棋子/地形查询
        // ══════════════════════════════════════════════════════════════

        /// <summary>指定格的可交互棋子（跳过地形棋子——陷阱与动物共存于同一格）。</summary>
        public override Piece GetPieceAt(int x, int y, BoardState boardState)
        {
            foreach (var p in boardState.Pieces)
                if (p.IsAlive && p.X == x && p.Y == y && !IsTerrainPiece(p)) return p;
            return null;
        }

        List<Piece> GetTerrainPiecesAt(int x, int y, BoardState boardState)
        {
            var list = new List<Piece>();
            foreach (var p in boardState.Pieces)
                if (p.IsAlive && IsTerrainPiece(p) && p.X == x && p.Y == y) list.Add(p);

            // 未由 board_state 表示的标准陷阱兜底合成（已表示或已被吞噬的格不再合成）
            foreach (var st in StandardTraps)
            {
                if (st.x != x || st.y != y) continue;
                if (TrapCellExistsInState(boardState, st.x, st.y)) continue;
                if (ConsumedContains(EnsureConsumed(boardState), st.x, st.y)) continue;
                list.Add(new Piece($"terrain_trap_{st.side}_{st.x}_{st.y}", "trap", null, st.side, st.x, st.y));
            }
            return list;
        }

        bool IsInTrap(int x, int y, BoardState boardState)
        {
            foreach (var tp in GetTerrainPiecesAt(x, y, boardState))
                if (tp.Type == "trap") return true;
            return false;
        }

        bool IsInEnemyTrap(int x, int y, string side, BoardState boardState)
        {
            foreach (var tp in GetTerrainPiecesAt(x, y, boardState))
            {
                if (tp.Type != "trap") continue;
                if (tp.Side != side) return true;
            }
            return false;
        }

        List<JObject> GetEnemyTrapEffects(int x, int y, string side, BoardState boardState)
        {
            var effects = new List<JObject>();
            foreach (var tp in GetTerrainPiecesAt(x, y, boardState))
            {
                if (tp.Type != "trap") continue;
                if (tp.Side == side) continue;
                var cfg = GetPieceConfig(tp.Type, tp.Side);
                if (cfg?["effects"] is not JArray effs) continue;
                foreach (var e in effs)
                    if (e is JObject eo && (eo["target"]?.Value<string>() is "enemy" or "all"))
                        effects.Add(eo);
            }
            return effects;
        }

        // ══════════════════════════════════════════════════════════════
        // 陷阱吞噬
        // ══════════════════════════════════════════════════════════════

        bool TrapNeutralizeEnabled()
        {
            if (Rules["special_rules"] is JObject sr && sr["trap_neutralizes_rank"] is JObject tr)
            {
                var e = tr["enabled"];
                if (e != null && e.Type == JTokenType.Boolean) return e.Value<bool>();
            }
            return true;
        }

        /// <summary>踩中敌方陷阱的动物立即死亡；陷阱一次性消耗（记入 consumed_traps）。</summary>
        public bool IsKilledByTrap(Piece piece, BoardState boardState)
        {
            if (!TrapNeutralizeEnabled()) return false;
            if (piece == null || piece.Side == null) return false;
            var consumed = EnsureConsumed(boardState);
            if (ConsumedContains(consumed, piece.X, piece.Y)) return false;
            if (!IsInEnemyTrap(piece.X, piece.Y, piece.Side, boardState)) return false;

            piece.IsAlive = false;
            consumed.Add(new JArray(piece.X, piece.Y));
            ConsumeTrapAt(boardState, piece.X, piece.Y);
            return true;
        }

        void ConsumeTrapAt(BoardState boardState, int x, int y)
        {
            foreach (var tp in boardState.Pieces)
                if (tp.Type == "trap" && tp.IsAlive && tp.X == x && tp.Y == y) tp.IsAlive = false;
        }

        static JArray EnsureConsumed(BoardState boardState)
        {
            if (boardState.Extra["consumed_traps"] is JArray a) return a;
            var arr = new JArray();
            boardState.Extra["consumed_traps"] = arr;
            return arr;
        }

        static bool ConsumedContains(JArray consumed, int x, int y)
        {
            foreach (var t in consumed)
                if (t is JArray a && a.Count >= 2 && a[0].Value<int>() == x && a[1].Value<int>() == y) return true;
            return false;
        }

        static bool TrapCellExistsInState(BoardState boardState, int x, int y)
        {
            foreach (var p in boardState.Pieces)
                if (p.Type == "trap" && p.X == x && p.Y == y) return true;
            return false;
        }

        // ══════════════════════════════════════════════════════════════
        // 等级吃子（rank + 鼠克象 + 水域 + 陷阱降级）
        // ══════════════════════════════════════════════════════════════

        bool CanCaptureByRank(Piece attacker, Piece defender, BoardState boardState)
        {
            if (attacker.Side == defender.Side) return false;
            if (IsTerrainPiece(defender) || IsTerrainPiece(attacker)) return false;

            var ac = GetPieceConfig(attacker.Type, attacker.Side);
            var dc = GetPieceConfig(defender.Type, defender.Side);
            if (ac == null || dc == null) return true;

            int ar = ac["rank"]?.Value<int>() ?? 0;
            int dr = dc["rank"]?.Value<int>() ?? 0;
            bool attackerInWater = IsInWater(attacker.X, attacker.Y);
            bool defenderInWater = IsInWater(defender.X, defender.Y);

            // 1. 水域免疫：水中的防守方（鼠）只能被同样在水中的攻击方吃
            if (defenderInWater)
            {
                var dwr = (dc["capture"] as JObject)?["water_rules"] as JObject;
                if (dwr?["invulnerable_in_water"]?.Value<bool>() == true && !attackerInWater) return false;
            }

            // 2. 水中攻击限制：水中鼠不能吃岸上的特定目标（象）
            if (attackerInWater)
            {
                var awr = (ac["capture"] as JObject)?["water_rules"] as JObject;
                if (awr?["cannot_attack_from_water"] is JArray cannot && ContainsStr(cannot, defender.Type)) return false;
            }

            // 3. 陷阱降级：防守方在敌方陷阱中，等级归零
            bool trapNeutralized = false;
            if (TrapNeutralizeEnabled())
            {
                foreach (var eff in GetEnemyTrapEffects(defender.X, defender.Y, defender.Side, boardState))
                    if (eff["type"]?.Value<string>() == "rank_override")
                    {
                        dr = eff["value"]?.Value<int>() ?? 0;
                        trapNeutralized = true;
                        break;
                    }
            }

            // 4. 例外规则：鼠克象（can_eat）/ 象不能吃鼠（cannot_eat，陷阱降级时跳过）
            if ((ac["capture"] as JObject)?["exceptions"] is JArray exs)
                foreach (var e in exs)
                {
                    if (e is not JObject eo) continue;
                    if (eo["can_eat"]?.Value<string>() is string ce && ce == defender.Type) return true;
                    if (eo["cannot_eat"]?.Value<string>() is string cn && cn == defender.Type && !trapNeutralized) return false;
                }

            // 5. 默认等级规则
            return ar >= dr;
        }

        static bool ContainsStr(JArray arr, string v)
        {
            foreach (var t in arr) if (t.Value<string>() == v) return true;
            return false;
        }

        // ══════════════════════════════════════════════════════════════
        // 合法移动
        // ══════════════════════════════════════════════════════════════

        public override List<int[]> GetValidMoves(Piece piece, BoardState boardState)
        {
            var result = new List<int[]>();
            if (string.IsNullOrEmpty(piece.Type)) return result;
            if (IsTerrainPiece(piece)) return result;

            var moveDefs = GetMoveDefinitions(piece.Type, piece.Side);
            if (moveDefs.Count == 0) return result;

            var allMoves = new List<int[]>();
            foreach (var moveDef in moveDefs)
                foreach (var expDef in ExpandSymmetry(moveDef))
                    allMoves.AddRange(ExecuteMoveDef(expDef, piece, boardState));

            // 去重并保持首次出现顺序
            var seen = new HashSet<string>();
            foreach (var m in allMoves)
            {
                var key = m[0] + "," + m[1];
                if (seen.Add(key)) result.Add(m);
            }
            return result;
        }

        protected override List<int[]> JumpMoves(JObject moveDef, Piece piece, BoardState boardState)
        {
            var to = moveDef["to"];
            if (to is JObject toObj && toObj["mode"]?.Value<string>() == "region")
                return JumpRegionMoves(moveDef, piece, boardState);

            int px = piece.X, py = piece.Y;
            var toVec = ToVec(to);
            var block = ToVecList(moveDef["block"] as JArray);
            var land = moveDef["land"]?.Value<string>() ?? "any";
            var where = moveDef["where"] as JArray ?? new JArray();

            int nx = px + toVec[0], ny = py + toVec[1];
            if (!InBounds(nx, ny)) return new List<int[]>();

            foreach (var b in block)
                if (GetPieceAt(px + b[0], py + b[1], boardState) != null) return new List<int[]>();

            var target = GetPieceAt(nx, ny, boardState);
            if (land == "empty" && target != null) return new List<int[]>();
            if (land == "enemy")
            {
                if (target == null || target.Side == piece.Side) return new List<int[]>();
                if (!CanCaptureByRank(piece, target, boardState)) return new List<int[]>();
            }
            if (land == "any" && target != null)
            {
                if (target.Side == piece.Side) return new List<int[]>();
                if (!CanCaptureByRank(piece, target, boardState)) return new List<int[]>();
            }

            if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) return new List<int[]>();
            return new List<int[]> { new[] { nx, ny } };
        }

        protected override List<int[]> RayMoves(JObject moveDef, Piece piece, BoardState boardState)
        {
            int px = piece.X, py = piece.Y;
            var dir = ToVec(moveDef["dir"]);
            int maxDist = moveDef["max"]?.Value<int>() ?? -1;
            int screens = moveDef["screens"]?.Value<int>() ?? 0;
            var land = moveDef["land"]?.Value<string>() ?? "any";
            var where = moveDef["where"] as JArray ?? new JArray();
            var pc = moveDef["path_constraint"] as JObject;

            if (maxDist == -1) maxDist = Math.Max(Width, Height);
            var moves = new List<int[]>();

            // 跳河模式：中间格必须满足 path_constraint，终点为第一个不满足的格（中间水格不作为落点）
            if (pc != null)
            {
                var mustBe = pc["must_be"]?.Value<string>();
                bool noBlocker = pc["no_blocker"]?.Value<bool>() ?? false;
                int passed = 0;

                for (int step = 1; step <= maxDist; step++)
                {
                    int nx = px + dir[0] * step, ny = py + dir[1] * step;
                    if (!InBounds(nx, ny)) break;

                    var target = GetPieceAt(nx, ny, boardState);
                    bool isPathCell = true;
                    if (mustBe == "water" && !IsInWater(nx, ny)) isPathCell = false;
                    if (noBlocker && target != null) isPathCell = false;

                    if (isPathCell) { passed++; continue; }
                    if (passed == 0) break;   // 没跳过任何水格，不成跳河

                    if (target != null)
                    {
                        if (target.Side != piece.Side && CanCaptureByRank(piece, target, boardState))
                        {
                            if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) break;
                            moves.Add(new[] { nx, ny });
                        }
                        break;   // 终点有棋子，无论是否吃都结束
                    }
                    if (land == "any" || land == "empty")
                    {
                        if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) break;
                        moves.Add(new[] { nx, ny });
                    }
                    break;   // 落到陆地后结束（跳河只跳一次）
                }
                return moves;
            }

            // 普通射线模式
            int platformsFound = 0;
            for (int step = 1; step <= maxDist; step++)
            {
                int nx = px + dir[0] * step, ny = py + dir[1] * step;
                if (!InBounds(nx, ny)) break;
                var target = GetPieceAt(nx, ny, boardState);

                if (platformsFound < screens)
                {
                    if (target != null) platformsFound++;
                    else if (land == "empty")
                    {
                        if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) continue;
                        moves.Add(new[] { nx, ny });
                    }
                }
                else
                {
                    if (target != null)
                    {
                        if ((land == "any" || land == "enemy") && target.Side != piece.Side &&
                            CanCaptureByRank(piece, target, boardState))
                        {
                            if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) continue;
                            moves.Add(new[] { nx, ny });
                        }
                        break;
                    }
                    if (land == "any" || land == "empty")
                    {
                        if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) continue;
                        moves.Add(new[] { nx, ny });
                    }
                }
            }
            return moves;
        }

        // ══════════════════════════════════════════════════════════════
        // 条件表达式（动物棋区域原语）
        // ══════════════════════════════════════════════════════════════

        protected override bool EvalCondition(JObject cond, Piece piece, BoardState boardState, int dx, int dy)
        {
            if (cond.Count != 1) return true;
            string key = null;
            JToken value = null;
            foreach (var p in cond.Properties()) { key = p.Name; value = p.Value; }

            if (key == "not" || key == "and" || key == "or")
                return base.EvalCondition(cond, piece, boardState, dx, dy);

            (int x, int y) ResolvePos(JToken val, string fallback)
            {
                string posVar = fallback;
                if (val is JObject vo) posVar = vo["pos"]?.Value<string>() ?? fallback;
                else if (val != null && val.Type == JTokenType.String) posVar = val.Value<string>();
                return posVar == "$self" ? (piece.X, piece.Y) : (dx, dy);
            }

            switch (key)
            {
                case "in_water":
                {
                    var pos = ResolvePos(value, "$dest");
                    return IsInWater(pos.x, pos.y);
                }
                case "in_trap":
                {
                    var pos = ResolvePos(value, "$dest");
                    return IsInTrap(pos.x, pos.y, boardState);
                }
                case "in_enemy_trap":
                {
                    var pos = ResolvePos(value, "$dest");
                    return IsInEnemyTrap(pos.x, pos.y, piece.Side, boardState);
                }
                case "in_own_den":
                {
                    var pos = ResolvePos(value, "$dest");
                    return IsInOwnDen(pos.x, pos.y, piece.Side);
                }
                case "in_enemy_den":
                {
                    var pos = ResolvePos(value, "$dest");
                    return IsInEnemyDen(pos.x, pos.y, piece.Side);
                }
            }
            return base.EvalCondition(cond, piece, boardState, dx, dy);
        }

        // ══════════════════════════════════════════════════════════════
        // 动作枚举与执行
        // ══════════════════════════════════════════════════════════════

        public override List<GameAction> GetAllActions(BoardState boardState, string side)
        {
            var actions = new List<GameAction>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || p.Side != side || IsTerrainPiece(p)) continue;
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
                outcome.Ok = false;
                outcome.Message = "不支持的动作类型";
                return outcome;
            }

            var piece = boardState.GetPieceById(action.PieceId);
            if (piece == null || !piece.IsAlive)
            {
                outcome.Ok = false;
                outcome.Message = "棋子不存在";
                return outcome;
            }

            bool legal = false;
            foreach (var m in GetValidMoves(piece, boardState))
                if (m[0] == action.X && m[1] == action.Y) { legal = true; break; }
            if (!legal)
            {
                outcome.Ok = false;
                outcome.Message = "非法移动";
                return outcome;
            }

            var target = GetPieceAt(action.X, action.Y, boardState);
            piece.X = action.X;
            piece.Y = action.Y;
            if (target != null)
            {
                target.IsAlive = false;
                outcome.CapturedIds.Add(target.Id);
            }

            // 踩中敌方陷阱立即被吞噬（一次性陷阱）
            if (IsKilledByTrap(piece, boardState))
            {
                outcome.Message = "killed_by_trap";
                outcome.CapturedIds.Add(piece.Id);
            }

            outcome.Ok = true;
            return outcome;
        }

        // ══════════════════════════════════════════════════════════════
        // 胜负判定（enter_den > annihilation > stalemate）
        // ══════════════════════════════════════════════════════════════

        public override GameOutcome CheckOutcome(BoardState boardState)
        {
            bool enterDenEnabled = true;
            if (Rules["win_conditions"] is JObject wc && wc["enter_den"] is JObject ed)
            {
                var en = ed["enabled"];
                if (en != null && en.Type == JTokenType.Boolean) enterDenEnabled = en.Value<bool>();
            }

            // 1. enter_den：己方动物进入对方兽穴
            if (enterDenEnabled)
                foreach (var p in boardState.Pieces)
                {
                    if (!p.IsAlive || IsTerrainPiece(p)) continue;
                    if (IsInEnemyDen(p.X, p.Y, p.Side))
                        return new GameOutcome { Ended = true, Winner = p.Side, Condition = "enter_den" };
                }

            // 2. annihilation：一方无存活动物（地形棋子不计入）
            bool redAlive = false, blackAlive = false;
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || IsTerrainPiece(p)) continue;
                if (p.Side == "red") redAlive = true;
                else if (p.Side == "black") blackAlive = true;
            }
            if (!redAlive) return new GameOutcome { Ended = true, Winner = "black", Condition = "annihilation" };
            if (!blackAlive) return new GameOutcome { Ended = true, Winner = "red", Condition = "annihilation" };

            // 3. stalemate：当前方无任何合法移动
            var currentTurn = boardState.CurrentTurn;
            if (!string.IsNullOrEmpty(currentTurn))
            {
                bool hasMove = false;
                foreach (var p in boardState.Pieces)
                {
                    if (!p.IsAlive || IsTerrainPiece(p) || p.Side != currentTurn) continue;
                    if (GetValidMoves(p, boardState).Count > 0) { hasMove = true; break; }
                }
                if (!hasMove)
                    return new GameOutcome
                    {
                        Ended = true,
                        Winner = currentTurn == "red" ? "black" : "red",
                        Condition = "stalemate",
                    };
            }

            return GameOutcome.Ongoing;
        }
    }
}