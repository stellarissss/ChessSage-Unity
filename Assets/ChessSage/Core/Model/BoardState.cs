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

        /// <summary>
        /// 查找坐标处的存活棋子。
        ///
        /// 刻意保持<b>线性扫描</b>：曾尝试引入 id→Piece 字典索引以提速 AI 搜索，
        /// 但棋盘状态的可变字段（<c>Piece.X/Y/IsAlive</c>）散落在 6 个规则引擎的十余处赋值点，
        /// 任何一处漏掉失效标记都会导致<b>走法规则静默出错</b>——这种正确性风险远大于性能收益。
        /// 因此改为在调用方（<c>ChessAi</c>）做一次性查表，而非在数据层埋缓存。
        /// </summary>
        public Piece GetPieceAt(int x, int y)
        {
            var pieces = Pieces;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (p.IsAlive && p.X == x && p.Y == y) return p;
            }
            return null;
        }

        public Piece GetPieceById(string id)
        {
            var pieces = Pieces;
            for (int i = 0; i < pieces.Count; i++)
                if (pieces[i].Id == id) return pieces[i];
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

        /// <summary>
        /// <b>搜索专用浅克隆</b>：只拷贝 AI 搜索真正会读写的字段——
        /// 棋子列表（逐个拷贝，坐标会被改）与 <see cref="CurrentTurn"/>。
        ///
        /// JSON 字段（MoveHistory / GameStatus / Mechanisms / Board / Extra）<b>按引用共享</b>。
        /// 这是安全的，因为搜索路径（<c>ChessAi.Minimax</c> / <c>Evaluate</c> / 规则引擎的走法生成）
        /// 只读取这些结构，从不写入；真正修改它们的只有 <c>GameSession</c> 的成文走子路径，
        /// 而那条路径用的是 <see cref="Clone"/>。
        ///
        /// 性能意义重大：完整 <see cref="Clone"/> 要对 4 个 JObject 做 DeepClone，
        /// 实测在 9×10 象棋的 depth=3 搜索（约 6 万节点）中，单是这一项就占了 2.2 秒——
        /// 也就是 AI 每走一步棋卡顿 2 秒的根因。改用本方法后不再有 JSON 分配。
        /// </summary>
        internal BoardState CloneForSearch()
        {
            var s = new BoardState
            {
                CurrentTurn = CurrentTurn,
                // 共享引用（只读语义）
                MoveHistory = MoveHistory,
                GameStatus = GameStatus,
                Mechanisms = Mechanisms,
                Board = Board,
                Extra = Extra,
            };
            s.Pieces.Capacity = Pieces.Count;
            foreach (var p in Pieces) s.Pieces.Add(p.Clone());
            return s;
        }

        public static string Opposite(string side) => side == "red" ? "black" : "red";
    }
}