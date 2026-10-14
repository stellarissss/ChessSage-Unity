using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Cheat
{
    /// <summary>
    /// 离线预置指令集（UX §10.3）：无 LLM 密钥时，用**预校验过的补丁模板**驱动完整
    /// 「选择 → 预览 diff → 确认 → 生效 → 业力变化」链路，保证 USP 仍可演示。
    ///
    /// 选取标准（对齐规格）：后果明确、Schema 必然通过、代价适中、点一下就能看到生效。
    /// 所有补丁均为 RFC6902，应用到当前对局的对应配置切片上；不含 LLM 调用。
    /// </summary>
    public static class CheatPresetLibrary
    {
        /// <summary>全量预置（顺序即规格表格顺序）。</summary>
        public static IReadOnlyList<PresetCommand> All()
        {
            return new List<PresetCommand>
            {
                // 1 象棋：马可横走（旗舰项）
                new PresetCommand
                {
                    Text = "让我的马可以横着走", Variant = "xiangqi", Classification = "C", Karma = 40,
                    TargetConfig = "pieces_red",
                    Patch = Patch(
                        AddMove("horse", Move("jump", new[] { 1, 0 }, "rotate4"))),
                },
                // 2 象棋：兵过河后可后退
                new PresetCommand
                {
                    Text = "把我的兵过河后可以后退", Variant = "xiangqi", Classification = "C", Karma = 45,
                    TargetConfig = "pieces_red",
                    Patch = Patch(
                        AddMove("soldier", MoveWithWhere("jump", new[] { 0, -1 }, "none", "crossed_river"))),
                },
                // 3 象棋：车飞过楚河汉界（多出 2 格跨越落点）
                new PresetCommand
                {
                    Text = "让我的车可以飞过楚河汉界", Variant = "xiangqi", Classification = "C", Karma = 45,
                    TargetConfig = "pieces_red",
                    Patch = Patch(
                        AddMove("chariot", Move("jump", new[] { 2, 0 }, "rotate4"))),
                },
                // 4 象棋：对方的兵变成石头（无走法、仍在场）
                new PresetCommand
                {
                    Text = "把对方的兵变成石头", Variant = "xiangqi", Classification = "C", Karma = 53,
                    TargetConfig = "pieces_black",
                    Patch = Patch(Replace("/pieces/soldier/moves", new JArray())),
                },
                // 5 通用：AI 本回合不能动
                new PresetCommand
                {
                    Text = "让AI这一回合不能动", Variant = "common", Classification = "A", Karma = 30,
                    TargetConfig = "board_state",
                    Patch = Patch(Add("/mechanisms/skip_turns/-", new JObject
                    {
                        ["side"] = "black", ["remaining"] = 1, ["reason"] = "天道令其止步",
                    })),
                },
                // 6 通用：悔一步棋（不入改规栈）
                new PresetCommand
                {
                    Text = "悔一步棋", Variant = "common", Classification = "A", Karma = 30,
                    IsUndoMove = true,
                },
                // 7 围棋：棋子被提掉后还能留在盘上（此处以"额外走法"近似演示）
                new PresetCommand
                {
                    Text = "让我的棋子被提掉后还能留在盘上", Variant = "weiqi", Classification = "C", Karma = 50,
                    TargetConfig = "pieces_white",
                    Patch = Patch(AddMove("stone", Move("jump", new[] { 1, 0 }, "rotate4"))),
                },
                // 8 五子棋：落子时一次下两颗（此处以"额外走法"近似演示）
                new PresetCommand
                {
                    Text = "让我的棋子落子时一次下两颗", Variant = "wuziqi", Classification = "C", Karma = 60,
                    TargetConfig = "pieces_black",
                    Patch = Patch(AddMove("stone", Move("jump", new[] { 1, 0 }, "rotate4"))),
                },
                // 9 动物棋：老鼠可以吃掉大象（此处以"额外走法"近似演示）
                new PresetCommand
                {
                    Text = "让我的老鼠可以吃掉大象", Variant = "dongwuqi", Classification = "C", Karma = 50,
                    TargetConfig = "pieces_red",
                    Patch = Patch(AddMove("rat", Move("jump", new[] { 1, 1 }, "rotate4"))),
                },
                // 10 通用：棋子变金色（外观，需「前端修改」技能；未解锁则置灰）
                new PresetCommand
                {
                    Text = "把我的棋子都变成金色", Variant = "common", Classification = "D", Karma = 10,
                    TargetConfig = "ui_config",
                    Patch = Patch(
                        Add("/theme/pieces/red_color", "#ffd700"),
                        Add("/theme/pieces/black_color", "#ffd700")),
                },
            };
        }

        /// <summary>按当前棋种过滤（UX §10.4.2）：象棋局不显示围棋预置。</summary>
        public static List<PresetCommand> ForVariant(string variant)
        {
            var result = new List<PresetCommand>();
            foreach (var p in All())
                if (p.AppliesTo(variant)) result.Add(p);
            return result;
        }

        // ── 补丁构造小工具 ──────────────────────────────────────────────

        static JArray Patch(params JObject[] ops)
        {
            var a = new JArray();
            foreach (var o in ops) a.Add(o);
            return a;
        }

        static JObject Add(string path, JToken value)
            => new JObject { ["op"] = "add", ["path"] = path, ["value"] = value };

        static JObject Replace(string path, JToken value)
            => new JObject { ["op"] = "replace", ["path"] = path, ["value"] = value };

        static JObject AddMove(string pieceType, JObject move)
            => Add("/pieces/" + pieceType + "/moves/-", move);

        static JObject Move(string kind, int[] to, string sym)
            => new JObject
            {
                ["kind"] = kind,
                ["to"] = new JArray(to),
                ["block"] = new JArray(),
                ["land"] = "any",
                ["sym"] = sym,
            };

        static JObject MoveWithWhere(string kind, int[] to, string sym, string regionKeyword)
        {
            var m = Move(kind, to, sym);
            m["where"] = new JArray(new JObject
            {
                [regionKeyword] = new JObject { ["pos"] = "$dest" },
            });
            return m;
        }
    }
}
