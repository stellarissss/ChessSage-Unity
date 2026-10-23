using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ChessSage.Core.Json;
using ChessSage.Core.Orchestration;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Agent
{
    /// <summary>
    /// 紧凑局势摘要（E1-S6；从旧编排器的 GetBoardSummary 移植的纯函数，零引擎依赖）。
    ///
    /// 【与旧实现的差异（有意）】旧摘要只有「红方N子, 黑方N子」计数；本移植版按工程侧 §5.6 扩充为
    /// 「尺寸 + 回合 + 双方残子按类型聚合 + 机制 + 近 N 手」——不再内联整份 board_state，
    /// 读数走工具 1/2，这是 token 大幅下降的主要来源。
    ///
    /// 【确定性（A4）】同快照同字节：聚合按类型名字典序排序、侧序固定 red/black、
    /// 无时间戳/无 HashSet 枚举序依赖——前缀缓存与黄金回放的前提（§13.2 R1-R7）。
    ///
    /// 【PieceTypeMapping 纠正表】旧编排器的 PieceTypeMapping 逐字平移
    /// （pawn→soldier 等 6 项；迁移表点名的坑）：生成 name 缺省棋子的规范名时使用，
    /// 防止把国际象棋类型名泄漏进中文象棋语境。
    /// </summary>
    public static class BoardSummarizer
    {
        /// <summary>侧序固定（旧 <c>BoardSummaryOrder</c> 逐字）——字节级稳定的根基。</summary>
        public static readonly string[] SideOrder = { "red", "black" };

        /// <summary>侧标签（旧 <c>SideLabels</c> 逐字）。</summary>
        public static readonly Dictionary<string, string> SideLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["red"] = "红方",
            ["black"] = "黑方",
        };

        /// <summary>
        /// 棋子类型纠正表（旧编排器 PieceTypeMapping 逐字）：
        /// 国际象棋类型名 → 中国象棋规范类型名。
        /// </summary>
        public static readonly Dictionary<string, string> PieceTypeMapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pawn"] = "soldier",
            ["rook"] = "chariot",
            ["knight"] = "horse",
            ["bishop"] = "elephant",
            ["queen"] = "advisor",
            ["king"] = "general",
        };

        /// <summary>机制键展示序（固定——不枚举 JObject 属性序）。</summary>
        static readonly string[] MechanismKeys =
        {
            "skip_turns", "ai_control", "player_control", "random_moves", "extra_turns", "move_limits",
        };

        /// <summary>
        /// 生成紧凑局势摘要。boardConfig 仅用于读取尺寸（width/height）；缺省不输出尺寸段。
        /// </summary>
        public static string Summarize(JObject boardState, JObject boardConfig = null, int recentMoves = 5)
        {
            if (boardState == null) boardState = new JObject();
            var lines = new List<string>();

            // 1. 尺寸 + 回合 + 状态
            var head = new StringBuilder();
            if (boardConfig != null)
            {
                var w = BoardGeometry.Width(boardConfig);
                var h = BoardGeometry.Height(boardConfig);
                if (w > 0 && h > 0)
                    head.Append($"棋盘 {w}x{h}；");
            }
            var turn = JsonHelpers.GetString(boardState, "current_turn", "red");
            head.Append($"当前轮到 {SideLabel(turn)}");
            var status = boardState["game_status"] as JObject;
            if (status != null)
            {
                var state = JsonHelpers.GetString(status, "state");
                if (!string.IsNullOrEmpty(state))
                {
                    head.Append("；对局状态 ");
                    head.Append(state);
                    var winner = JsonHelpers.GetString(status, "winner");
                    if (!string.IsNullOrEmpty(winner))
                        head.Append($"（胜者 {SideLabel(winner)}）");
                }
            }
            lines.Add($"- {head}");

            // 2. 双方残子按类型聚合（is_alive 才计；类型名字典序——与数组序无关）
            var pieces = boardState["pieces"] as JArray;
            foreach (var side in SideOrder)
            {
                var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                if (pieces != null)
                    foreach (var token in pieces)
                    {
                        if (!(token is JObject p)) continue;
                        if (!JsonHelpers.GetBool(p, "is_alive", true)) continue;
                        if (JsonHelpers.GetString(p, "side") != side) continue;
                        var type = JsonHelpers.GetString(p, "type");
                        if (string.IsNullOrEmpty(type)) continue;
                        counts.TryGetValue(type, out var c);
                        counts[type] = c + 1;
                    }
                var total = 0;
                var parts = new List<string>();
                foreach (var kv in counts)
                {
                    total += kv.Value;
                    parts.Add($"{kv.Key}×{kv.Value.ToString(CultureInfo.InvariantCulture)}");
                }
                lines.Add(parts.Count > 0
                    ? $"- {SideLabel(side)} {total.ToString(CultureInfo.InvariantCulture)} 子：{string.Join(", ", parts.ToArray())}"
                    : $"- {SideLabel(side)} 0 子");
            }

            // 3. 机制（只列非空项；固定键序）
            var mech = boardState["mechanisms"] as JObject;
            var mechParts = new List<string>();
            if (mech != null)
                foreach (var key in MechanismKeys)
                {
                    var list = mech[key] as JArray;
                    if (list == null || list.Count == 0) continue;
                    var descs = new List<string>();
                    foreach (var t in list)
                    {
                        if (!(t is JObject m)) continue;
                        var side = JsonHelpers.GetString(m, "side");
                        var turns = m["turns"]?.Type == JTokenType.Integer
                            ? ((JValue)m["turns"]).Value<long>().ToString(CultureInfo.InvariantCulture)
                            : null;
                        descs.Add(turns != null ? $"{side}:{turns}" : side);
                    }
                    mechParts.Add($"{key}({string.Join(",", descs.ToArray())})");
                }
            lines.Add(mechParts.Count > 0 ? $"- 机制: {string.Join(", ", mechParts.ToArray())}" : "- 机制: 无");

            // 4. 近 N 手（尾部截取；格式 piece_id (fx,fy)→(tx,ty)）
            var history = boardState["move_history"] as JArray;
            if (history != null && history.Count > 0 && recentMoves > 0)
            {
                var from = Math.Max(0, history.Count - recentMoves);
                var moves = new List<string>();
                for (int i = from; i < history.Count; i++)
                {
                    if (!(history[i] is JObject mv)) continue;
                    moves.Add(DescribeMove(mv));
                }
                lines.Add($"- 近 {moves.Count.ToString(CultureInfo.InvariantCulture)} 手: {string.Join("；", moves.ToArray())}");
            }
            else
            {
                lines.Add("- 近 0 手: 无");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 棋子规范名：有 name 用 name；缺省按 <see cref="PieceTypeMapping"/> 纠正表映射 type，
        /// 未收录类型原样返回。move_piece 的 place 自动补名与摘要显示共用此表。
        /// </summary>
        public static string CanonicalPieceName(string type, string name)
        {
            if (!string.IsNullOrEmpty(name)) return name;
            if (string.IsNullOrEmpty(type)) return "未名棋子";
            string mapped;
            return PieceTypeMapping.TryGetValue(type, out mapped) ? mapped : type;
        }

        static string DescribeMove(JObject mv)
        {
            var id = JsonHelpers.GetString(mv, "piece_id");
            if (string.IsNullOrEmpty(id)) id = "?";
            var to = mv["to"] as JArray;
            var toStr = to != null && to.Count >= 2
                ? $"({Pos(to[0])},{Pos(to[1])})"
                : "(?)";
            var from = mv["from"] as JArray;
            if (from != null && from.Count >= 2)
                return $"{id} ({Pos(from[0])},{Pos(from[1])})→{toStr}";
            return $"{id} →{toStr}";
        }

        static string Pos(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer) return "?";
            return ((JValue)token).Value<long>().ToString(CultureInfo.InvariantCulture);
        }

        static string SideLabel(string side)
            => side != null && SideLabels.TryGetValue(side, out var label) ? label : (side ?? "?");
    }
}
