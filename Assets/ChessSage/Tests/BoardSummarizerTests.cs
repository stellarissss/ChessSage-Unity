using System.Collections.Generic;
using ChessSage.Core.Agent;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>
    /// E1-S6：BoardSummarizer（从旧 OrchestratorEngine.GetBoardSummary 移植的纯函数）。
    /// 期望值独立推导：摘要 = 棋盘尺寸 + 回合 + 双方残子按类型聚合 + 机制 + 近 N 手；
    /// 确定性（同快照同字节，A4）；PieceTypeMapping 纠正表与旧表逐字一致（迁移表点名的坑）。
    /// </summary>
    [TestFixture]
    public sealed class BoardSummarizerTests
    {
        static JObject SampleBoard()
        {
            var board = JObject.Parse(@"{
              ""pieces"": [
                {""id"": ""r_general_1"", ""type"": ""general"", ""name"": ""帅"", ""side"": ""red"", ""position"": [4, 9], ""is_alive"": true},
                {""id"": ""r_chariot_1"", ""type"": ""chariot"", ""name"": ""车"", ""side"": ""red"", ""position"": [0, 9], ""is_alive"": true},
                {""id"": ""r_soldier_1"", ""type"": ""soldier"", ""name"": ""兵"", ""side"": ""red"", ""position"": [0, 6], ""is_alive"": true},
                {""id"": ""r_soldier_2"", ""type"": ""soldier"", ""name"": ""兵"", ""side"": ""red"", ""position"": [2, 6], ""is_alive"": false},
                {""id"": ""b_general_1"", ""type"": ""general"", ""name"": ""将"", ""side"": ""black"", ""position"": [4, 0], ""is_alive"": true},
                {""id"": ""b_horse_1"", ""type"": ""horse"", ""name"": ""马"", ""side"": ""black"", ""position"": [1, 0], ""is_alive"": true},
                {""id"": ""b_horse_2"", ""type"": ""horse"", ""name"": ""马"", ""side"": ""black"", ""position"": [7, 0], ""is_alive"": true}
              ],
              ""current_turn"": ""red"",
              ""move_history"": [
                {""piece_id"": ""r_soldier_1"", ""from"": [0,6], ""to"": [0,5], ""kind"": ""move"", ""captured"": null, ""flipped"": []},
                {""piece_id"": ""b_horse_1"", ""from"": [1,0], ""to"": [2,2], ""kind"": ""move"", ""captured"": null, ""flipped"": []}
              ],
              ""game_status"": {""state"": ""playing"", ""winner"": null, ""win_condition"": null, ""custom_rules_active"": []},
              ""mechanisms"": {
                ""skip_turns"": [], ""ai_control"": [], ""player_control"": [],
                ""random_moves"": [], ""extra_turns"": [], ""move_limits"": []
              }
            }");
            return board;
        }

        [Test]
        public void PieceTypeMapping_CorrectionTable_MatchesLegacyVerbatim()
        {
            // 旧 OrchestratorEngine.PieceTypeMapping 逐字（工程侧 §8 迁移表点名的坑）。
            Assert.AreEqual("soldier", BoardSummarizer.PieceTypeMapping["pawn"]);
            Assert.AreEqual("chariot", BoardSummarizer.PieceTypeMapping["rook"]);
            Assert.AreEqual("horse", BoardSummarizer.PieceTypeMapping["knight"]);
            Assert.AreEqual("elephant", BoardSummarizer.PieceTypeMapping["bishop"]);
            Assert.AreEqual("advisor", BoardSummarizer.PieceTypeMapping["queen"]);
            Assert.AreEqual("general", BoardSummarizer.PieceTypeMapping["king"]);
            Assert.AreEqual(6, BoardSummarizer.PieceTypeMapping.Count);
        }

        [Test]
        public void CanonicalPieceName_UsesCorrectionTable_WhenNameMissing()
        {
            Assert.AreEqual("soldier", BoardSummarizer.CanonicalPieceName("pawn", null));
            Assert.AreEqual("general", BoardSummarizer.CanonicalPieceName("king", ""));
            Assert.AreEqual("車", BoardSummarizer.CanonicalPieceName("chariot", "車"), "有 name 时 name 优先");
            Assert.AreEqual("cannon", BoardSummarizer.CanonicalPieceName("cannon", null), "未收录类型原样返回");
        }

        [Test]
        public void Summarize_CountsAlivePiecesBySide()
        {
            var text = BoardSummarizer.Summarize(SampleBoard());

            // 红方 4 子中 3 存活（soldier_2 已死）；黑方 3 子全存活。
            StringAssert.Contains("红方 3 子", text);
            StringAssert.Contains("黑方 3 子", text);
            StringAssert.Contains("general×1", text);
            StringAssert.Contains("soldier×1", text, "已死棋子不计入残子");
            StringAssert.Contains("horse×2", text);
        }

        [Test]
        public void Summarize_IncludesTurnBoardMechanismsAndRecentMoves()
        {
            var board = SampleBoard();
            board["current_turn"] = "black";
            board["mechanisms"]["skip_turns"] = new JArray { new JObject { ["side"] = "red", ["turns"] = 1 } };

            var text = BoardSummarizer.Summarize(board, recentMoves: 5);

            StringAssert.Contains("当前轮到 黑方", text);
            StringAssert.Contains("机制: skip_turns(red:1)", text);
            StringAssert.Contains("近 2 手", text);
            StringAssert.Contains("r_soldier_1 (0,6)→(0,5)", text);
        }

        [Test]
        public void Summarize_IsDeterministic_SameSnapshotSameBytes()
        {
            var board = SampleBoard();
            var a = BoardSummarizer.Summarize(board);
            var b = BoardSummarizer.Summarize(board);
            Assert.AreEqual(a, b, "同快照必须字节级一致（A4 前缀稳定/可回放）");

            // 打乱 pieces 数组顺序不应改变聚合输出（聚合按类型排序，不依赖数组序）。
            var shuffled = (JObject)board.DeepClone();
            var pieces = (JArray)shuffled["pieces"];
            var first = pieces[0];
            pieces.RemoveAt(0);
            pieces.Add(first);
            Assert.AreEqual(a, BoardSummarizer.Summarize(shuffled), "聚合结果与 pieces 数组序无关");
        }

        [Test]
        public void Summarize_RecentMovesTruncatedToTail()
        {
            var board = SampleBoard();
            var history = (JArray)board["move_history"];
            for (int i = 0; i < 8; i++)
                history.Add(new JObject
                {
                    ["piece_id"] = $"p{i}",
                    ["from"] = new JArray(0, 0),
                    ["to"] = new JArray(i, i),
                    ["kind"] = "move",
                });

            var text = BoardSummarizer.Summarize(board, recentMoves: 3);
            StringAssert.Contains("近 3 手", text);
            StringAssert.Contains("p7", text, "只取尾部 3 手");
            Assert.IsFalse(text.Contains("p4 ("), "更早的手不出现");
        }

        [Test]
        public void Summarize_NullAndEmptyBoard_DoNotThrow()
        {
            Assert.DoesNotThrow(() => BoardSummarizer.Summarize(null));
            Assert.DoesNotThrow(() => BoardSummarizer.Summarize(new JObject()));
            var empty = BoardSummarizer.Summarize(new JObject());
            StringAssert.Contains("红方 0 子", empty);
        }

        [Test]
        public void Summarize_BoardSizeFromBoardConfig()
        {
            var boardConfig = JObject.Parse(@"{""width"": 8, ""height"": 7}");
            var text = BoardSummarizer.Summarize(SampleBoard(), boardConfig);
            StringAssert.Contains("棋盘 8x7", text);

            // 无 board 配置时不谎报尺寸（缺省不输出尺寸段）。
            var noCfg = BoardSummarizer.Summarize(SampleBoard(), null);
            Assert.IsFalse(noCfg.Contains("棋盘 "), "缺 board 配置则不输出尺寸段");
        }

        [Test]
        public void Summarize_Compact_UnderTokenBudget()
        {
            // 设计侧 §6.2：紧凑摘要 ≈0.3-1K token；32 子满盘按 4 字符/token 粗估须 < 1600 字节。
            var board = SampleBoard();
            var pieces = (JArray)board["pieces"];
            for (int i = 0; i < 32; i++)
                pieces.Add(new JObject
                {
                    ["id"] = $"x{i}",
                    ["type"] = "soldier",
                    ["name"] = "兵",
                    ["side"] = i % 2 == 0 ? "red" : "black",
                    ["position"] = new JArray(i % 9, i / 9),
                    ["is_alive"] = true,
                });

            var text = BoardSummarizer.Summarize(board);
            Assert.Less(text.Length, 1600, $"摘要过长（{text.Length} 字符），会烧掉上下文预算");
        }
    }
}
