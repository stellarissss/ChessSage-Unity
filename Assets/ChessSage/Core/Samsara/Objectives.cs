using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 目标系统（对应 legacy-web/samsara/objectives.py 的 ObjectiveSystem）。
    /// 依据关卡目标类型校验棋盘状态是否达成。
    /// </summary>
    public sealed class ObjectiveSystem
    {
        private readonly SamsaraState _state;

        public ObjectiveSystem(SamsaraState state, string objectiveTypesPath)
        {
            _state = state;
            ObjectiveTypes = SamsaraJson.LoadObject(objectiveTypesPath) ?? DefaultObjectiveTypes();
        }

        /// <summary>目标类型定义（objective_types.json 真源）。</summary>
        public JObject ObjectiveTypes { get; private set; }

        private static JObject DefaultObjectiveTypes()
        {
            var types = new JObject();
            var definitions = new[]
            {
                new object[] { "checkmate", "将死对方", "使对方无路可走" },
                new object[] { "capture_count", "吃子数目标", "吃掉对方指定数量的棋子" },
                new object[] { "turn_limit", "竞速", "在限定回合内获胜" },
                new object[] { "evacuation", "撤离", "将指定棋子撤离到目标区域" },
                new object[] { "board_coverage", "棋盘覆盖率", "己方棋子覆盖指定百分比的棋盘" },
                new object[] { "formation", "特定阵型", "组成指定阵型" },
                new object[] { "color_coverage", "颜色覆盖率", "点亮指定百分比的暗色区域" },
                new object[] { "survival", "生存", "坚持指定回合数并保留一定棋子" },
                new object[] { "assassination", "刺杀", "在限定回合内吃掉对方指定棋子" },
                new object[] { "escort", "护送", "护送指定棋子沿路径前进" },
                new object[] { "compound", "复合条件", "满足多个条件" },
            };
            foreach (var definition in definitions)
            {
                types[(string)definition[0]] = new JObject
                {
                    ["name"] = (string)definition[1],
                    ["description"] = (string)definition[2],
                    ["turn_limit"] = 20,
                };
            }
            return types;
        }

        private static bool IsAlive(JObject piece)
        {
            return SamsaraJson.GetBool(piece, "is_alive", true);
        }

        private static string Side(JObject piece)
        {
            return SamsaraJson.GetString(piece, "side", "");
        }

        private static JArray Pieces(JObject gameState)
        {
            return gameState == null ? null : gameState["pieces"] as JArray;
        }

        private static int TokenInt(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try
            {
                return token.Value<int>();
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool PositionEquals(JToken position, int x, int y)
        {
            var arr = position as JArray;
            if (arr == null || arr.Count < 2) return false;
            return TokenInt(arr[0], int.MinValue) == x && TokenInt(arr[1], int.MinValue) == y;
        }

        private static int PositionAt(JToken position, int index)
        {
            var arr = position as JArray;
            if (arr == null || index >= arr.Count) return 0;
            return TokenInt(arr[index], 0);
        }

        /// <summary>按目标类型分派校验，返回 {completed, progress, message}。</summary>
        public JObject Check(JObject objective, JObject gameState)
        {
            var objType = SamsaraJson.GetString(objective, "type", "checkmate");
            switch (objType)
            {
                case "checkmate": return CheckCheckmate(objective, gameState);
                case "capture_count": return CheckCaptureCount(objective, gameState);
                case "turn_limit": return CheckTurnLimit(objective, gameState);
                case "evacuation": return CheckEvacuation(objective, gameState);
                case "board_coverage": return CheckBoardCoverage(objective, gameState);
                case "color_coverage": return CheckColorCoverage(objective, gameState);
                case "survival": return CheckSurvival(objective, gameState);
                case "assassination": return CheckAssassination(objective, gameState);
                case "compound": return CheckCompound(objective, gameState);
                default:
                    return new JObject
                    {
                        ["completed"] = false,
                        ["progress"] = 0,
                        ["message"] = "未知目标类型",
                    };
            }
        }

        private static JObject Result(bool completed, double progress, string message)
        {
            return new JObject
            {
                ["completed"] = completed,
                ["progress"] = progress,
                ["message"] = message,
            };
        }

        private static JObject CheckCheckmate(JObject objective, JObject gameState)
        {
            var gameStatus = gameState == null ? null : gameState["game_status"] as JObject;
            if (SamsaraJson.GetString(gameStatus, "state", "") == "ended"
                && SamsaraJson.GetString(gameStatus, "winner", "") == "red")
            {
                return Result(true, 100, "已将死对方");
            }
            return Result(false, 0, "继续将死对方");
        }

        private static JObject CheckCaptureCount(JObject objective, JObject gameState)
        {
            var target = SamsaraJson.GetInt(objective, "target", 1);
            var captured = 0;
            var pieces = Pieces(gameState);
            if (pieces != null)
            {
                foreach (var item in pieces)
                {
                    var piece = item as JObject;
                    if (piece != null && !IsAlive(piece) && Side(piece) == "black") captured++;
                }
            }
            var progress = Math.Min(100, (int)(captured / (double)target * 100));
            return Result(captured >= target, progress, "已吃掉" + captured + "/" + target + "个棋子");
        }

        private JObject CheckTurnLimit(JObject objective, JObject gameState)
        {
            var maxTurns = SamsaraJson.GetInt(objective, "max_turns", 20);
            var currentTurn = _state.GetInt("current_turn", 0);
            var gameStatus = gameState == null ? null : gameState["game_status"] as JObject;
            if (SamsaraJson.GetString(gameStatus, "state", "") == "ended"
                && SamsaraJson.GetString(gameStatus, "winner", "") == "red"
                && currentTurn <= maxTurns)
            {
                return Result(true, 100, "在限定回合内获胜");
            }
            if (currentTurn >= maxTurns) return Result(false, 0, "回合已用完");
            var progress = Math.Min(100, (int)((maxTurns - currentTurn) / (double)maxTurns * 100));
            return Result(false, progress, "还剩" + (maxTurns - currentTurn) + "回合");
        }

        private static JObject CheckEvacuation(JObject objective, JObject gameState)
        {
            var targetRegion = objective["target_region"] as JObject ?? new JObject();
            var piecesSpec = objective["pieces"] as JArray ?? new JArray();
            var topLeft = targetRegion["top_left"] as JArray ?? new JArray { 0, 0 };
            var bottomRight = targetRegion["bottom_right"] as JArray ?? new JArray { 10, 10 };

            var evacuated = 0;
            var pieces = Pieces(gameState);
            if (pieces != null)
            {
                foreach (var item in pieces)
                {
                    var piece = item as JObject;
                    if (piece == null || !IsAlive(piece)) continue;
                    if (!piecesSpec.Contains(SamsaraJson.GetString(piece, "type", ""))) continue;
                    var pos = piece["position"];
                    if (PositionAt(pos, 0) >= PositionAt(topLeft, 0) && PositionAt(pos, 0) <= PositionAt(bottomRight, 0)
                        && PositionAt(pos, 1) >= PositionAt(topLeft, 1) && PositionAt(pos, 1) <= PositionAt(bottomRight, 1))
                    {
                        evacuated++;
                    }
                }
            }
            var progress = piecesSpec.Count > 0
                ? Math.Min(100, (int)(evacuated / (double)piecesSpec.Count * 100))
                : 0;
            return Result(evacuated >= piecesSpec.Count, progress,
                evacuated + "/" + piecesSpec.Count + "个棋子已撤离");
        }

        private static JObject CheckBoardCoverage(JObject objective, JObject gameState)
        {
            var targetPct = SamsaraJson.GetInt(objective, "target_percentage", 60);
            const int boardWidth = 9;
            const int boardHeight = 10;
            var totalCells = boardWidth * boardHeight;
            var occupied = 0;
            var pieces = Pieces(gameState);
            if (pieces != null)
            {
                foreach (var item in pieces)
                {
                    var piece = item as JObject;
                    if (piece != null && IsAlive(piece) && Side(piece) == "red") occupied++;
                }
            }
            var coverage = Math.Min(100, (int)(occupied / (double)totalCells * 100));
            return Result(coverage >= targetPct, coverage, "覆盖率" + coverage + "%");
        }

        private static JObject CheckColorCoverage(JObject objective, JObject gameState)
        {
            var targetPct = SamsaraJson.GetInt(objective, "target_percentage", 75);
            const int boardWidth = 9;
            const int boardHeight = 10;
            var darkCells = 0;
            var litCells = 0;
            var pieces = Pieces(gameState);
            for (var y = 0; y < boardHeight; y++)
            {
                for (var x = 0; x < boardWidth; x++)
                {
                    if ((x + y) % 2 != 1) continue;
                    darkCells++;
                    if (pieces == null) continue;
                    foreach (var item in pieces)
                    {
                        var piece = item as JObject;
                        if (piece != null && IsAlive(piece) && PositionEquals(piece["position"], x, y))
                        {
                            litCells++;
                            break;
                        }
                    }
                }
            }
            var coverage = darkCells > 0 ? Math.Min(100, (int)(litCells / (double)darkCells * 100)) : 0;
            return Result(coverage >= targetPct, coverage, "暗色区域覆盖率" + coverage + "%");
        }

        private JObject CheckSurvival(JObject objective, JObject gameState)
        {
            var minPieces = SamsaraJson.GetInt(objective, "min_pieces", 3);
            var maxTurns = SamsaraJson.GetInt(objective, "max_turns", 20);
            var currentTurn = _state.GetInt("current_turn", 0);
            var aliveCount = 0;
            var pieces = Pieces(gameState);
            if (pieces != null)
            {
                foreach (var item in pieces)
                {
                    var piece = item as JObject;
                    if (piece != null && IsAlive(piece) && Side(piece) == "red") aliveCount++;
                }
            }
            if (currentTurn >= maxTurns && aliveCount >= minPieces) return Result(true, 100, "生存成功");
            if (aliveCount < minPieces) return Result(false, 0, "棋子不足");
            var progress = Math.Min(100, (int)(currentTurn / (double)maxTurns * 100));
            return Result(false, progress, "还剩" + (maxTurns - currentTurn) + "回合");
        }

        private static JObject CheckAssassination(JObject objective, JObject gameState)
        {
            var targetPiece = SamsaraJson.GetString(objective, "target_piece", "");
            var pieces = Pieces(gameState);
            if (pieces != null)
            {
                foreach (var item in pieces)
                {
                    var piece = item as JObject;
                    if (piece != null
                        && SamsaraJson.GetString(piece, "type", "") == targetPiece
                        && Side(piece) == "black"
                        && !IsAlive(piece))
                    {
                        return Result(true, 100, "刺杀成功");
                    }
                }
            }
            return Result(false, 0, "目标仍存活");
        }

        private JObject CheckCompound(JObject objective, JObject gameState)
        {
            var op = SamsaraJson.GetString(objective, "operator", "AND");
            var conditions = objective["conditions"] as JArray ?? new JArray();
            var results = new List<JObject>();
            foreach (var condition in conditions) results.Add(Check(condition as JObject, gameState));

            bool completed;
            if (op == "AND")
            {
                completed = true;
                foreach (var r in results) if (!SamsaraJson.GetBool(r, "completed", false)) { completed = false; break; }
            }
            else if (op == "OR")
            {
                completed = false;
                foreach (var r in results) if (SamsaraJson.GetBool(r, "completed", false)) { completed = true; break; }
            }
            else
            {
                completed = false;
            }

            var totalProgress = 0.0;
            var messages = new List<string>();
            foreach (var r in results)
            {
                totalProgress += SamsaraJson.GetInt(r, "progress", 0);
                messages.Add(SamsaraJson.GetString(r, "message", ""));
            }
            var avgProgress = results.Count > 0 ? totalProgress / results.Count : 0.0;

            var subResults = new JArray();
            foreach (var r in results) subResults.Add(r);

            return new JObject
            {
                ["completed"] = completed,
                ["progress"] = avgProgress,
                ["message"] = string.Join("; ", messages),
                ["sub_results"] = subResults,
            };
        }
    }
}