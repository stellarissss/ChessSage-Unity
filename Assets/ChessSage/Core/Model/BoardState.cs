using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Model
{
    /// <summary>
    /// 对局状态。对应 legacy-web 的 board_state.json：
    /// {pieces, current_turn, move_history, game_status, mechanisms, board(可选)}。
    /// 未知字段会被保留在 <see cref="Extra"/> 中，保证 JSON Patch 往返不丢字段。
    /// </summary>
    public sealed class BoardState
    {
        public readonly List<Piece> Pieces = new List<Piece>();
        public string CurrentTurn = "red";
        public JArray MoveHistory = new JArray();
        public JObject GameStatus = new JObject();
        public JObject Mechanisms = new JObject();
        /// <summary>棋盘几何配置（board.json 内容），供规则引擎做区域/九宫/边界判断。</summary>
        public JObject Board;
        /// <summary>board_state 中未建模的其它字段。</summary>
        public JObject Extra = new JObject();

        public static readonly string[] MechanismKeys =
        {
            "skip_turns", "ai_control", "player_control", "random_moves", "extra_turns", "move_limits",
        };

        public static BoardState FromJson(JObject o)
        {
            var s = new BoardState
            {
                CurrentTurn = o["current_turn"]?.Value<string>() ?? "red",
                Board = o["board"] as JObject,
            };
            if (o["pieces"] is JArray arr)
                foreach (var t in arr)
                    if (t is JObject jo) s.Pieces.Add(Piece.FromJson(jo));
            if (o["move_history"] is JArray mh) s.MoveHistory = (JArray)mh.DeepClone();
            if (o["game_status"] is JObject gs) s.GameStatus = (JObject)gs.DeepClone();
            if (o["mechanisms"] is JObject mech) s.Mechanisms = (JObject)mech.DeepClone();
            EnsureMechanismKeys(s.Mechanisms);

            foreach (var prop in o.Properties())
            {
                if (prop.Name == "pieces" || prop.Name == "current_turn" || prop.Name == "move_history" ||
                    prop.Name == "game_status" || prop.Name == "mechanisms" || prop.Name == "board")
                    continue;
                s.Extra[prop.Name] = prop.Value.DeepClone();
            }
            return s;
        }

        public JObject ToJson()
        {
            var o = new JObject();
            foreach (var prop in Extra.Properties()) o[prop.Name] = prop.Value.DeepClone();
            var arr = new JArray();
            foreach (var p in Pieces) arr.Add(p.ToJson());
            o["pieces"] = arr;
            o["current_turn"] = CurrentTurn;
            o["move_history"] = MoveHistory.DeepClone();
            o["game_status"] = GameStatus.DeepClone();
            o["mechanisms"] = Mechanisms.DeepClone();
            if (Board != null) o["board"] = Board.DeepClone();
            return o;
        }

        public static void EnsureMechanismKeys(JObject mech)
        {
            if (mech == null) return;
            foreach (var key in MechanismKeys)
                if (mech[key] == null || mech[key].Type != JTokenType.Array)
                    mech[key] = new JArray();
        }

        public Piece GetPieceAt(int x, int y)
        {
            foreach (var p in Pieces)
                if (p.IsAlive && p.X == x && p.Y == y) return p;
            return null;
        }

        public Piece GetPieceById(string id)
        {
            foreach (var p in Pieces)
                if (p.Id == id) return p;
            return null;
        }

        public BoardState Clone()
        {
            var s = new BoardState
            {
                CurrentTurn = CurrentTurn,
                MoveHistory = (JArray)MoveHistory.DeepClone(),
                GameStatus = (JObject)GameStatus.DeepClone(),
                Mechanisms = (JObject)Mechanisms.DeepClone(),
                Board = Board == null ? null : (JObject)Board.DeepClone(),
                Extra = (JObject)Extra.DeepClone(),
            };
            foreach (var p in Pieces) s.Pieces.Add(p.Clone());
            return s;
        }

        public static string Opposite(string side) => side == "red" ? "black" : "red";
    }
}