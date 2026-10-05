using System.Collections.Generic;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules
{
    /// <summary>一次对局动作：移动（move）或落子（place）。</summary>
    public sealed class GameAction
    {
        public string Kind = "move";   // move | place | pass
        public string PieceId;         // move 时为棋子 id；place 时为 null
        public int X, Y;

        public GameAction() { }
        public GameAction(string kind, string pieceId, int x, int y)
        {
            Kind = kind; PieceId = pieceId; X = x; Y = y;
        }

        public JObject ToJson() => new JObject
        {
            ["kind"] = Kind,
            ["piece_id"] = PieceId,
            ["to"] = new JArray(X, Y),
        };

        public static GameAction FromToken(JToken t)
        {
            if (t is not JObject o) return null;
            var to = o["to"] as JArray;
            return new GameAction(
                o["kind"]?.Value<string>() ?? "move",
                o["piece_id"]?.Value<string>(),
                to != null && to.Count >= 2 ? to[0].Value<int>() : 0,
                to != null && to.Count >= 2 ? to[1].Value<int>() : 0);
        }
    }

    /// <summary>动作执行结果：被吃子、被翻转子等副作用。</summary>
    public sealed class ActionOutcome
    {
        public bool Ok;
        public string Message = "";
        public readonly List<string> CapturedIds = new List<string>();
        public readonly List<string> FlippedIds = new List<string>();
        public bool Passed;
    }

    /// <summary>胜负判定结果。</summary>
    public sealed class GameOutcome
    {
        public bool Ended;
        public string Winner;      // 获胜阵营（red/black/white），平局为 null
        public string Condition;   // general_captured / checkmate / five_in_a_row / ...
        public bool Draw;

        public static readonly GameOutcome Ongoing = new GameOutcome();
    }

    /// <summary>
    /// 棋类规则引擎基类 —— 承载所有棋类共享的配置访问、修饰器（can_capture / eatable /
    /// invulnerable）与棋盘几何。各棋类差异由子类实现。
    /// </summary>
    public abstract class VariantRuleBase
    {
        protected readonly JObject BoardConfig;
        protected readonly JObject Rules;
        protected readonly Dictionary<string, JObject> PiecesBySide = new Dictionary<string, JObject>();
        protected readonly Dictionary<string, JArray> CustomPiecesBySide = new Dictionary<string, JArray>();
        protected readonly HashSet<string> KingTypes = new HashSet<string>();

        /// <summary>参与对局的阵营顺序（如 ["red","black"] 或 ["black","white"]）。</summary>
        public readonly List<string> Sides = new List<string>();

        protected VariantRuleBase(JObject board, JObject piecesA, JObject piecesB, JObject rules,
            string sideA = null, string sideB = null)
        {
            BoardConfig = board ?? new JObject();
            Rules = rules ?? new JObject();

            sideA = sideA ?? piecesA?["side"]?.Value<string>() ?? "red";
            sideB = sideB ?? piecesB?["side"]?.Value<string>() ?? "black";
            Sides.Add(sideA);
            if (sideB != sideA) Sides.Add(sideB);

            PiecesBySide[sideA] = piecesA?["pieces"] as JObject ?? new JObject();
            PiecesBySide[sideB] = piecesB?["pieces"] as JObject ?? new JObject();
            CustomPiecesBySide[sideA] = piecesA?["custom_pieces"] as JArray ?? new JArray();
            CustomPiecesBySide[sideB] = piecesB?["custom_pieces"] as JArray ?? new JArray();

            foreach (var kv in CustomPiecesBySide)
                foreach (var cp in kv.Value)
                    if (cp is JObject c && c["is_king"]?.Value<bool>() == true && c["type"]?.Value<string>() is string t)
                        KingTypes.Add(t);
        }

        // ── 棋类元信息 ──
        public virtual string VariantId => "xiangqi";
        public virtual bool IsPlacementGame => false;
        public virtual string DefaultPlayerSide => Sides.Count > 0 ? Sides[0] : "red";
        public string Opposite(string side) => side == Sides[0] ? (Sides.Count > 1 ? Sides[1] : side) : Sides[0];

        /// <summary>该棋类是否使用「将军/将死」规则（象棋）。</summary>
        public virtual bool SupportsCheckRules => false;

        /// <summary>将帅是否被吃（返回胜方，否则 null）。非象棋类返回 null。</summary>
        public virtual string IsGeneralCaptured(BoardState boardState) => null;

        /// <summary>某方是否正被将军。</summary>
        public virtual bool IsInCheck(string side, BoardState boardState) => false;

        /// <summary>某方是否已被将死。</summary>
        public virtual bool IsCheckmate(string side, BoardState boardState) => false;

        // ── 几何 ──
        protected JObject Geometry => BoardConfig["geometry"] as JObject ?? new JObject();
        public int Width => Geometry["width"]?.Value<int>() ?? 9;
        public int Height => Geometry["height"]?.Value<int>() ?? 10;
        public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

        // ── 棋子配置 ──
        public JObject GetPieceConfig(string pieceType, string side)
        {
            if (PiecesBySide.TryGetValue(side, out var sidePieces) &&
                sidePieces.TryGetValue(pieceType, out var baseConfig) && baseConfig is JObject bo)
                return bo;

            if (CustomPiecesBySide.TryGetValue(side, out var custom))
                foreach (var cp in custom)
                    if (cp is JObject c && c["type"]?.Value<string>() == pieceType) return c;
            return null;
        }

        protected List<JObject> GetMoveDefinitions(string pieceType, string side)
        {
            var list = new List<JObject>();
            var baseConfig = GetPieceConfig(pieceType, side);
            if (baseConfig?["moves"] is JArray moves)
                foreach (var m in moves)
                    if (m is JObject mo) list.Add(mo);
            return list;
        }

        // ── 修饰器 ──
        protected JObject GetRulesTypeModifiers()
        {
            var tm = Rules["type_modifiers"] as JObject;
            if (tm == null) tm = (Rules["modifiers"] as JObject)?["type_modifiers"] as JObject;
            return tm ?? new JObject();
        }

        public JObject ResolveModifiers(Piece piece)
        {
            var outObj = new JObject();
            var ptype = piece.Type;
            var side = piece.Side;

            var tm = GetRulesTypeModifiers();
            if (tm[ptype] is JObject global) outObj.Merge(global, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            if (tm[side] is JObject sideMap && sideMap[ptype] is JObject sideType)
                outObj.Merge(sideType, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });

            var baseConfig = GetPieceConfig(ptype, side);
            if (baseConfig != null)
            {
                if (baseConfig["modifiers"] is JObject cfgMods) outObj.Merge(cfgMods, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                foreach (var attr in new[] { "can_capture", "eatable", "invulnerable" })
                    if (baseConfig.TryGetValue(attr, out var v)) outObj[attr] = v.DeepClone();
            }

            foreach (var attr in new[] { "can_capture", "eatable", "invulnerable" })
            {
                if (piece.Extra.TryGetValue(attr, out var v) && v.Type != JTokenType.Null) outObj[attr] = v.DeepClone();
                else if (piece.CustomProperties.TryGetValue(attr, out var cv)) outObj[attr] = cv.DeepClone();
            }
            return outObj;
        }

        public JToken GetEffectiveModifier(Piece piece, string attr, JToken defaultValue = null)
            => ResolveModifiers(piece)[attr] ?? defaultValue;

        public bool IsTargetEatable(Piece target)
        {
            var eat = GetEffectiveModifier(target, "eatable", new JValue(true));
            if (eat.Type == JTokenType.Boolean && eat.Value<bool>() == false) return false;
            var inv = GetEffectiveModifier(target, "invulnerable", new JValue(false));
            if (inv.Type == JTokenType.Boolean && inv.Value<bool>()) return false;
            return true;
        }

        public virtual bool IsInvulnerable(Piece target) => !IsTargetEatable(target);

        public bool CanCapturePiece(Piece attacker, Piece target)
        {
            var cc = GetEffectiveModifier(attacker, "can_capture", new JValue(true));
            if (cc.Type == JTokenType.Boolean && cc.Value<bool>() == false) return false;
            return IsTargetEatable(target);
        }

        /// <summary>过滤：攻击方不能吃 或 目标不可被吃 的落点一律剔除。</summary>
        protected List<int[]> RestrictCaptures(Piece piece, BoardState boardState, List<int[]> moves)
        {
            var cc = GetEffectiveModifier(piece, "can_capture", new JValue(true));
            bool attackerCanCapture = !(cc.Type == JTokenType.Boolean && cc.Value<bool>() == false);

            var result = new List<int[]>();
            foreach (var m in moves)
            {
                var target = GetPieceAt(m[0], m[1], boardState);
                if (target != null && target.Side != piece.Side)
                {
                    if (!attackerCanCapture) continue;
                    if (!IsTargetEatable(target)) continue;
                }
                result.Add(m);
            }
            return result;
        }

        // ── 区域 ──
        public virtual bool IsInRegion(int x, int y, string regionRef, string side)
        {
            if (regionRef == "$full_board") return true;
            if (regionRef == "$palace") return InPalace(x, y, side);

            var regions = Geometry["regions"] as JObject;
            var region = regions?[regionRef] as JObject;
            if (region == null) return true;

            if (region["x_range"] is JArray xr && xr.Count >= 2)
                if (!(x >= xr[0].Value<int>() && x <= xr[1].Value<int>())) return false;
            if (region["y_range"] is JArray yr && yr.Count >= 2)
                if (!(y >= yr[0].Value<int>() && y <= yr[1].Value<int>())) return false;
            return true;
        }

        protected virtual bool InPalace(int x, int y, string side)
        {
            var palace = Geometry["palace"] as JObject;
            var sidePalace = palace?[side] as JObject;
            if (sidePalace != null && sidePalace.Count > 0)
            {
                var tl = sidePalace["top_left"] as JArray;
                var br = sidePalace["bottom_right"] as JArray;
                int tlx = tl?[0]?.Value<int>() ?? 0, tly = tl?[1]?.Value<int>() ?? 0;
                int brx = br?[0]?.Value<int>() ?? 0, bry = br?[1]?.Value<int>() ?? 0;
                return tlx <= x && x <= brx && tly <= y && y <= bry;
            }
            if (x < 3 || x > 5) return false;
            return side == "red" ? (y >= 7 && y <= 9) : (y >= 0 && y <= 2);
        }

        protected int RiverLine => Geometry["river_line"]?.Value<int>() ?? 5;
        protected bool CrossedRiver(int y, string side) => side == "red" ? y < RiverLine : y >= RiverLine;
        protected bool IsOwnSide(int y, string side) => side == "red" ? y >= RiverLine : y < RiverLine;

        protected static string ResolveVar(string var, Piece piece)
            => var == "$palace" ? "$palace" : var == "$side" ? piece.Side : var;

        public virtual Piece GetPieceAt(int x, int y, BoardState boardState) => boardState.GetPieceAt(x, y);

        public Piece GetPieceById(BoardState state, string id) => state.GetPieceById(id);

        // ══════════════════════════════════════════════════════════════
        // 子类必须实现
        // ══════════════════════════════════════════════════════════════

        /// <summary>棋子（piece-based 游戏）的合法落点。</summary>
        public abstract List<int[]> GetValidMoves(Piece piece, BoardState boardState);

        /// <summary>落子类游戏（围棋/黑白棋/五子棋）的合法落点；非落子类返回空。</summary>
        public virtual List<int[]> GetValidPlacements(BoardState boardState, string side) => new List<int[]>();

        /// <summary>枚举某方全部合法动作（AI 使用）。</summary>
        public abstract List<GameAction> GetAllActions(BoardState boardState, string side);

        /// <summary>执行动作并改写 boardState；返回副作用。</summary>
        public abstract ActionOutcome ApplyAction(BoardState boardState, GameAction action, string side);

        /// <summary>动作执行后的胜负判定。</summary>
        public abstract GameOutcome CheckOutcome(BoardState boardState);
    }
}