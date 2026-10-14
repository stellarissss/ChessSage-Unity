using System.Collections.Generic;
using ChessSage.Core.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Cheat
{
    /// <summary>
    /// 改规预览的「人读 diff」渲染器（UX §5.1）。
    ///
    /// 输入 <c>ProcessCommandAsync</c> 返回的 <c>modified_configs</c>（全新完整配置，键为
    /// board_state / pieces_* / rules / ui_config / board）与当前对局配置，输出一组
    /// 「改前 → 改后」对照卡片。**绝不**直接显示原始 JSON Pointer。
    ///
    /// 跨配置联动（UX §5.4.2）：同一棋子的改动无论落在 board_state 还是 pieces_*，
    /// 都按「棋子类型」合并到同一张卡，避免玩家以为改了两处。
    /// </summary>
    public static class CheatDiffRenderer
    {
        public static List<DiffCard> Render(JObject oldConfigs, JObject modifiedConfigs)
        {
            var cards = new List<DiffCard>();
            var merge = new Dictionary<string, DiffCard>();
            if (modifiedConfigs == null) return cards;

            foreach (var prop in modifiedConfigs.Properties())
            {
                var key = prop.Name;
                if (!(prop.Value is JObject newCfg)) continue;
                var oldCfg = oldConfigs?[key] as JObject ?? new JObject();
                var patch = JsonPatch.GenerateDiff(oldCfg, newCfg);

                switch (key)
                {
                    case "pieces_red":
                    case "pieces_black":
                    case "pieces_white":
                        RenderPieces(key, oldCfg, newCfg, patch, merge, cards);
                        break;
                    case "board_state":
                        RenderBoardState(oldCfg, newCfg, patch, merge, cards);
                        break;
                    case "rules":
                        RenderGrouped("group:rules", "规则修改", oldCfg, newCfg, patch, merge, cards, RulesFieldLabel);
                        break;
                    case "ui_config":
                        RenderGrouped("group:ui", "外观修改", oldCfg, newCfg, patch, merge, cards, UiFieldLabel);
                        break;
                    case "board":
                        RenderGrouped("group:board", "棋盘修改", oldCfg, newCfg, patch, merge, cards, null);
                        break;
                }
            }
            return cards;
        }

        // ── 棋子规则配置 ────────────────────────────────────────────────

        static void RenderPieces(string cfgKey, JObject oldCfg, JObject newCfg,
            JArray patch, Dictionary<string, DiffCard> merge, List<DiffCard> cards)
        {
            var side = SideLabel(GetStr(newCfg, "side", GetStr(oldCfg, "side")));
            var oldPieces = oldCfg["pieces"] as JObject ?? new JObject();
            var newPieces = newCfg["pieces"] as JObject ?? new JObject();

            foreach (var type in ChangedPieceTypes(patch))
            {
                var op = oldPieces[type] as JObject ?? new JObject();
                var np = newPieces[type] as JObject ?? new JObject();
                var label = GetStr(np, "label", GetStr(op, "label", type));
                var card = GetOrAdd(merge, "piece:" + type, $"{side}·{label}", cards);

                var oldMoves = op["moves"] as JArray;
                var newMoves = np["moves"] as JArray;
                if (!JToken.DeepEquals(oldMoves, newMoves))
                {
                    card.Lines.Add(new DiffLine($"{label}（{side}）", DescribeMoves(oldMoves), DescribeMoves(newMoves)));
                    card.Impact = $"走法数量 {Count(oldMoves)} → {Count(newMoves)}";
                }
                if (GetStr(op, "label") != GetStr(np, "label"))
                    card.Lines.Add(new DiffLine($"{label}·名称", GetStr(op, "label", "无"), GetStr(np, "label", "无")));
            }
        }

        // ── 棋盘状态（棋子位置 / 存活 / 类型）────────────────────────────

        static void RenderBoardState(JObject oldCfg, JObject newCfg, JArray patch,
            Dictionary<string, DiffCard> merge, List<DiffCard> cards)
        {
            var oldPieces = oldCfg["pieces"] as JArray;
            var newPieces = newCfg["pieces"] as JArray;

            foreach (var i in ChangedArrayIndices(patch, "pieces"))
            {
                var op = PieceAt(oldPieces, i);
                var np = PieceAt(newPieces, i);
                var type = GetStr(np, "type", GetStr(op, "type", "棋子"));
                var name = GetStr(np, "name", GetStr(op, "name", type));
                var card = GetOrAdd(merge, "piece:" + type, name, cards);

                if (!JToken.DeepEquals(op?["position"], np?["position"]))
                    card.Lines.Add(new DiffLine(name, PosStr(op), PosStr(np)));
                var oldAlive = op == null || GetBool(op, "is_alive", true);
                var newAlive = np == null || GetBool(np, "is_alive", true);
                if (oldAlive != newAlive)
                    card.Lines.Add(new DiffLine($"{name}·在场", oldAlive ? "在场" : "已移除",
                        newAlive ? "在场" : "移除"));
                if (GetStr(op, "type") != GetStr(np, "type"))
                    card.Lines.Add(new DiffLine($"{name}·类型", GetStr(op, "type", "无"), GetStr(np, "type", "无")));
            }

            // board_state 上除棋子列表以外的改动（如 mechanisms 机制）单独成卡。
            var mechCard = (DiffCard)null;
            foreach (var token in patch)
            {
                if (!(token is JObject op)) continue;
                var path = op["path"]?.Value<string>() ?? string.Empty;
                var segs = JsonPatch.ParsePointer(path);
                if (segs.Count > 0 && segs[0] == "pieces") continue;

                if (mechCard == null)
                    mechCard = GetOrAdd(merge, "group:board_state", "机制修改", cards);
                var label = BoardStateFieldLabel(segs);
                mechCard.Lines.Add(new DiffLine(label, ValueAt(oldCfg, path), ValueAt(newCfg, path)));
            }
        }

        static string BoardStateFieldLabel(List<string> segs)
        {
            var map = new Dictionary<string, string>
            {
                ["mechanisms"] = "机制",
                ["skip_turns"] = "跳过回合",
                ["ai_control"] = "AI 接管",
                ["player_control"] = "玩家控制",
                ["random_moves"] = "随机走棋",
                ["extra_turns"] = "额外回合",
                ["move_limits"] = "走棋步数限制",
                ["side"] = "阵营",
                ["remaining"] = "剩余",
                ["reason"] = "原因",
                ["limit"] = "上限",
                ["current_turn"] = "当前回合",
                ["game_status"] = "对局状态",
            };
            var parts = new List<string>();
            foreach (var s in segs)
                parts.Add(map.TryGetValue(s, out var m) ? m : (s == "-" ? "新增" : s));
            return string.Join("·", parts);
        }

        // ── rules / ui_config / board：一处一张卡，逐字段成行 ───────────────

        static void RenderGrouped(string groupKey, string title, JObject oldCfg, JObject newCfg,
            JArray patch, Dictionary<string, DiffCard> merge, List<DiffCard> cards,
            System.Func<List<string>, string> fieldLabel)
        {
            if (patch.Count == 0) return;
            var card = GetOrAdd(merge, groupKey, title, cards);

            foreach (var token in patch)
            {
                if (!(token is JObject op)) continue;
                var path = op["path"]?.Value<string>() ?? string.Empty;
                var segs = JsonPatch.ParsePointer(path);
                var label = fieldLabel != null ? fieldLabel(segs) : FallbackFieldLabel(segs);
                var before = ValueAt(oldCfg, path);
                var after = ValueAt(newCfg, path);
                card.Lines.Add(new DiffLine(label, before, after));
            }
        }

        // ── 描述工具 ───────────────────────────────────────────────────

        static string DescribeMoves(JArray moves)
        {
            if (moves == null || moves.Count == 0) return "无走法";
            var parts = new List<string>();
            foreach (var m in moves)
                if (m is JObject o) parts.Add(DescribeMove(o));
            return string.Join(" 或 ", parts);
        }

        static string DescribeMove(JObject m)
        {
            var sb = new List<string>();
            var kind = GetStr(m, "kind");
            if (kind == "jump")
            {
                var to = m["to"];
                sb.Add("跳跃" + (to is JArray ta ? Arr(ta) : to?.Type == JTokenType.String ? "前进" : "?"));
            }
            else if (kind == "ray")
            {
                var dir = m["dir"] as JArray;
                sb.Add("直线" + (dir != null ? Arr(dir) : "") + MaxStr(m));
            }
            else
            {
                sb.Add(string.IsNullOrEmpty(kind) ? "未知走法" : kind);
            }

            var block = m["block"] as JArray;
            if (block != null && block.Count > 0 && block[0] is JArray b0) sb.Add("受阻" + Arr(b0));

            var screens = m["screens"];
            if (screens != null && screens.Type == JTokenType.Integer && screens.Value<int>() > 0)
                sb.Add("隔" + screens.Value<int>() + "子");

            var land = GetStr(m, "land");
            if (land == "empty") sb.Add("仅空位");
            else if (land == "enemy") sb.Add("仅敌子");

            var sym = GetStr(m, "sym");
            if (sym == "rotate4") sb.Add("四向");
            else if (sym == "rotate4_mirror") sb.Add("四向镜像");
            else if (sym == "mirror_x") sb.Add("左右镜像");

            var where = m["where"] as JArray;
            if (where != null && where.Count > 0)
                sb.Add(where.Count == 1 && where[0] is JObject w && w["crossed_river"] != null ? "限过河" : "有限制");

            return string.Join("·", sb);
        }

        static string MaxStr(JObject m)
        {
            var max = m["max"];
            if (max == null || max.Type != JTokenType.Integer) return "";
            var v = max.Value<int>();
            return v < 0 ? "不限步" : "≤" + v + "格";
        }

        static string Arr(JArray a)
        {
            if (a == null) return "";
            var parts = new List<string>();
            foreach (var t in a) parts.Add(t.ToString());
            return "(" + string.Join(",", parts) + ")";
        }

        static int Count(JArray a) => a?.Count ?? 0;

        static string PosStr(JObject piece)
        {
            if (piece == null) return "(无)";
            var pos = piece["position"];
            if (pos is JArray a && a.Count == 2) return $"({a[0]},{a[1]})";
            return "(无)";
        }

        static JObject PieceAt(JArray arr, int i)
            => arr != null && i >= 0 && i < arr.Count ? arr[i] as JObject : null;

        static List<string> ChangedPieceTypes(JArray patch)
        {
            var list = new List<string>();
            foreach (var token in patch)
            {
                if (!(token is JObject op)) continue;
                var segs = JsonPatch.ParsePointer(op["path"]?.Value<string>() ?? "");
                if (segs.Count >= 2 && segs[0] == "pieces" && !list.Contains(segs[1])) list.Add(segs[1]);
            }
            return list;
        }

        static List<int> ChangedArrayIndices(JArray patch, string arrayKey)
        {
            var list = new List<int>();
            foreach (var token in patch)
            {
                if (!(token is JObject op)) continue;
                var segs = JsonPatch.ParsePointer(op["path"]?.Value<string>() ?? "");
                if (segs.Count >= 2 && segs[0] == arrayKey && int.TryParse(segs[1], out var idx) && !list.Contains(idx))
                    list.Add(idx);
            }
            return list;
        }

        static DiffCard GetOrAdd(Dictionary<string, DiffCard> merge, string key, string title, List<DiffCard> cards)
        {
            if (merge.TryGetValue(key, out var existing)) return existing;
            var card = new DiffCard { Title = title };
            merge[key] = card;
            cards.Add(card);
            return card;
        }

        static string ValueAt(JObject cfg, string path)
        {
            if (cfg == null) return "(无)";
            try
            {
                var parts = JsonPatch.ParsePointer(path);
                JToken cur = cfg;
                foreach (var part in parts)
                {
                    if (cur is JObject jo)
                    {
                        if (!jo.TryGetValue(part, out cur)) return "(无)";
                    }
                    else if (cur is JArray ja && int.TryParse(part, out var idx) && idx >= 0 && idx < ja.Count)
                    {
                        cur = ja[idx];
                    }
                    else
                    {
                        return "(无)";
                    }
                }
                return FormatValue(cur);
            }
            catch
            {
                return "(无)";
            }
        }

        static string FormatValue(JToken v)
        {
            if (v == null || v.Type == JTokenType.Null) return "(无)";
            switch (v.Type)
            {
                case JTokenType.Boolean: return v.Value<bool>() ? "启用" : "关闭";
                case JTokenType.String: return v.Value<string>();
                case JTokenType.Integer: return v.ToString();
                case JTokenType.Float: return v.ToString();
                default: return v.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        static string FallbackFieldLabel(List<string> segs)
            => segs.Count == 0 ? "整体" : string.Join(".", segs);

        static string RulesFieldLabel(List<string> segs)
        {
            var map = new Dictionary<string, string>
            {
                ["win_conditions"] = "胜利条件",
                ["enabled"] = "启用",
                ["display_name"] = "名称",
                ["description"] = "说明",
                ["priority"] = "优先级",
                ["special_rules"] = "特殊规则",
                ["draw_rules"] = "和棋规则",
                ["side_overrides"] = "阵营规则",
                ["pieces"] = "棋子规则",
                ["moves"] = "走法",
            };
            var parts = new List<string>();
            foreach (var s in segs) parts.Add(map.TryGetValue(s, out var m) ? m : s);
            return string.Join("·", parts);
        }

        static string UiFieldLabel(List<string> segs)
        {
            var map = new Dictionary<string, string>
            {
                ["theme"] = "主题",
                ["pieces"] = "棋子",
                ["board"] = "棋盘",
                ["highlight"] = "高亮",
                ["red_color"] = "红方颜色",
                ["black_color"] = "黑方颜色",
                ["red_bg"] = "红方底色",
                ["black_bg"] = "黑方底色",
                ["background_color"] = "背景色",
                ["line_color"] = "线条色",
                ["font_family"] = "字体",
                ["selected"] = "选中色",
                ["valid_move"] = "可走点色",
                ["last_move"] = "最近一步色",
            };
            var parts = new List<string>();
            foreach (var s in segs) parts.Add(map.TryGetValue(s, out var m) ? m : s);
            return string.Join("·", parts);
        }

        static string SideLabel(string side)
            => side == "red" ? "红方" : side == "black" ? "黑方" : side == "white" ? "白方" : (side ?? "");

        static string GetStr(JObject o, string key, string def = "")
        {
            var v = o?[key];
            if (v == null || v.Type == JTokenType.Null) return def;
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }

        static bool GetBool(JObject o, string key, bool def)
        {
            var v = o?[key];
            if (v == null || v.Type == JTokenType.Null) return def;
            if (v.Type == JTokenType.Boolean) return v.Value<bool>();
            return def;
        }
    }
}
