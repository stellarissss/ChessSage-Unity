using System.Collections.Generic;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules.Variants
{
    /// <summary>
    /// 黑白棋规则引擎 —— jump/ray 基类 + flip（夹吃翻转）原子。
    /// 逐语义移植自 legacy-web/heibaiqi/rule_engine.py：
    /// 合法落子（_flip_moves / get_valid_placements）、翻转（apply_flip_captures）、
    /// 胜负（is_game_over / get_winner）。
    /// </summary>
    public sealed class HeibaiqiRuleEngine : JumpRayRuleBase
    {
        static readonly (int dx, int dy)[] FlipDirs =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1),
        };

        public HeibaiqiRuleEngine(JObject board, JObject piecesBlack, JObject piecesWhite, JObject rules)
            : base(board, piecesBlack, piecesWhite, rules, "black", "white") { }

        public override bool IsPlacementGame => true;

        static int[] RotVec(int[] v, int dx, int dy)
            => new[] { v[0] * dx - v[1] * dy, v[0] * dy + v[1] * dx };

        // ── 对称展开：为 flip 原子补齐方向 ──
        protected override List<JObject> ExpandSymmetry(JObject moveDef)
        {
            if (moveDef["kind"]?.Value<string>() != "flip") return base.ExpandSymmetry(moveDef);

            var result = new List<JObject>();
            var sym = moveDef["sym"]?.Value<string>() ?? "none";
            var dir = ToVec(moveDef["dir"]);

            if (sym == "rotate4_mirror")
            {
                // 黑白棋标准：八方向（4 正方向 + 4 对角方向）
                foreach (var (dx, dy) in FlipDirs)
                {
                    var clone = (JObject)moveDef.DeepClone();
                    clone["dir"] = new JArray(dx, dy);
                    result.Add(clone);
                }
            }
            else if (sym == "rotate4")
            {
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (-1, 0), (0, -1) })
                {
                    var clone = (JObject)moveDef.DeepClone();
                    clone["dir"] = ToArr(RotVec(dir, dx, dy));
                    result.Add(clone);
                }
            }
            else if (sym == "mirror_x")
            {
                result.Add((JObject)moveDef.DeepClone());
                var clone = (JObject)moveDef.DeepClone();
                clone["dir"] = new JArray(-dir[0], dir[1]);
                result.Add(clone);
            }
            else
            {
                result.Add((JObject)moveDef.DeepClone());
            }
            return result;
        }

        // ── 执行：flip 单方向返回合法落子点 ──
        protected override List<int[]> ExecuteMoveDef(JObject moveDef, Piece piece, BoardState boardState)
            => moveDef["kind"]?.Value<string>() == "flip"
                ? FlipMovesSingle(moveDef, piece, boardState)
                : base.ExecuteMoveDef(moveDef, piece, boardState);

        // ── 合法落子：聚合该方所有棋子的 flip 落子点（去重保序）──
        public override List<int[]> GetValidPlacements(BoardState boardState, string side)
        {
            var result = new List<int[]>();
            var seen = new HashSet<string>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive || p.Side != side) continue;
                foreach (var pl in FlipMoves(p, boardState))
                {
                    var key = pl[0] + "," + pl[1];
                    if (seen.Add(key)) result.Add(pl);
                }
            }
            return result;
        }

        // 锚点己棋沿各 flip 方向的合法落子点
        List<int[]> FlipMoves(Piece piece, BoardState boardState)
        {
            var result = new List<int[]>();
            var defs = GetMoveDefinitions(piece.Type, piece.Side);
            if (defs.Count == 0) return result;

            var seen = new HashSet<string>();
            foreach (var md in defs)
            {
                if (md["kind"]?.Value<string>() != "flip") continue;
                foreach (var exp in ExpandSymmetry(md))
                    foreach (var pl in FlipMovesSingle(exp, piece, boardState))
                    {
                        var key = pl[0] + "," + pl[1];
                        if (seen.Add(key)) result.Add(pl);
                    }
            }
            return result;
        }

        // 单方向：从锚点先遇 ≥1 敌棋、再遇空格即为合法落子点
        List<int[]> FlipMovesSingle(JObject expDef, Piece piece, BoardState boardState)
        {
            int px = piece.X, py = piece.Y;
            var dir = ToVec(expDef["dir"]);
            int maxDist = expDef["max"]?.Value<int>() ?? 6;
            var where = expDef["where"] as JArray ?? new JArray();

            var placements = new List<int[]>();
            int enemyCount = 0;
            for (int step = 1; step <= maxDist; step++)
            {
                int nx = px + dir[0] * step, ny = py + dir[1] * step;
                if (!InBounds(nx, ny)) break;
                var target = GetPieceAt(nx, ny, boardState);
                if (target == null)
                {
                    // 空格：此前已遇 ≥1 敌棋则为合法落子点；where 以落子后新棋子视角求值
                    if (enemyCount >= 1 && (where.Count == 0 ||
                        EvalWhere(where, PlacingProbe(piece, nx, ny), boardState, nx, ny)))
                        placements.Add(new[] { nx, ny });
                    break;
                }
                if (target.Side == piece.Side) break;
                enemyCount++;
            }
            return placements;
        }

        static Piece PlacingProbe(Piece anchor, int x, int y)
            => new Piece("__placing__", anchor.Type, anchor.Name, anchor.Side, x, y);

        // ── 翻转：落子后沿各方向夹吃中间的敌棋，返回被翻转棋子 id ──
        public List<string> ApplyFlipCaptures(int px, int py, string side, BoardState boardState)
        {
            var flipped = new List<string>();
            foreach (var (dx, dy, maxDist) in GetFlipDirections(side))
            {
                var enemies = new List<Piece>();
                for (int step = 1; step <= maxDist; step++)
                {
                    int nx = px + dx * step, ny = py + dy * step;
                    if (!InBounds(nx, ny)) break;
                    var target = GetPieceAt(nx, ny, boardState);
                    if (target == null) break;
                    if (target.Side == side)
                    {
                        // 遇到己方棋子：翻转中间敌棋
                        if (enemies.Count > 0)
                            foreach (var e in enemies) { e.Side = side; flipped.Add(e.Id); }
                        break;
                    }
                    enemies.Add(target);
                }
            }
            return flipped;
        }

        // 聚合该方所有 flip 方向（八方向，按首次出现去重保序）
        List<(int dx, int dy, int max)> GetFlipDirections(string side)
        {
            var directions = new List<(int dx, int dy, int max)>();
            var seen = new HashSet<string>();

            void Collect(JObject config)
            {
                if (config["moves"] is not JArray moves) return;
                foreach (var m in moves)
                {
                    if (m is not JObject mo || mo["kind"]?.Value<string>() != "flip") continue;
                    foreach (var exp in ExpandSymmetry(mo))
                    {
                        var d = ToVec(exp["dir"]);
                        var key = d[0] + "," + d[1];
                        if (seen.Add(key)) directions.Add((d[0], d[1], exp["max"]?.Value<int>() ?? 6));
                    }
                }
            }

            if (PiecesBySide.TryGetValue(side, out var sidePieces))
                foreach (var kv in sidePieces)
                    if (kv.Value is JObject c) Collect(c);
            if (CustomPiecesBySide.TryGetValue(side, out var custom))
                foreach (var cp in custom)
                    if (cp is JObject c) Collect(c);
            return directions;
        }

        // ── 落子：新增己方 disc 并翻转夹吃的敌子 ──
        public override ActionOutcome ApplyAction(BoardState boardState, GameAction action, string side)
        {
            var outcome = new ActionOutcome();
            var valid = GetValidPlacements(boardState, side);
            bool isLegal = false;
            foreach (var v in valid) if (v[0] == action.X && v[1] == action.Y) { isLegal = true; break; }
            if (!isLegal) { outcome.Ok = false; outcome.Message = "非法落子点"; return outcome; }

            boardState.Pieces.Add(new Piece(NextDiscId(boardState, side), "disc",
                side == "black" ? "黑棋" : "白棋", side, action.X, action.Y));
            outcome.Ok = true;
            outcome.FlippedIds.AddRange(ApplyFlipCaptures(action.X, action.Y, side, boardState));
            return outcome;
        }

        // 新 disc id：当前方 {side}_disc_N 的最大编号 + 1
        static string NextDiscId(BoardState boardState, string side)
        {
            int max = 0;
            var prefix = side + "_disc_";
            foreach (var p in boardState.Pieces)
            {
                if (p.Side != side || p.Id == null || !p.Id.StartsWith(prefix)) continue;
                if (int.TryParse(p.Id.Substring(prefix.Length), out var n) && n > max) max = n;
            }
            return prefix + (max + 1);
        }

        public override List<GameAction> GetAllActions(BoardState boardState, string side)
        {
            var actions = new List<GameAction>();
            foreach (var p in GetValidPlacements(boardState, side))
                actions.Add(new GameAction("place", null, p[0], p[1]));
            return actions;
        }

        // ── 胜负：棋盘满 / 双方无路可走 → 结束；多子者胜，平局 Winner=null ──
        public override GameOutcome CheckOutcome(BoardState boardState)
        {
            int total = 0;
            var sides = new List<string>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive) continue;
                total++;
                if (!sides.Contains(p.Side)) sides.Add(p.Side);
            }

            string reason = null;
            if (total >= Width * Height) reason = "board_full";
            else if (sides.Count == 0) reason = "no_valid_moves_both";
            else
            {
                bool anyMove = false;
                foreach (var s in sides)
                    if (GetValidPlacements(boardState, s).Count > 0) { anyMove = true; break; }
                if (!anyMove) reason = "no_valid_moves_both";
            }

            if (reason == null) return GameOutcome.Ongoing;

            var outcome = new GameOutcome { Ended = true, Condition = reason, Winner = WinnerOf(boardState) };
            outcome.Draw = outcome.Winner == null;
            return outcome;
        }

        // 棋子数多者胜，并列则平局（null）
        static string WinnerOf(BoardState boardState)
        {
            var counts = new Dictionary<string, int>();
            foreach (var p in boardState.Pieces)
            {
                if (!p.IsAlive) continue;
                counts.TryGetValue(p.Side, out var c);
                counts[p.Side] = c + 1;
            }
            if (counts.Count == 0) return null;

            int max = 0;
            foreach (var v in counts.Values) if (v > max) max = v;

            string winner = null;
            int winners = 0;
            foreach (var kv in counts) if (kv.Value == max) { winner = kv.Key; winners++; }
            return winners == 1 ? winner : null;
        }
    }
}