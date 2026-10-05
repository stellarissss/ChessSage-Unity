using System;
using System.Collections.Generic;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules
{
    /// <summary>
    /// jump + ray 双原子移动体系 + 条件表达式引擎。
    /// 为象棋/五子棋/动物棋/黑白棋（落子）等共享；逐语义移植自 legacy-web/*/rule_engine.py。
    /// </summary>
    public abstract class JumpRayRuleBase : VariantRuleBase
    {
        protected JumpRayRuleBase(JObject board, JObject piecesA, JObject piecesB, JObject rules,
            string sideA = null, string sideB = null)
            : base(board, piecesA, piecesB, rules, sideA, sideB) { }

        // ══════════════════════════════════════════════════════════════
        // 合法移动（jump/ray 通用）
        // ══════════════════════════════════════════════════════════════

        public override List<int[]> GetValidMoves(Piece piece, BoardState boardState)
        {
            var result = new List<int[]>();
            if (string.IsNullOrEmpty(piece.Type)) return result;

            var moveDefs = GetMoveDefinitions(piece.Type, piece.Side);
            if (moveDefs.Count == 0) return result;

            var allMoves = new List<int[]>();
            foreach (var moveDef in moveDefs)
                foreach (var expDef in ExpandSymmetry(moveDef))
                    allMoves.AddRange(ExecuteMoveDef(expDef, piece, boardState));

            allMoves = RestrictCaptures(piece, boardState, allMoves);

            // 去重并保持首次出现顺序（等价于 Python dict-from-generator 的插入序）
            var seen = new HashSet<string>();
            foreach (var m in allMoves)
            {
                var key = m[0] + "," + m[1];
                if (seen.Add(key)) result.Add(m);
            }
            return result;
        }

        // ══════════════════════════════════════════════════════════════
        // 对称展开
        // ══════════════════════════════════════════════════════════════

        static readonly (int dx, int dy)[] Rot4 = { (1, 0), (0, 1), (-1, 0), (0, -1) };

        static int[] Rot(int[] v, int dx, int dy) => new[] { v[0] * dx - v[1] * dy, v[0] * dy + v[1] * dx };

        protected virtual List<JObject> ExpandSymmetry(JObject moveDef)
        {
            var result = new List<JObject>();
            var sym = moveDef["sym"]?.Value<string>() ?? "none";
            var kind = moveDef["kind"]?.Value<string>();

            if (kind == "jump")
            {
                var to = moveDef["to"];
                if (to is JObject toObj && toObj["mode"]?.Value<string>() == "region")
                {
                    result.Add((JObject)moveDef.DeepClone());
                    return result;
                }

                var toArr = ToVec(to);
                var block = ToVecList(moveDef["block"] as JArray);

                if (sym == "rotate4" || sym == "rotate4_mirror")
                {
                    foreach (var (dx, dy) in Rot4)
                    {
                        var clone = (JObject)moveDef.DeepClone();
                        clone["to"] = ToArr(Rot(toArr, dx, dy));
                        clone["block"] = ToArrList(RotList(block, dx, dy));
                        result.Add(clone);
                        if (sym == "rotate4_mirror")
                        {
                            var mirror = (JObject)moveDef.DeepClone();
                            var rt = Rot(toArr, dx, dy);
                            var rb = RotList(block, dx, dy);
                            mirror["to"] = new JArray(rt[0], -rt[1]);
                            mirror["block"] = ToArrList(MirrorY(rb));
                            result.Add(mirror);
                        }
                    }
                }
                else if (sym == "mirror_x")
                {
                    result.Add((JObject)moveDef.DeepClone());
                    var clone = (JObject)moveDef.DeepClone();
                    clone["to"] = new JArray(-toArr[0], toArr[1]);
                    clone["block"] = ToArrList(MirrorX(block));
                    result.Add(clone);
                }
                else
                {
                    result.Add((JObject)moveDef.DeepClone());
                }
            }
            else if (kind == "ray")
            {
                var dir = ToVec(moveDef["dir"]);
                if (sym == "rotate4" || sym == "rotate4_mirror")
                {
                    foreach (var (dx, dy) in Rot4)
                    {
                        var clone = (JObject)moveDef.DeepClone();
                        clone["dir"] = ToArr(Rot(dir, dx, dy));
                        result.Add(clone);
                        if (sym == "rotate4_mirror")
                        {
                            var mirror = (JObject)moveDef.DeepClone();
                            var rd = Rot(dir, dx, dy);
                            mirror["dir"] = new JArray(rd[0], -rd[1]);
                            result.Add(mirror);
                        }
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
            }
            return result;
        }

        protected static int[] ToVec(JToken t)
        {
            var a = t as JArray;
            return a != null && a.Count >= 2 ? new[] { a[0].Value<int>(), a[1].Value<int>() } : new[] { 0, 0 };
        }

        protected static List<int[]> ToVecList(JArray a)
        {
            var list = new List<int[]>();
            if (a != null)
                foreach (var t in a)
                    if (t is JArray ta && ta.Count >= 2) list.Add(new[] { ta[0].Value<int>(), ta[1].Value<int>() });
            return list;
        }

        protected static JArray ToArr(int[] v) => new JArray(v[0], v[1]);

        protected static JArray ToArrList(List<int[]> list)
        {
            var a = new JArray();
            foreach (var v in list) a.Add(ToArr(v));
            return a;
        }

        static List<int[]> RotList(List<int[]> list, int dx, int dy)
        {
            var r = new List<int[]>(list.Count);
            foreach (var b in list) r.Add(Rot(b, dx, dy));
            return r;
        }

        static List<int[]> MirrorX(List<int[]> list)
        {
            var r = new List<int[]>(list.Count);
            foreach (var b in list) r.Add(new[] { -b[0], b[1] });
            return r;
        }

        static List<int[]> MirrorY(List<int[]> list)
        {
            var r = new List<int[]>(list.Count);
            foreach (var b in list) r.Add(new[] { b[0], -b[1] });
            return r;
        }

        // ══════════════════════════════════════════════════════════════
        // 移动定义执行
        // ══════════════════════════════════════════════════════════════

        protected virtual List<int[]> ExecuteMoveDef(JObject moveDef, Piece piece, BoardState boardState)
        {
            var kind = moveDef["kind"]?.Value<string>();
            if (kind == "jump") return JumpMoves(moveDef, piece, boardState);
            if (kind == "ray") return RayMoves(moveDef, piece, boardState);
            return new List<int[]>();
        }

        protected virtual List<int[]> JumpMoves(JObject moveDef, Piece piece, BoardState boardState)
        {
            var to = moveDef["to"];
            if (to is JObject toObj && toObj["mode"]?.Value<string>() == "region")
                return JumpRegionMoves(moveDef, piece, boardState);

            int px = piece.X, py = piece.Y;
            int[] toVec = ToVec(to);
            if (to?.Type == JTokenType.String && to.Value<string>() == "$forward")
                toVec = new[] { 0, piece.Side == "red" ? -1 : 1 };

            var block = ToVecList(moveDef["block"] as JArray);
            var land = moveDef["land"]?.Value<string>() ?? "any";
            var where = moveDef["where"] as JArray ?? new JArray();

            int nx = px + toVec[0], ny = py + toVec[1];
            if (!InBounds(nx, ny)) return new List<int[]>();

            foreach (var b in block)
                if (GetPieceAt(px + b[0], py + b[1], boardState) != null) return new List<int[]>();

            var target = GetPieceAt(nx, ny, boardState);
            if (land == "empty" && target != null) return new List<int[]>();
            if (land == "enemy" && (target == null || target.Side == piece.Side)) return new List<int[]>();
            if (land == "any" && target != null && target.Side == piece.Side) return new List<int[]>();

            if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) return new List<int[]>();
            return new List<int[]> { new[] { nx, ny } };
        }

        protected virtual List<int[]> JumpRegionMoves(JObject moveDef, Piece piece, BoardState boardState)
        {
            int px = piece.X, py = piece.Y;
            var toObj = (JObject)moveDef["to"];
            var regionRef = toObj["region"]?.Value<string>();
            var land = moveDef["land"]?.Value<string>() ?? "any";
            var where = moveDef["where"] as JArray ?? new JArray();

            var moves = new List<int[]>();
            for (int nx = 0; nx < Width; nx++)
                for (int ny = 0; ny < Height; ny++)
                {
                    if (nx == px && ny == py) continue;
                    if (!IsInRegion(nx, ny, regionRef, piece.Side)) continue;
                    var target = GetPieceAt(nx, ny, boardState);
                    if (land == "empty" && target != null) continue;
                    if (land == "enemy" && (target == null || target.Side == piece.Side)) continue;
                    if (land == "any" && target != null && target.Side == piece.Side) continue;
                    if (where.Count > 0 && !EvalWhere(where, piece, boardState, nx, ny)) continue;
                    moves.Add(new[] { nx, ny });
                }
            return moves;
        }

        protected virtual List<int[]> RayMoves(JObject moveDef, Piece piece, BoardState boardState)
        {
            int px = piece.X, py = piece.Y;
            var dir = ToVec(moveDef["dir"]);
            int maxDist = moveDef["max"]?.Value<int>() ?? -1;
            int screens = moveDef["screens"]?.Value<int>() ?? 0;
            var land = moveDef["land"]?.Value<string>() ?? "any";
            var where = moveDef["where"] as JArray ?? new JArray();

            if (maxDist == -1) maxDist = Math.Max(Width, Height);

            var moves = new List<int[]>();
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
                        if ((land == "any" || land == "enemy") && target.Side != piece.Side)
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
        // 条件表达式
        // ══════════════════════════════════════════════════════════════

        protected bool EvalWhere(JArray conditions, Piece piece, BoardState boardState, int dx, int dy)
        {
            foreach (var c in conditions)
                if (c is JObject co && !EvalCondition(co, piece, boardState, dx, dy)) return false;
            return true;
        }

        protected virtual bool EvalCondition(JObject cond, Piece piece, BoardState boardState, int dx, int dy)
        {
            if (cond.Count != 1) return true;
            string key = null;
            JToken value = null;
            foreach (var p in cond.Properties()) { key = p.Name; value = p.Value; }

            if (key == "not") return !EvalCondition((JObject)value, piece, boardState, dx, dy);
            if (key == "and")
            {
                foreach (var sub in (JArray)value) if (!EvalCondition((JObject)sub, piece, boardState, dx, dy)) return false;
                return true;
            }
            if (key == "or")
            {
                foreach (var sub in (JArray)value) if (EvalCondition((JObject)sub, piece, boardState, dx, dy)) return true;
                return false;
            }

            (int x, int y) ResolvePos(JToken val, string fallback)
            {
                string posVar = fallback;
                if (val is JObject vo) posVar = vo["pos"]?.Value<string>() ?? fallback;
                else if (val.Type == JTokenType.String) posVar = val.Value<string>();
                return posVar == "$self" ? (piece.X, piece.Y) : (dx, dy);
            }

            if (key == "in_region")
            {
                string regionName = value is JObject vo ? vo["region"]?.Value<string>() ?? "$palace" : value.Value<string>();
                var pos = ResolvePos(value, "$dest");
                regionName = ResolveVar(regionName, piece);
                return IsInRegion(pos.x, pos.y, regionName, piece.Side);
            }
            if (key == "crossed_river")
            {
                var pos = ResolvePos(value, "$dest");
                return CrossedRiver(pos.y, piece.Side);
            }
            if (key == "same_side")
            {
                var pos = ResolvePos(value, "$dest");
                return IsOwnSide(pos.y, piece.Side);
            }
            if (key == "at_row")
            {
                int row;
                (int x, int y) pos;
                if (value is JObject vo) { pos = ResolvePos(value, "$dest"); row = vo["row"]?.Value<int>() ?? -1; }
                else { pos = (dx, dy); row = value.Value<int>(); }
                return pos.y == row;
            }
            if (key == "at_col")
            {
                int col;
                (int x, int y) pos;
                if (value is JObject vo) { pos = ResolvePos(value, "$dest"); col = vo["col"]?.Value<int>() ?? -1; }
                else { pos = (dx, dy); col = value.Value<int>(); }
                return pos.x == col;
            }
            return true;
        }
    }
}