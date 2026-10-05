using System;
using System.Collections.Generic;
using System.Text;
using ChessSage.Core.Model;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Rules.Variants
{
    /// <summary>
    /// 无限制围棋规则引擎 —— 落子类游戏（棋子不移动）。
    /// 逐语义移植自 legacy-web/weiqi/rule_engine.py（RuleEngine / FastBoard）与 main.py 的落子语义：
    ///   · 合法落点：空点、非打劫（ko）、非自杀、黑方非禁手（get_valid_moves / is_valid_move）；
    ///   · 落子提子：提掉无气敌组，单子提子写下打劫点（place_stone）；
    ///   · 机制原语：按颜色/符号配置的限气 liberty_cap 与不可吃 uncapturable；
    ///   · 胜负：吃十子胜（check_capture_10）与全歼胜（check_capture_all）。
    /// </summary>
    public sealed class WeiqiRuleEngine : JumpRayRuleBase
    {
        /// <summary>四邻方向，顺序与 Python 一致 [(-1,0),(1,0),(0,-1),(0,1)]。</summary>
        static readonly int[][] Dirs =
        {
            new[] { -1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, 1 },
        };

        /// <summary>禁手判定的四个线方向 [(1,0),(0,1),(1,1),(1,-1)]。</summary>
        static readonly (int dx, int dy)[] LineDirs =
        {
            (1, 0), (0, 1), (1, 1), (1, -1),
        };

        /// <summary>rules.modifiers.go：围棋机制原语（限气 / 不可吃），按颜色或棋子符号配置。</summary>
        readonly JObject _goModifiers;

        public WeiqiRuleEngine(JObject board, JObject piecesBlack, JObject piecesRed, JObject rules)
            : base(board, piecesBlack, piecesRed, rules, "black", "white")
        {
            _goModifiers = (Rules["modifiers"] as JObject)?["go"] as JObject ?? new JObject();
        }

        public override bool IsPlacementGame => true;

        /// <summary>围棋无棋子走法，落子类游戏的移动接口返回空集合。</summary>
        public override List<int[]> GetValidMoves(Piece piece, BoardState boardState) => new List<int[]>();

        // ══════════════════════════════════════════════════════════════
        // 机制原语：限气 liberty_cap / 不可吃 uncapturable
        // ══════════════════════════════════════════════════════════════

        /// <summary>解析颜色级 +（可选）棋子符号级机制原语，符号级覆盖颜色级。</summary>
        JObject ResolveMods(string side, string symbol)
        {
            var result = new JObject();
            if (_goModifiers[side] is JObject colorEntry)
                result.Merge(colorEntry, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            if (!string.IsNullOrEmpty(symbol) && _goModifiers[symbol] is JObject symEntry)
                result.Merge(symEntry, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            return result;
        }

        /// <summary>返回限气上限，未配置或非正整数返回 null。</summary>
        int? LibertyCap(string side, string symbol)
        {
            var v = ResolveMods(side, symbol)["liberty_cap"];
            if (v != null && v.Type == JTokenType.Integer && v.Value<int>() > 0) return v.Value<int>();
            return null;
        }

        /// <summary>返回是否不可被吃（不可断气）。</summary>
        bool Uncapturable(string side, string symbol)
            => ResolveMods(side, symbol)["uncapturable"]?.Value<bool>() ?? false;

        // ══════════════════════════════════════════════════════════════
        // 位置索引 / 连通块 / 气
        // ══════════════════════════════════════════════════════════════

        /// <summary>位置 → 活子索引（O(1) 查找，等价 Python _pos_index）。</summary>
        static Dictionary<(int x, int y), Piece> PosIndex(BoardState boardState)
        {
            var idx = new Dictionary<(int x, int y), Piece>();
            foreach (var p in boardState.Pieces)
                if (p.IsAlive) idx[(p.X, p.Y)] = p;
            return idx;
        }

        /// <summary>获取同色连通块（BFS，等价 Python _get_group）。</summary>
        List<Piece> GetGroup(BoardState boardState, int x, int y)
        {
            var group = new List<Piece>();
            var idx = PosIndex(boardState);
            if (!idx.TryGetValue((x, y), out var start)) return group;
            string target = start.Side;

            var visited = new HashSet<(int x, int y)>();
            var stack = new Stack<(int x, int y)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                if (!visited.Add((cx, cy))) continue;
                if (idx.TryGetValue((cx, cy), out var current) && current.Side == target)
                {
                    group.Add(current);
                    foreach (var d in Dirs)
                    {
                        int nx = cx + d[0], ny = cy + d[1];
                        if (InBounds(nx, ny) && !visited.Contains((nx, ny))) stack.Push((nx, ny));
                    }
                }
            }
            return group;
        }

        /// <summary>计算连通块有效气数：设了 liberty_cap 时取 min(实际气, 上限)（等价 Python _count_liberties）。</summary>
        int CountLiberties(BoardState boardState, List<Piece> group)
        {
            var idx = PosIndex(boardState);
            var liberties = new HashSet<(int x, int y)>();
            foreach (var p in group)
                foreach (var d in Dirs)
                {
                    int nx = p.X + d[0], ny = p.Y + d[1];
                    if (InBounds(nx, ny) && !idx.ContainsKey((nx, ny))) liberties.Add((nx, ny));
                }

            int actual = liberties.Count;
            if (group.Count == 0) return actual;
            var cap = LibertyCap(group[0].Side, group[0].Name);
            return cap.HasValue ? Math.Min(actual, cap.Value) : actual;
        }

        /// <summary>等价 Python _effective_liberties（_count_liberties 已应用上限，重复取 min 幂等）。</summary>
        int EffectiveLiberties(BoardState boardState, List<Piece> group) => CountLiberties(boardState, group);

        /// <summary>计算指定位置棋子所属连通块的气（等价 Python calculate_liberties）。</summary>
        public int CalculateLiberties(BoardState boardState, int x, int y)
        {
            if (GetPieceAt(x, y, boardState) == null) return 0;
            return EffectiveLiberties(boardState, GetGroup(boardState, x, y));
        }

        /// <summary>移除指定位置所属连通块，返回提子数；不可吃（uncapturable）时返回 0（等价 Python remove_group）。</summary>
        public int RemoveGroup(BoardState boardState, int x, int y)
        {
            if (GetPieceAt(x, y, boardState) == null) return 0;
            var group = GetGroup(boardState, x, y);
            if (Uncapturable(group[0].Side, group[0].Name)) return 0;
            foreach (var p in group) p.IsAlive = false;
            return group.Count;
        }

        // ══════════════════════════════════════════════════════════════
        // 合法落点
        // ══════════════════════════════════════════════════════════════

        /// <summary>所有合法落点（空点、非打劫、非自杀、黑方非禁手），遍历顺序 x 外层 / y 内层与 Python 一致。</summary>
        public override List<int[]> GetValidPlacements(BoardState boardState, string side)
        {
            var moves = new List<int[]>();
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    if (IsValidMove(boardState, x, y, side))
                        moves.Add(new[] { x, y });
            return moves;
        }

        /// <summary>判断落子是否合法（等价 Python is_valid_move）。</summary>
        public bool IsValidMove(BoardState boardState, int x, int y, string side)
        {
            if (!InBounds(x, y)) return false;
            if (GetPieceAt(x, y, boardState) != null) return false;
            if (IsKo(boardState, x, y, side)) return false;
            if (IsSuicide(boardState, x, y, side)) return false;
            if (side == "black" && Rules["special_rules"]?["forbidden_black"]?["enabled"]?.Value<bool>() == true)
                if (IsForbiddenBlack(boardState, x, y)) return false;
            return true;
        }

        public override List<GameAction> GetAllActions(BoardState boardState, string side)
        {
            var actions = new List<GameAction>();
            foreach (var m in GetValidPlacements(boardState, side))
                actions.Add(new GameAction("place", null, m[0], m[1]));
            return actions;
        }

        // ══════════════════════════════════════════════════════════════
        // 打劫 / 自杀 / 禁手
        // ══════════════════════════════════════════════════════════════

        /// <summary>打劫：落子后棋盘与上一手打劫点标识相同则禁止（等价 Python _is_ko）。</summary>
        bool IsKo(BoardState boardState, int x, int y, string side)
        {
            if (Rules["special_rules"]?["ko_rule"]?["enabled"]?.Value<bool>() != true) return false;

            string currentKey = boardState.Extra["ko_state"]?.Type == JTokenType.String
                ? boardState.Extra["ko_state"].Value<string>() : null;
            if (string.IsNullOrEmpty(currentKey)) return false;

            var test = boardState.Clone();
            test.Pieces.Add(new Piece("test", "stone", "●", side, x, y));

            string other = Opposite(side);
            foreach (var d in Dirs)
            {
                int nx = x + d[0], ny = y + d[1];
                if (!InBounds(nx, ny)) continue;
                var piece = GetPieceAt(nx, ny, test);
                if (piece == null || piece.Side != other) continue;
                var group = GetGroup(test, nx, ny);
                if (CountLiberties(test, group) != 0) continue;
                if (Uncapturable(other, group.Count > 0 ? group[0].Name : null)) continue;
                foreach (var p in group) p.IsAlive = false;
            }

            return GetBoardKey(test) == currentKey;
        }

        /// <summary>自杀：落子后自身无气且不能提子（等价 Python _is_suicide）。</summary>
        bool IsSuicide(BoardState boardState, int x, int y, string side)
        {
            if (Rules["special_rules"]?["suicide_rule"]?["enabled"]?.Value<bool>() != true) return false;

            var test = boardState.Clone();
            test.Pieces.Add(new Piece("test", "stone", "●", side, x, y));

            string other = Opposite(side);
            foreach (var d in Dirs)
            {
                int nx = x + d[0], ny = y + d[1];
                if (!InBounds(nx, ny)) continue;
                var piece = GetPieceAt(nx, ny, test);
                if (piece == null || piece.Side != other) continue;
                var group = GetGroup(test, nx, ny);
                if (CountLiberties(test, group) != 0) continue;
                if (Uncapturable(other, group.Count > 0 ? group[0].Name : null)) continue;
                return false; // 可提子 → 非自杀
            }

            var self = GetGroup(test, x, y);
            return CountLiberties(test, self) == 0;
        }

        /// <summary>黑棋禁手：长连 / 双活三 / 双冲四（等价 Python _is_forbidden_black）。</summary>
        bool IsForbiddenBlack(BoardState boardState, int x, int y)
        {
            var test = boardState.Clone();
            test.Pieces.Add(new Piece("test", "stone", "●", "black", x, y));

            if (CheckOverline(test, x, y, "black")) return true;
            if (CountOpenThrees(test, x, y, "black") >= 2) return true;
            if (CountOpenFours(test, x, y, "black") >= 2) return true;
            return false;
        }

        /// <summary>是否长连（超过五连，等价 Python _check_overline）。</summary>
        bool CheckOverline(BoardState boardState, int x, int y, string side)
        {
            foreach (var (dx, dy) in LineDirs)
            {
                int count = 1;
                for (int step = 1; step <= 6; step++)
                {
                    int nx = x + dx * step, ny = y + dy * step;
                    if (!InBounds(nx, ny)) break;
                    var piece = GetPieceAt(nx, ny, boardState);
                    if (piece == null || piece.Side != side) break;
                    count++;
                }
                for (int step = 1; step <= 6; step++)
                {
                    int nx = x - dx * step, ny = y - dy * step;
                    if (!InBounds(nx, ny)) break;
                    var piece = GetPieceAt(nx, ny, boardState);
                    if (piece == null || piece.Side != side) break;
                    count++;
                }
                if (count > 5) return true;
            }
            return false;
        }

        /// <summary>活三模式（None=空，S=同色）。</summary>
        static readonly string[][] OpenThreePatterns =
        {
            new[] { null, "S", "S", "S", null },
            new[] { null, "S", null, "S", "S" },
            new[] { "S", null, "S", "S", null },
            new[] { null, "S", "S", null, "S" },
            new[] { "S", "S", null, "S", null },
        };

        /// <summary>冲四模式（None=空，S=同色）。</summary>
        static readonly string[][] OpenFourPatterns =
        {
            new[] { null, "S", "S", "S", "S" },
            new[] { "S", null, "S", "S", "S" },
            new[] { "S", "S", null, "S", "S" },
            new[] { "S", "S", "S", null, "S" },
            new[] { "S", "S", "S", "S", null },
        };

        int CountOpenThrees(BoardState boardState, int x, int y, string side)
        {
            int count = 0;
            foreach (var (dx, dy) in LineDirs)
                if (MatchPattern(BuildWindow(boardState, x, y, dx, dy), side, OpenThreePatterns)) count++;
            return count;
        }

        int CountOpenFours(BoardState boardState, int x, int y, string side)
        {
            int count = 0;
            foreach (var (dx, dy) in LineDirs)
                if (MatchPattern(BuildWindow(boardState, x, y, dx, dy), side, OpenFourPatterns)) count++;
            return count;
        }

        /// <summary>取以 (x,y) 为中心、沿 (dx,dy) 的 ±2 窗口（等价 Python line[2:7]）。</summary>
        string[] BuildWindow(BoardState boardState, int x, int y, int dx, int dy)
        {
            var window = new string[5];
            for (int step = -2; step <= 2; step++)
            {
                int nx = x + dx * step, ny = y + dy * step;
                if (!InBounds(nx, ny)) window[step + 2] = "border";
                else window[step + 2] = GetPieceAt(nx, ny, boardState)?.Side;
            }
            return window;
        }

        static bool MatchPattern(string[] window, string side, string[][] patterns)
        {
            foreach (var pat in patterns)
            {
                bool ok = true;
                for (int i = 0; i < 5; i++)
                {
                    string expect = pat[i] == "S" ? side : pat[i];
                    if (window[i] != expect) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════
        // 落子 / 提子
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 执行落子：合法校验 → 落子 → 提掉无气敌组 → 累加提子数 → 单子提子设打劫点。
        /// 回合切换由调用方负责（与 Python place_stone 一致）。
        /// </summary>
        public override ActionOutcome ApplyAction(BoardState boardState, GameAction action, string side)
        {
            var outcome = new ActionOutcome();
            if (action == null || action.Kind != "place")
            {
                outcome.Ok = false;
                outcome.Message = "围棋仅支持 place 落子";
                return outcome;
            }

            int x = action.X, y = action.Y;
            if (!InBounds(x, y)) { outcome.Ok = false; outcome.Message = "位置超出棋盘范围"; return outcome; }
            if (GetPieceAt(x, y, boardState) != null) { outcome.Ok = false; outcome.Message = "该位置已有棋子"; return outcome; }
            if (!IsValidMove(boardState, x, y, side)) { outcome.Ok = false; outcome.Message = "非法落子（打劫或自杀）"; return outcome; }

            string label = side == "black" ? "●" : "○";
            string id = side + "_stone_" + x + "_" + y + "_" + boardState.Pieces.Count;
            boardState.Pieces.Add(new Piece(id, "stone", label, side, x, y));

            int capturedCount = 0;
            string other = Opposite(side);
            foreach (var d in Dirs)
            {
                int nx = x + d[0], ny = y + d[1];
                if (!InBounds(nx, ny)) continue;
                var piece = GetPieceAt(nx, ny, boardState);
                if (piece == null || piece.Side != other) continue;

                var group = GetGroup(boardState, nx, ny);
                if (CountLiberties(boardState, group) != 0) continue;
                if (Uncapturable(other, group.Count > 0 ? group[0].Name : null)) continue;

                capturedCount += group.Count;
                foreach (var p in group)
                {
                    p.IsAlive = false;
                    outcome.CapturedIds.Add(p.Id);
                }
            }

            var captures = boardState.Extra["captures"] as JObject;
            if (captures == null)
            {
                captures = new JObject { ["black"] = 0, ["white"] = 0 };
                boardState.Extra["captures"] = captures;
            }
            captures[side] = (captures[side]?.Value<int>() ?? 0) + capturedCount;

            string prevKo = boardState.Extra["ko_state"]?.Type == JTokenType.String
                ? boardState.Extra["ko_state"].Value<string>() : null;
            boardState.Extra["ko_state"] = JValue.CreateNull();
            if (capturedCount == 1)
            {
                string key = GetBoardKey(boardState);
                if (prevKo == null || key != prevKo) boardState.Extra["ko_state"] = key;
            }

            outcome.Ok = true;
            return outcome;
        }

        /// <summary>棋盘唯一标识，用于打劫检测；格式与 Python str(sorted(pieces)) 完全一致。</summary>
        string GetBoardKey(BoardState boardState)
        {
            var items = new List<(int x, int y, string side)>();
            foreach (var p in boardState.Pieces)
                if (p.IsAlive) items.Add((p.X, p.Y, p.Side));

            items.Sort((a, b) =>
            {
                int c = a.x.CompareTo(b.x); if (c != 0) return c;
                c = a.y.CompareTo(b.y); if (c != 0) return c;
                return string.CompareOrdinal(a.side, b.side);
            });

            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('(').Append(items[i].x).Append(", ").Append(items[i].y).Append(", '").Append(items[i].side).Append("')");
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════
        // 胜负判定
        // ══════════════════════════════════════════════════════════════

        /// <summary>吃十子胜 / 全歼胜（等价 Python check_capture_10 / check_capture_all）。</summary>
        public override GameOutcome CheckOutcome(BoardState boardState)
        {
            var captures = boardState.Extra["captures"] as JObject;
            int target = Rules["win_conditions"]?["capture_10"]?["target"]?.Value<int>() ?? 10;
            int blackCap = captures?["black"]?.Value<int>() ?? 0;
            int whiteCap = captures?["white"]?.Value<int>() ?? 0;
            if (blackCap >= target) return new GameOutcome { Ended = true, Winner = "black", Condition = "capture_10" };
            if (whiteCap >= target) return new GameOutcome { Ended = true, Winner = "white", Condition = "capture_10" };

            // 全歼：某方曾落子（含被提的死子）而活子归零，对方仍有活子。
            bool blackEver = false, whiteEver = false, blackAlive = false, whiteAlive = false;
            foreach (var p in boardState.Pieces)
            {
                if (p.Side == "black") { blackEver = true; if (p.IsAlive) blackAlive = true; }
                else if (p.Side == "white") { whiteEver = true; if (p.IsAlive) whiteAlive = true; }
            }
            if (blackEver && !blackAlive && whiteAlive) return new GameOutcome { Ended = true, Winner = "white", Condition = "capture_all" };
            if (whiteEver && !whiteAlive && blackAlive) return new GameOutcome { Ended = true, Winner = "black", Condition = "capture_all" };

            return GameOutcome.Ongoing;
        }
    }
}