using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Orchestration
{
    /// <summary>
    /// OrchestratorEngine 的各分类 action handler（A / A2 / B / C / C+ / D / D2）。
    /// 逐段对齐 ai_orchestrator_base.py 的对应方法。
    /// </summary>
    public sealed partial class OrchestratorEngine
    {
        // ── B 类 prompt 文案块 ──
        const string BTypeHintLead = "如果修改涉及棋子type字段，必须使用以下正确的type名称：";
        const string BTypeHintDoc = @"- 兵/卒 → ""soldier""
- 车/車 → ""chariot""
- 马/馬 → ""horse""
- 象/相 → ""elephant""
- 士/仕 → ""advisor""
- 将/帅 → ""general""
- 炮/砲 → ""cannon""

绝对禁止使用 pawn、rook、bishop、knight、king、queen 等国际象棋术语！";
        const string BRotateHintDoc = "board配置包括：width/height互换、palace坐标旋转、river_line位置调整";
        const string BTransformNameDoc = @"name必须与新type对应：红方棋子用红方名称，黑方棋子用黑方名称
  - chariot(车) → 红方""車"" / 黑方""車""
  - horse(马) → 红方""馬"" / 黑方""馬""
  - elephant(象) → 红方""相"" / 黑方""象""
  - advisor(士) → 红方""仕"" / 黑方""士""
  - general(将) → 红方""帥"" / 黑方""將""
  - cannon(炮) → 红方""炮"" / 黑方""砲""
  - soldier(兵) → 红方""兵"" / 黑方""卒""";

        static string Dump(JToken token, bool indent)
        {
            if (token == null) return "null";
            return token.ToString(indent ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None);
        }

        // ══════════════════════════════════════════════════════════════
        // A 类：机制修改（A1 硬编码 + A2 灵活编码）
        // ══════════════════════════════════════════════════════════════

        /// <summary>A类：机制修改（A1硬编码 + A2灵活编码混合模式）。</summary>
        public async Task<JObject> HandleActionAAsync(
            JObject intent, JObject configs, JObject logEntry, CancellationToken cancellationToken = default)
        {
            if (logEntry == null) logEntry = new JObject();

            var actions = intent["actions"] as JArray;
            if (actions == null || actions.Count == 0)
            {
                var instruction = intent["structured_instruction"] as JObject ?? new JObject();
                actions = new JArray();
                actions.Add(new JObject
                {
                    ["type"] = "A",
                    ["subtype"] = "A1",
                    ["target_files"] = intent["target_files"] ?? new JArray(),
                    ["instruction"] = instruction,
                    ["prompt"] = JsonHelpers.GetString(intent, "next_ai_prompt"),
                });
            }

            var a1Actions = new List<JObject>();
            var a2Actions = new List<JObject>();
            foreach (var token in actions)
            {
                if (!(token is JObject act)) continue;
                if (JsonHelpers.GetString(act, "type") != "A") continue;
                var subtype = JsonHelpers.GetString(act, "subtype");
                var instruction = act["instruction"] as JObject ?? new JObject();
                var actionName = JsonHelpers.GetString(instruction, "action");
                if (subtype == "A2" || ContainsString(A2ActionNames, actionName))
                    a2Actions.Add(act);
                else
                    a1Actions.Add(act);
            }

            var allResults = new JArray();
            var modifiedConfigsAll = new JObject();

            void Absorb(JObject result)
            {
                allResults.Add(result);
                if (!(result["modified_configs"] is JObject mc)) return;
                foreach (var prop in mc.Properties())
                {
                    if (prop.Name == "board_state" && modifiedConfigsAll.ContainsKey("board_state"))
                        DeepMergeBoardState(modifiedConfigsAll["board_state"] as JObject, prop.Value as JObject);
                    else
                        modifiedConfigsAll[prop.Name] = prop.Value.DeepClone();
                }
            }

            foreach (var act in a1Actions)
                Absorb(HandleA1Hardcoded(act, configs, intent));

            if (a2Actions.Count > 0)
            {
                ThinkingStage = "code";
                var tasks = new List<Task<JObject>>();
                foreach (var act in a2Actions)
                    tasks.Add(HandleA2MechanismAsync(act, configs, logEntry, cancellationToken));
                var a2Results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in a2Results) Absorb(result);
            }

            var successCount = 0;
            var errorCount = 0;
            foreach (var token in allResults)
            {
                if (!(token is JObject r)) continue;
                if (JsonHelpers.GetBool(r, "success")) successCount++;
                else if (JsonHelpers.GetString(r, "type") != "rejected") errorCount++;
            }

            if (successCount > 0 && errorCount == 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "applied",
                    ["message"] = JsonHelpers.GetString(intent, "response_to_player", "机制修改已应用"),
                    ["modified_configs"] = modifiedConfigsAll,
                    ["classification"] = "A",
                    ["action_results"] = allResults,
                };
            }
            if (successCount > 0 && errorCount > 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "partial",
                    ["message"] = JsonHelpers.GetString(intent, "response_to_player", "部分修改成功"),
                    ["modified_configs"] = modifiedConfigsAll,
                    ["classification"] = "A",
                    ["action_results"] = allResults,
                };
            }

            var messages = new List<string>();
            foreach (var token in allResults)
                if (token is JObject r && !string.IsNullOrEmpty(JsonHelpers.GetString(r, "message")))
                    messages.Add(JsonHelpers.GetString(r, "message"));

            return new JObject
            {
                ["success"] = false,
                ["type"] = errorCount == 0 ? "rejected" : "error",
                ["message"] = messages.Count > 0 ? string.Join("\n", messages) : "操作失败",
                ["classification"] = "A",
                ["action_results"] = allResults,
            };
        }

        /// <summary>A1子类：硬编码处理（悔棋 / 设定输赢）。</summary>
        JObject HandleA1Hardcoded(JObject action, JObject configs, JObject intent)
        {
            var instruction = action["instruction"] as JObject ?? new JObject();
            var actionName = JsonHelpers.GetString(instruction, "action");
            var parameters = instruction["parameters"] as JObject ?? new JObject();

            var board = (JObject)GetConfigOrEmpty(configs, "board_state");

            if (actionName == "undo_move")
            {
                var steps = JsonHelpers.GetInt(parameters, "steps", 1);
                var history = board["move_history"] as JArray ?? new JArray();
                if (history.Count < steps)
                {
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "rejected",
                        ["message"] = $"没有足够的步数可悔（当前历史{history.Count}步）",
                    };
                }
                for (int i = 0; i < steps; i++)
                {
                    if (history.Count == 0) break;
                    var last = history[history.Count - 1];
                    history.RemoveAt(history.Count - 1);
                    if (last is JObject lastObj) A1UndoStep(board, lastObj);
                }
                board["move_history"] = history;
                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "applied",
                    ["message"] = JsonHelpers.GetString(intent, "response_to_player", $"已悔{steps}步棋"),
                    ["modified_configs"] = new JObject { ["board_state"] = board },
                    ["classification"] = "A",
                    ["subtype"] = "A1",
                };
            }

            if (actionName == "set_winner")
            {
                var winner = JsonHelpers.GetString(parameters, "winner", WinnerDefault);
                if (!(board["game_status"] is JObject gameStatus))
                {
                    gameStatus = new JObject();
                    board["game_status"] = gameStatus;
                }
                gameStatus["state"] = "ended";
                gameStatus["winner"] = winner;
                gameStatus["win_condition"] = "玩家指令";
                if (gameStatus["custom_rules_active"] == null) gameStatus["custom_rules_active"] = new JArray();
                var message = A1WinnerMessage(winner, intent);
                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "applied",
                    ["message"] = message,
                    ["modified_configs"] = new JObject { ["board_state"] = board },
                    ["classification"] = "A",
                    ["subtype"] = "A1",
                };
            }

            return new JObject
            {
                ["success"] = false,
                ["type"] = "rejected",
                ["message"] = $"无法识别的A1操作: {actionName}",
                ["classification"] = "A",
                ["subtype"] = "A1",
            };
        }

        /// <summary>单步悔棋：恢复棋子状态并切换回合（移动制棋类默认实现）。</summary>
        void A1UndoStep(JObject board, JObject last)
        {
            var piece = FindPiece(board, JsonHelpers.GetString(last, "piece_id"));
            if (piece != null && last["from"] != null)
                piece["position"] = last["from"].DeepClone();

            var capturedId = JsonHelpers.GetString(last, "captured");
            if (!string.IsNullOrEmpty(capturedId))
            {
                var cap = FindPiece(board, capturedId);
                if (cap != null) cap["is_alive"] = true;
            }

            var currentTurn = JsonHelpers.GetString(board, "current_turn");
            board["current_turn"] = SideToggle.TryGetValue(currentTurn, out var toggled) ? toggled : WinnerDefault;
        }

        /// <summary>set_winner 的结果消息钩子。</summary>
        string A1WinnerMessage(string winner, JObject intent)
            => JsonHelpers.GetString(intent, "response_to_player", $"已设置{winner}方获胜");

        /// <summary>A2子类：灵活编码机制修改（通过 CodeAI 修改 board_state/rules）。</summary>
        async Task<JObject> HandleA2MechanismAsync(
            JObject action, JObject configs, JObject logEntry, CancellationToken cancellationToken)
        {
            var codeGenLog = logEntry["code_generation"] as JObject;
            if (codeGenLog == null) { codeGenLog = new JObject(); logEntry["code_generation"] = codeGenLog; }

            var instruction = action["instruction"] as JObject ?? new JObject();
            var nextPrompt = JsonHelpers.GetString(action, "prompt");
            var targetFiles = action["target_files"] as JArray ?? new JArray();
            var actionName = JsonHelpers.GetString(instruction, "action");
            var parameters = instruction["parameters"] as JObject ?? new JObject();

            var needsBoardState = false;
            var needsRules = false;
            foreach (var token in targetFiles)
            {
                if (token.Type != JTokenType.String) continue;
                var f = token.Value<string>();
                if (f.Contains("board_state")) needsBoardState = true;
                if (f.Contains("rules")) needsRules = true;
            }
            if (actionName == "modify_personality" || actionName == "set_ai_personality") needsRules = true;
            if (ContainsString(A2BoardStateActions, actionName)) needsBoardState = true;
            if (!needsBoardState && !needsRules) needsBoardState = true;

            var boardState = GetConfigOrEmpty(configs, "board_state");
            var rules = GetConfigOrEmpty(configs, "rules");

            var targetsDesc = new List<string>();
            if (needsBoardState) targetsDesc.Add("board_state.json（mechanisms机制字段）");
            if (needsRules) targetsDesc.Add("rules.json（ai_difficulty.personality性格字段）");

            var userPrompt = "## 修改任务\n" + nextPrompt + "\n\n"
                + "## 具体操作\n"
                + $"- 动作: {actionName}\n"
                + $"- 目标: {JsonHelpers.GetString(instruction, "target")}\n"
                + $"- 参数: {Dump(parameters, false)}\n"
                + $"- 修改文件: {string.Join(", ", targetsDesc)}\n\n"
                + "## 机制原语说明（用于修改 board_state.json 的 mechanisms 字段）\n"
                + "你可以组合使用以下原语来实现各种游戏机制效果：\n\n"
                + "### skip_turns - 跳过回合（冻结）\n"
                + "格式: {\"side\": \"red|black\", \"remaining\": 回合数, \"reason\": \"说明\"}\n"
                + "效果：指定方跳过N回合（无法走棋）\n\n"
                + "### ai_control - AI接管\n"
                + "格式: {\"side\": \"red|black\", \"remaining\": 回合数, \"reason\": \"说明\"}\n"
                + "效果：指定方的N回合由AI代为走棋\n\n"
                + "### random_moves - 随机走棋\n"
                + "格式: {\"side\": \"red|black\", \"remaining\": 步数, \"reason\": \"说明\"}\n"
                + "效果：指定方接下来N步棋随机选择合法走法\n\n"
                + "### extra_turns - 额外回合\n"
                + "格式: {\"side\": \"red|black\", \"remaining\": 回合数, \"reason\": \"说明\"}\n"
                + "效果：指定方获得N次额外回合（连续走棋）\n\n"
                + "### move_limits - 每回合步数限制\n"
                + "格式: {\"side\": \"red|black\", \"limit\": 步数}\n"
                + "效果：指定方每回合可以走N步\n\n"
                + "## AI性格配置说明（用于修改 rules.json 的 ai_difficulty.personality 字段）\n"
                + "预设性格类型：\n"
                + "- normal: 正常平衡型\n"
                + "- aggressive: 激进进攻型（重视进攻，中心控制）\n"
                + "- defensive: 保守防守型（重视防守，将帅安全）\n"
                + "- random: 随机瞎下型（低搜索深度，高随机度）\n"
                + "- custom: 自定义型\n\n"
                + "性格参数：\n"
                + "- type: 预设性格类型\n"
                + "- aggressiveness: 进攻倾向 0.0-1.0\n"
                + "- conservatism: 保守程度 0.0-1.0\n"
                + "- randomness_override: 覆盖随机度 null或0.0-1.0\n"
                + "- depth_override: 覆盖搜索深度 null或正整数\n"
                + "- value_biases: 棋子价值偏差 {piece_type: bias_multiplier}\n"
                + "- custom_prompt: 自定义提示词（字符串或null）\n\n"
                + "## 当前 board_state.json 的 mechanisms 字段\n```json\n"
                + Dump(boardState["mechanisms"] ?? new JObject(), true) + "\n```\n\n"
                + "## 当前 rules.json 的 ai_difficulty.personality 字段\n```json\n"
                + Dump(rules["ai_difficulty"]?["personality"] ?? new JObject(), true) + "\n```\n\n"
                + "## 要求\n"
                + "0. **优先输出 JSON Patch 数组**（RFC 6902 格式），仅描述需要修改的字段。\n"
                + "   你可以同时修改 board_state.json 和 rules.json，用以下格式输出：\n"
                + "   {\n     \"board_state_patch\": [ ... ],\n     \"rules_patch\": [ ... ]\n   }\n"
                + "   如果无法生成 patch，再输出完整 JSON 对象。\n"
                + "1. 只修改需要修改的部分，保持其他部分不变\n"
                + "2. 确保JSON格式正确\n"
                + "3. 只输出JSON，不要输出其他内容";

            string resp;
            double elapsed;
            try
            {
                (resp, elapsed) = await CallDeepSeekAsync(
                    PromptBuilder.MechanismModifierSystem, userPrompt, 0.2, null, cancellationToken).ConfigureAwait(false);
                codeGenLog["success"] = true;
                codeGenLog["elapsed_time"] = elapsed;
                codeGenLog["raw_output"] = resp;
            }
            catch (Exception e)
            {
                codeGenLog["success"] = false;
                codeGenLog["error"] = e.Message;
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"AI生成失败: {e.Message}",
                    ["classification"] = "A",
                    ["subtype"] = "A2",
                };
            }

            var parsedToken = ExtractJsonToken(resp);
            if (parsedToken == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "AI输出解析失败：无法解析为JSON",
                    ["classification"] = "A",
                    ["subtype"] = "A2",
                };
            }

            var modifiedConfigs = new JObject();

            if (needsBoardState && parsedToken is JObject parsedObj)
            {
                var boardPatch = parsedObj["board_state_patch"] as JArray;
                if (boardPatch != null)
                {
                    var (valid, err) = ChessSage.Core.Json.JsonPatch.IsValid(boardPatch);
                    if (!valid)
                    {
                        return new JObject
                        {
                            ["success"] = false,
                            ["type"] = "error",
                            ["message"] = $"board_state_patch格式错误: {err}",
                            ["classification"] = "A",
                            ["subtype"] = "A2",
                        };
                    }
                    try
                    {
                        boardState = (JObject)ChessSage.Core.Json.JsonPatch.Apply(boardState, boardPatch);
                        modifiedConfigs["board_state"] = boardState;
                    }
                    catch (Exception e)
                    {
                        return new JObject
                        {
                            ["success"] = false,
                            ["type"] = "error",
                            ["message"] = $"应用board_state_patch失败: {e.Message}",
                            ["classification"] = "A",
                            ["subtype"] = "A2",
                        };
                    }
                }
                else if (parsedObj["board_state"] is JObject fullBoardState)
                {
                    var diffPatch = ChessSage.Core.Json.JsonPatch.GenerateDiff(boardState, fullBoardState);
                    boardState = (JObject)ChessSage.Core.Json.JsonPatch.Apply(boardState, diffPatch);
                    modifiedConfigs["board_state"] = boardState;
                }
            }

            if (needsRules && parsedToken is JObject rulesParsed)
            {
                var rulesPatch = rulesParsed["rules_patch"] as JArray;
                if (rulesPatch != null)
                {
                    var (valid, err) = ChessSage.Core.Json.JsonPatch.IsValid(rulesPatch);
                    if (!valid)
                    {
                        return new JObject
                        {
                            ["success"] = false,
                            ["type"] = "error",
                            ["message"] = $"rules_patch格式错误: {err}",
                            ["classification"] = "A",
                            ["subtype"] = "A2",
                        };
                    }
                    try
                    {
                        rules = (JObject)ChessSage.Core.Json.JsonPatch.Apply(rules, rulesPatch);
                        modifiedConfigs["rules"] = rules;
                    }
                    catch (Exception e)
                    {
                        return new JObject
                        {
                            ["success"] = false,
                            ["type"] = "error",
                            ["message"] = $"应用rules_patch失败: {e.Message}",
                            ["classification"] = "A",
                            ["subtype"] = "A2",
                        };
                    }
                }
                else if (rulesParsed["rules"] is JObject fullRules)
                {
                    var diffPatch = ChessSage.Core.Json.JsonPatch.GenerateDiff(rules, fullRules);
                    rules = (JObject)ChessSage.Core.Json.JsonPatch.Apply(rules, diffPatch);
                    modifiedConfigs["rules"] = rules;
                }
            }

            if (!modifiedConfigs.HasValues && parsedToken is JArray singlePatch)
            {
                var (valid, _) = ChessSage.Core.Json.JsonPatch.IsValid(singlePatch);
                if (valid)
                {
                    try
                    {
                        if (needsBoardState)
                        {
                            boardState = (JObject)ChessSage.Core.Json.JsonPatch.Apply(boardState, singlePatch);
                            modifiedConfigs["board_state"] = boardState;
                        }
                        else if (needsRules)
                        {
                            rules = (JObject)ChessSage.Core.Json.JsonPatch.Apply(rules, singlePatch);
                            modifiedConfigs["rules"] = rules;
                        }
                    }
                    catch
                    {
                        // 忽略：无有效修改时在下方统一报错
                    }
                }
            }

            if (!modifiedConfigs.HasValues)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "未检测到有效的修改操作",
                    ["classification"] = "A",
                    ["subtype"] = "A2",
                };
            }

            if (modifiedConfigs["board_state"] is JObject bs)
            {
                var (valid, err) = SchemaValidator.ValidateBoardState(bs);
                if (!valid)
                {
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "error",
                        ["message"] = $"board_state验证失败: {err}",
                        ["classification"] = "A",
                        ["subtype"] = "A2",
                    };
                }
            }
            if (modifiedConfigs["rules"] is JObject rl)
            {
                var (valid, err) = SchemaValidator.ValidateRules(rl);
                if (!valid)
                {
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "error",
                        ["message"] = $"rules验证失败: {err}",
                        ["classification"] = "A",
                        ["subtype"] = "A2",
                    };
                }
            }

            var totalOps = 0;
            if (needsBoardState && parsedToken is JObject bo && bo["board_state_patch"] is JArray bpa) totalOps += bpa.Count;
            if (needsRules && parsedToken is JObject ro && ro["rules_patch"] is JArray rpa) totalOps += rpa.Count;
            codeGenLog["patch_mode"] = totalOps > 0 ? "patch" : "diff";
            if (totalOps > 0) codeGenLog["patch_operations"] = totalOps;

            return new JObject
            {
                ["success"] = true,
                ["type"] = "applied",
                ["message"] = "机制修改已应用",
                ["modified_configs"] = modifiedConfigs,
                ["classification"] = "A",
                ["subtype"] = "A2",
            };
        }

        // ══════════════════════════════════════════════════════════════
        // B 类：棋盘变换
        // ══════════════════════════════════════════════════════════════

        /// <summary>B类：棋盘变换（带日志）。</summary>
        async Task<JObject> HandleActionBWithLogAsync(
            JObject intent, JObject configs, JObject logEntry, CancellationToken cancellationToken)
        {
            var boardState = configs["board_state"] as JObject ?? new JObject();
            var instruction = intent["structured_instruction"] as JObject ?? new JObject();
            var nextPrompt = JsonHelpers.GetString(intent, "next_ai_prompt");
            var actionStr = JsonHelpers.GetString(instruction, "action");

            string[] bActions = { "add", "remove", "move", "rotate", "transform", "modify",
                                  "copy", "duplicate", "teleport", "swap", "flip", "create" };
            if (!string.IsNullOrEmpty(actionStr) && !ContainsAny(actionStr.ToLowerInvariant(), bActions))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "rejected",
                    ["message"] = $"B类不处理此动作: {actionStr}",
                    ["classification"] = "B",
                };
            }

            var actionType = DetectActionType(actionStr);
            if (!(logEntry["validation"] is JObject validation))
            {
                validation = new JObject();
                logEntry["validation"] = validation;
            }
            validation["action_type"] = actionType;

            var parameters = instruction["parameters"] as JObject ?? new JObject();
            if (parameters.ContainsKey("new_type"))
            {
                var originalType = parameters["new_type"]?.Value<string>() ?? "";
                var correctedType = PieceTypeMapping.TryGetValue(originalType.ToLowerInvariant(), out var mapped)
                    ? mapped : originalType;
                if (correctedType != originalType)
                {
                    parameters["new_type"] = correctedType;
                    logEntry["type_correction"] = $"{originalType} -> {correctedType}";
                }
            }

            var actionTypeEmphasis = "";
            if (actionType == "add")
            {
                actionTypeEmphasis = "\n## ⚠️ 操作类型特别提醒（添加棋子）\n"
                    + "- 绝对不能修改或删除已有棋子，只能添加新棋子\n"
                    + "- 所有原有棋子必须完整保留，包括它们的ID、type、side、name、position等所有属性\n"
                    + "- 新棋子必须添加到空位置，不能与现有存活棋子位置重叠\n";
            }
            else if (actionType == "remove")
            {
                actionTypeEmphasis = "\n## ⚠️ 操作类型特别提醒（删除棋子）\n"
                    + "- 使用is_alive=false标记删除，不从数组中移除棋子对象\n"
                    + "- 棋子的其他所有属性（id、type、side、name、position等）必须保持不变\n"
                    + "- 不能从pieces数组中删除任何棋子对象\n";
            }
            else if (actionType == "move")
            {
                actionTypeEmphasis = "\n## ⚠️ 操作类型特别提醒（移动棋子）\n"
                    + "- 只修改position字段，其他属性保持不变\n"
                    + "- 棋子的id、type、side、name、is_alive等核心属性绝对不能修改\n"
                    + "- 不能添加或删除任何棋子\n";
            }
            else if (actionType == "rotate")
            {
                actionTypeEmphasis = "\n## ⚠️ 操作类型特别提醒（旋转棋盘）\n"
                    + "- 必须同时修改board配置和棋子坐标，不能只改棋子\n"
                    + $"- {BRotateHintDoc}\n"
                    + "- 所有棋子的坐标都需要按相同旋转规则进行转换\n"
                    + "- 棋子的其他属性（id、type、side、name、is_alive等）保持不变\n";
            }
            else if (actionType == "transform")
            {
                actionTypeEmphasis = "\n## ⚠️ 操作类型特别提醒（棋子类型变换）\n"
                    + "- **必须同时修改 type 和 name 两个字段，缺一不可！绝对不能只改type不改name**\n"
                    + $"- {BTransformNameDoc}\n"
                    + "- id、side、position、is_alive 等核心属性绝对不能修改\n"
                    + "- 棋子数量不能变化（不能新增也不能删除棋子）\n"
                    + "- 使用 replace 操作修改 /pieces/{index}/type 和 /pieces/{index}/name\n"
                    + "- 注意：数组索引从 0 开始\n";
            }

            var userPrompt = "## 修改任务\n" + nextPrompt + "\n\n"
                + "## 具体操作\n"
                + $"- 动作: {JsonHelpers.GetString(instruction, "action")}\n"
                + $"- 目标: {JsonHelpers.GetString(instruction, "target")}\n"
                + $"- 参数: {Dump(parameters, false)}\n"
                + $"- 约束: {Dump(instruction["constraints"] ?? new JArray(), false)}\n"
                + $"- 操作类型: {actionType}\n"
                + actionTypeEmphasis + "\n"
                + "## ⚠️ 重要提醒\n" + BTypeHintLead + "\n" + BTypeHintDoc + "\n\n"
                + "## 当前board_state.json完整内容\n```json\n" + Dump(boardState, true) + "\n```\n\n"
                + "## 要求\n"
                + "0. **优先输出 JSON Patch 数组**（RFC 6902 格式），仅描述需要修改的字段，避免输出完整棋盘。格式示例：\n"
                + "   [{\"op\": \"replace\", \"path\": \"/pieces/0/position\", \"value\": [4, 5]}]\n"
                + "   如果无法生成 patch，再输出完整 JSON。\n"
                + "1. 请根据上述任务修改board_state.json\n"
                + "2. 输出修改后的完整board_state.json（包含所有棋子，不要省略任何棋子）\n"
                + "3. 只修改需要修改的部分，保持其他部分不变\n"
                + "4. 确保JSON格式正确\n"
                + "5. 只输出JSON，不要输出其他内容";

            var (newBoard, errorMsg) = await CallCodeAiWithValidationAsync(
                PromptBuilder.BoardTransformerSystem, userPrompt, "board_state", boardState,
                logEntry, 0.1, actionType, false, null, cancellationToken).ConfigureAwait(false);

            if (newBoard == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = string.IsNullOrEmpty(errorMsg) ? "AI生成失败" : errorMsg,
                };
            }

            var (valid, err) = ValidateBoard(newBoard, boardState, actionType);
            if (!valid)
            {
                validation["schema_error"] = err;
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"验证失败: {err}",
                };
            }

            var oldPieces = CountAlive(boardState);
            var newPieces = CountAlive(newBoard);
            if (newPieces != oldPieces)
            {
                if (!(logEntry["warnings"] is JArray warnings))
                {
                    warnings = new JArray();
                    logEntry["warnings"] = warnings;
                }
                warnings.Add($"棋子数量变化: 原{oldPieces}个，现{newPieces}个");
            }

            return new JObject
            {
                ["success"] = true,
                ["type"] = "applied",
                ["message"] = JsonHelpers.GetString(intent, "response_to_player", "棋盘已变换"),
                ["modified_configs"] = new JObject { ["board_state"] = newBoard },
                ["classification"] = "B",
            };
        }

        // ══════════════════════════════════════════════════════════════
        // C 类：规则修改
        // ══════════════════════════════════════════════════════════════

        /// <summary>C类：规则修改（带日志）。</summary>
        async Task<JObject> HandleActionCWithLogAsync(
            JObject intent, JObject configs, JObject logEntry, CancellationToken cancellationToken)
        {
            var sideConfig = SideConfigFor(intent);
            var targetConfigName = sideConfig.configName;
            var sideLabel = sideConfig.label;
            var configFilename = targetConfigName + ".json";

            var pieces = GetConfigOrEmpty(configs, targetConfigName);
            var instruction = intent["structured_instruction"] as JObject ?? new JObject();
            var nextPrompt = JsonHelpers.GetString(intent, "next_ai_prompt");
            var actionStr = JsonHelpers.GetString(instruction, "action");

            if (!string.IsNullOrEmpty(actionStr) && !ContainsAny(actionStr.ToLowerInvariant(), CActionKeywords))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "rejected",
                    ["message"] = $"C类不处理此动作: {actionStr}",
                    ["classification"] = "C",
                };
            }

            var ruleChangeEmphasis = "\n## ⚠️ 规则修改强约束\n"
                + "1. 你必须对规则进行实质性修改，不能输出与输入相同的规则\n"
                + "2. 如果用户要求修改某方的棋子（如\"红方的马\"），直接修改当前文件\n"
                + "3. 如果用户要求\"可以移动到任意一格\"，可以在moves中添加自由移动\n"
                + "4. 修改后必须确保规则与修改前不同\n"
                + "5. 如果规则未变化，校验层会检测到并触发重试\n";

            var userPrompt = "## 修改任务\n" + nextPrompt + "\n\n"
                + "## 具体操作\n"
                + $"- 动作: {JsonHelpers.GetString(instruction, "action")}\n"
                + $"- 目标: {JsonHelpers.GetString(instruction, "target")}\n"
                + $"- 参数: {Dump(instruction["parameters"] ?? new JObject(), false)}\n"
                + $"- 约束: {Dump(instruction["constraints"] ?? new JArray(), false)}\n\n"
                + ruleChangeEmphasis + "\n"
                + $"## 当前{sideLabel}{configFilename}完整内容\n```json\n" + Dump(pieces, true) + "\n```\n\n"
                + "## 要求\n"
                + "0. **优先输出 JSON Patch 数组**（RFC 6902 格式），仅描述需要修改的字段，避免输出完整规则。格式示例：\n"
                + "   [{\"op\": \"replace\", \"path\": \"/pieces/elephant/moves/0/where\", \"value\": []}]\n"
                + "   如果无法生成 patch，再输出完整 JSON。\n"
                + $"1. 请根据上述任务修改{configFilename}\n"
                + $"2. 输出修改后的完整{configFilename}（包含所有棋子规则，不要省略）\n"
                + "3. 只修改需要修改的部分，保持其他部分不变\n"
                + "4. 确保JSON格式正确\n"
                + "5. 只输出JSON，不要输出其他内容";

            var targetType = instruction["target"] != null && instruction["target"].Type == JTokenType.String
                ? instruction["target"].Value<string>() : null;

            var (newPieces, errorMsg) = await CallCodeAiWithValidationAsync(
                PromptBuilder.RuleModifierSystem, userPrompt, targetConfigName, pieces,
                logEntry, 0.1, "unknown", true, targetType, cancellationToken).ConfigureAwait(false);

            if (newPieces == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = string.IsNullOrEmpty(errorMsg) ? "AI生成失败" : errorMsg,
                };
            }

            var board = GetConfigOrEmpty(configs, "board_state");
            var actionDesc = JsonHelpers.GetString(instruction, "action");
            if (!string.IsNullOrEmpty(actionDesc) && board["game_status"] is JObject gameStatus)
            {
                if (!(gameStatus["custom_rules_active"] is JArray active))
                {
                    active = new JArray();
                    gameStatus["custom_rules_active"] = active;
                }
                active.Add($"[{sideLabel}]{actionDesc}");
            }

            return new JObject
            {
                ["success"] = true,
                ["type"] = "applied",
                ["message"] = JsonHelpers.GetString(intent, "response_to_player", $"{sideLabel}规则已修改"),
                ["modified_configs"] = new JObject
                {
                    [targetConfigName] = newPieces,
                    ["board_state"] = board,
                },
                ["classification"] = "C",
            };
        }

        // ══════════════════════════════════════════════════════════════
        // C+ 类：自定义棋子创建
        // ══════════════════════════════════════════════════════════════

        /// <summary>C+类：自定义棋子创建（带日志）。</summary>
        async Task<JObject> HandleActionCpWithLogAsync(
            JObject intent, JObject configs, JObject logEntry, CancellationToken cancellationToken)
        {
            var codeGenLog = logEntry["code_generation"] as JObject;
            if (codeGenLog == null) { codeGenLog = new JObject(); logEntry["code_generation"] = codeGenLog; }

            var sideConfig = SideConfigFor(intent);
            var targetConfigName = sideConfig.configName;
            var sideLabel = sideConfig.label;
            var configFilename = targetConfigName + ".json";

            var pieces = GetConfigOrEmpty(configs, targetConfigName);
            var boardState = GetConfigOrEmpty(configs, "board_state");

            var instruction = intent["structured_instruction"] as JObject ?? new JObject();
            var nextPrompt = JsonHelpers.GetString(intent, "next_ai_prompt");
            var actionStr = JsonHelpers.GetString(instruction, "action");

            string[] cpActions = { "create_custom_piece", "create", "invent", "make", "add_new_piece", "new_piece" };
            if (!string.IsNullOrEmpty(actionStr) && !ContainsAny(actionStr.ToLowerInvariant(), cpActions))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "rejected",
                    ["message"] = $"C+类不处理此动作: {actionStr}",
                    ["classification"] = "C+",
                };
            }

            var parameters = instruction["parameters"] as JObject ?? new JObject();

            var (boundW, boundH) = CpBoardBounds(boardState, null);
            var boardDesc = string.Format(CpBoardDesc, boundW, boundH);

            var existingTypes = new List<string>(CpPredefinedTypes);
            if (pieces["custom_pieces"] is JArray customPieces)
                foreach (var token in customPieces)
                    if (token is JObject cp) existingTypes.Add(JsonHelpers.GetString(cp, "type"));
            existingTypes.Sort(StringComparer.Ordinal);

            var userPrompt = "## 创建任务\n" + nextPrompt + "\n\n"
                + "## 具体操作\n"
                + $"- 动作: {JsonHelpers.GetString(instruction, "action")}\n"
                + $"- 目标: {JsonHelpers.GetString(instruction, "target")}\n"
                + $"- 参数: {Dump(parameters, false)}\n"
                + $"- 约束: {Dump(instruction["constraints"] ?? new JArray(), false)}\n"
                + $"## 当前 {sideLabel}{configFilename} 完整内容\n```json\n" + Dump(pieces, true) + "\n```\n\n"
                + "## 当前 board_state.json 摘要\n" + boardDesc + "\n"
                + $"- 现有棋子数量: {CountAlive(boardState)}\n\n"
                + "## 已存在的棋子类型（新棋子type不能与这些冲突）\n"
                + string.Join(", ", existingTypes) + "\n"
                + "## 要求\n"
                + "1. 根据玩家描述创建新棋子，输出包含 pieces_patch 和 board_state_patch 两个字段的JSON对象\n"
                + "2. 新棋子的 type 字段必须是英文标识符，不能与已存在的类型冲突\n"
                + "3. 移动规则必须基于 jump/ray 原语组合生成\n"
                + "4. 复合移动能力使用多个move定义\n"
                + "5. 棋子位置必须在棋盘范围内且不与现有棋子重叠\n"
                + "6. 只输出JSON对象，不要输出其他内容";

            ThinkingStage = "code";
            string resp;
            double elapsed;
            try
            {
                (resp, elapsed) = await CallDeepSeekAsync(
                    PromptBuilder.PieceCreatorSystem, userPrompt, 0.1, null, cancellationToken).ConfigureAwait(false);
                codeGenLog["success"] = true;
                codeGenLog["elapsed_time"] = elapsed;
                codeGenLog["raw_output"] = resp;
            }
            catch (Exception e)
            {
                codeGenLog["success"] = false;
                codeGenLog["error"] = e.Message;
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"AI生成失败: {e.Message}",
                    ["classification"] = "C+",
                };
            }

            var parsed = ExtractJson(resp);
            if (parsed == null)
            {
                codeGenLog["parse_error"] = "无法解析为JSON对象";
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "AI输出解析失败：期望包含 pieces_patch 和 board_state_patch 的JSON对象",
                    ["classification"] = "C+",
                };
            }

            var piecesPatch = parsed["pieces_patch"] as JArray;
            var boardStatePatch = parsed["board_state_patch"] as JArray;
            if (piecesPatch == null || boardStatePatch == null)
            {
                codeGenLog["parse_error"] = "patch字段类型错误";
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "AI输出格式错误：pieces_patch 和 board_state_patch 必须是数组",
                    ["classification"] = "C+",
                };
            }

            codeGenLog["patch_mode"] = "patch";
            codeGenLog["pieces_patch_operations"] = piecesPatch.Count;
            codeGenLog["board_state_patch_operations"] = boardStatePatch.Count;
            var combinedPatch = new JArray();
            foreach (var t in piecesPatch) combinedPatch.Add(t.DeepClone());
            foreach (var t in boardStatePatch) combinedPatch.Add(t.DeepClone());
            codeGenLog["patch_operations_detail"] = combinedPatch;

            JObject newPieces;
            JObject newBoardState;
            try
            {
                var (validPr, errPr) = ChessSage.Core.Json.JsonPatch.IsValid(piecesPatch);
                if (!validPr)
                {
                    codeGenLog["patch_apply_error"] = $"pieces_patch格式错误: {errPr}";
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "error",
                        ["message"] = $"pieces_patch格式错误: {errPr}",
                        ["classification"] = "C+",
                    };
                }
                var (validBs, errBs) = ChessSage.Core.Json.JsonPatch.IsValid(boardStatePatch);
                if (!validBs)
                {
                    codeGenLog["patch_apply_error"] = $"board_state_patch格式错误: {errBs}";
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "error",
                        ["message"] = $"board_state_patch格式错误: {errBs}",
                        ["classification"] = "C+",
                    };
                }

                newPieces = (JObject)ChessSage.Core.Json.JsonPatch.Apply(pieces, piecesPatch);
                newBoardState = (JObject)ChessSage.Core.Json.JsonPatch.Apply(boardState, boardStatePatch);
            }
            catch (Exception e)
            {
                codeGenLog["patch_apply_error"] = e.Message;
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"应用patch失败: {e.Message}",
                    ["classification"] = "C+",
                };
            }

            var newCustomPieces = newPieces["custom_pieces"] as JArray ?? new JArray();
            var oldCustomPieces = pieces["custom_pieces"] as JArray ?? new JArray();
            var addedPieces = new List<JObject>();
            if (newCustomPieces.Count > oldCustomPieces.Count)
                for (int i = oldCustomPieces.Count; i < newCustomPieces.Count; i++)
                    if (newCustomPieces[i] is JObject cp) addedPieces.Add(cp);
            else
                foreach (var token in newCustomPieces)
                    if (token is JObject cpItem) addedPieces.Add(cpItem);

            var validationErrors = new List<string>();
            foreach (var cp in addedPieces)
            {
                var cpType = JsonHelpers.GetString(cp, "type");
                if (CpPredefinedTypes.Contains(cpType))
                    validationErrors.Add($"新棋子type '{cpType}' 与预定义类型冲突");

                if (cp["moves"] is JArray moves)
                {
                    foreach (var token in moves)
                    {
                        if (!(token is JObject move)) continue;
                        var kind = JsonHelpers.GetString(move, "kind");
                        if (!ContainsString(CpPrimitives, kind))
                            validationErrors.Add($"新棋子 '{cpType}' 的 move.kind '{kind}' 不是合法值（合法值: {CpPrimitivesDesc}）");
                    }
                }
            }

            var newBoardPieces = newBoardState["pieces"] as JArray ?? new JArray();
            var existingIds = new HashSet<string>();
            if (boardState["pieces"] is JArray oldPiecesArr)
                foreach (var token in oldPiecesArr)
                    if (token is JObject p) existingIds.Add(JsonHelpers.GetString(p, "id"));

            var addedInstances = new List<JObject>();
            foreach (var token in newBoardPieces)
                if (token is JObject p && !existingIds.Contains(JsonHelpers.GetString(p, "id")))
                    addedInstances.Add(p);

            var alivePositions = new HashSet<string>();
            if (boardState["pieces"] is JArray oldPiecesArr2)
                foreach (var token in oldPiecesArr2)
                    if (token is JObject p && JsonHelpers.GetBool(p, "is_alive", true))
                        alivePositions.Add(PositionKey(p["position"]));

            var (instW, instH) = CpBoardBounds(newBoardState, (boundW, boundH));

            foreach (var inst in addedInstances)
            {
                var instType = JsonHelpers.GetString(inst, "type");
                var instPosition = inst["position"] as JArray;

                var allValidTypes = new HashSet<string>(CpPredefinedTypes);
                foreach (var token in newCustomPieces)
                    if (token is JObject cp) allValidTypes.Add(JsonHelpers.GetString(cp, "type"));

                if (!allValidTypes.Contains(instType))
                    validationErrors.Add($"新棋子实例 type '{instType}' 未在 custom_pieces 中定义");

                if (instPosition == null || instPosition.Count != 2)
                {
                    validationErrors.Add($"新棋子实例 '{JsonHelpers.GetString(inst, "id")}' 的 position 格式错误");
                    continue;
                }

                int x = instPosition[0].Type == JTokenType.Integer ? instPosition[0].Value<int>() : -1;
                int y = instPosition[1].Type == JTokenType.Integer ? instPosition[1].Value<int>() : -1;
                if (x < 0 || x >= instW || y < 0 || y >= instH)
                    validationErrors.Add(
                        $"新棋子实例 '{JsonHelpers.GetString(inst, "id")}' 的位置 [{x},{y}] 超出棋盘范围 "
                        + string.Format(CpRangeErrorFmt, instW - 1, instH - 1));

                var key = PositionKey(inst["position"]);
                if (alivePositions.Contains(key))
                    validationErrors.Add($"新棋子实例 '{JsonHelpers.GetString(inst, "id")}' 的位置 [{x},{y}] 与现有棋子重叠");

                alivePositions.Add(key);
            }

            if (validationErrors.Count > 0)
            {
                if (!(logEntry["validation"] is JObject vlog))
                {
                    vlog = new JObject();
                    logEntry["validation"] = vlog;
                }
                vlog["errors"] = ToJArray(validationErrors);
                vlog["success"] = false;
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "校验失败: " + string.Join("; ", validationErrors),
                    ["classification"] = "C+",
                };
            }

            var (validBoard, errBoard) = ValidateBoard(newBoardState, boardState, "add");
            if (!validBoard)
            {
                if (!(logEntry["validation"] is JObject vlog2))
                {
                    vlog2 = new JObject();
                    logEntry["validation"] = vlog2;
                }
                vlog2["schema_error"] = errBoard;
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"棋盘验证失败: {errBoard}",
                    ["classification"] = "C+",
                };
            }

            if (!(logEntry["validation"] is JObject vlog3))
            {
                vlog3 = new JObject();
                logEntry["validation"] = vlog3;
            }
            vlog3["success"] = true;

            var actionDesc = JsonHelpers.GetString(instruction, "action");
            if (!string.IsNullOrEmpty(actionDesc) && newBoardState["game_status"] is JObject cpStatus)
            {
                if (!(cpStatus["custom_rules_active"] is JArray active))
                {
                    active = new JArray();
                    cpStatus["custom_rules_active"] = active;
                }
                active.Add($"[{sideLabel}]{actionDesc}");
            }

            return new JObject
            {
                ["success"] = true,
                ["type"] = "applied",
                ["message"] = JsonHelpers.GetString(intent, "response_to_player", $"{sideLabel}新棋子已创建"),
                ["modified_configs"] = new JObject
                {
                    [targetConfigName] = newPieces,
                    ["board_state"] = newBoardState,
                },
                ["classification"] = "C+",
            };
        }

        static string PositionKey(JToken position)
        {
            if (position is JArray arr && arr.Count == 2)
                return arr[0].ToString() + "," + arr[1].ToString();
            return position?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
        }

        /// <summary>C+ 实例位置校验边界 (x上限, y上限)；优先从 board_state.board 动态读取。</summary>
        (int w, int h) CpBoardBounds(JObject boardState, (int w, int h)? fallback)
        {
            var fb = fallback ?? CpDefaultBounds;
            var geo = boardState?["board"] as JObject;
            var w = JsonHelpers.GetInt(geo, "width", 0);
            var h = JsonHelpers.GetInt(geo, "height", 0);
            return (w != 0 ? w : fb.w, h != 0 ? h : fb.h);
        }

        // ══════════════════════════════════════════════════════════════
        // D 类：界面修改（D1 配置 / D2 HTML 区段）
        // ══════════════════════════════════════════════════════════════

        /// <summary>D类：界面修改（带日志）。</summary>
        async Task<JObject> HandleActionDWithLogAsync(
            JObject intent, JObject configs, JObject logEntry, CancellationToken cancellationToken)
        {
            var instruction = intent["structured_instruction"] as JObject ?? new JObject();
            var nextPrompt = JsonHelpers.GetString(intent, "next_ai_prompt");
            var targetFiles = intent["target_files"] as JArray ?? new JArray();
            var target = JsonHelpers.GetString(instruction, "target");
            var action = JsonHelpers.GetString(instruction, "action");
            var parameters = instruction["parameters"] as JObject ?? new JObject();

            string[] dActions = { "change_color", "modify_style", "change_theme", "modify_layout",
                                  "update_appearance", "change_font", "modify_board", "customize",
                                  "update_ui", "style_change", "appearance",
                                  "piece", "pieces", "piece_size", "piece_color", "piece_style",
                                  "resize", "scale", "size", "棋子", "大小", "尺寸" };
            if (!string.IsNullOrEmpty(action) && !ContainsAny(action.ToLowerInvariant(), dActions))
            {
                string[] dTargets = { "board", "ui", "theme", "style", "layout", "appearance",
                                      "color", "font", "background", "visual", "display",
                                      "piece", "pieces", "棋子", "棋子大小", "棋子颜色", "棋子样式" };
                if (!string.IsNullOrEmpty(target) && !ContainsAny(target.ToLowerInvariant(), dTargets))
                {
                    return new JObject
                    {
                        ["success"] = false,
                        ["type"] = "rejected",
                        ["message"] = $"D类不处理此动作/目标: {action} / {target}",
                        ["classification"] = "D",
                    };
                }
            }

            var targetConfigName = "ui_config";
            if (ContainsString(targetFiles, "board.json")
                || target.ToLowerInvariant().Contains("board")
                || target.ToLowerInvariant().Contains("board_layout"))
            {
                targetConfigName = "board";
            }

            var configData = GetConfigOrEmpty(configs, targetConfigName);
            var configFilename = targetConfigName + ".json";

            string[] boardSizeKeywords = { "add_column", "add_row", "expand_board", "remove_column", "remove_row",
                                           "widen", "narrow", "resize", "change_size", "拓宽", "增加行", "增加列",
                                           "减少行", "减少列", "扩大", "缩小" };
            string[] promptSizeKeywords = { "拓宽", "增加一竖", "增加一行", "增加列", "增加行",
                                            "board_width", "board_height", "grid_columns", "grid_rows" };
            var isBoardSizeChange = ContainsAny(action.ToLowerInvariant(), boardSizeKeywords)
                || ContainsAny((nextPrompt ?? "").ToLowerInvariant(), promptSizeKeywords);

            JObject autoBoardStateUpdate = null;
            if (isBoardSizeChange && configs["board_state"] is JObject)
            {
                var boardState = GetConfigOrEmpty(configs, "board_state");
                if (boardState["board"] is JObject boardSub)
                {
                    if (parameters.ContainsKey("total_columns") || parameters.ContainsKey("board_width")
                        || parameters.ContainsKey("new_column_index"))
                    {
                        var curW = JsonHelpers.GetInt(boardSub, "width",
                            JsonHelpers.GetInt(configs["board"]?["geometry"] as JObject, "width", 19));
                        var newWidth = JsonHelpers.GetInt(parameters, "total_columns",
                            JsonHelpers.GetInt(parameters, "board_width", curW + 1));
                        boardSub["width"] = newWidth;
                        autoBoardStateUpdate = boardState;
                    }
                    else if (parameters.ContainsKey("total_rows") || parameters.ContainsKey("board_height")
                             || parameters.ContainsKey("rows_to_add"))
                    {
                        var curH = JsonHelpers.GetInt(boardSub, "height",
                            JsonHelpers.GetInt(configs["board"]?["geometry"] as JObject, "height", 19));
                        var newHeight = JsonHelpers.GetInt(parameters, "total_rows",
                            JsonHelpers.GetInt(parameters, "board_height", curH + JsonHelpers.GetInt(parameters, "rows_to_add", 1)));
                        boardSub["height"] = newHeight;
                        autoBoardStateUpdate = boardState;
                    }
                }
            }

            var sizeChangeNote = "";
            if (isBoardSizeChange)
            {
                sizeChangeNote = "\n## ⚠️ 重要提醒：棋盘尺寸修改\n"
                    + "此任务涉及棋盘尺寸修改。注意：\n"
                    + "- board.json 的 geometry 字段包含棋盘行列数字段（width/height）\n"
                    + "- board.json 的 appearance 字段包含视觉配置（layout.viewbox_padding_* 和 layout.board_size 等）\n"
                    + "- 棋盘行列数的修改需要修改 geometry 字段\n"
                    + "- **不要输出空的 patch 数组！至少要输出一个操作（即使只是保持原值的 replace）**\n";
            }

            var userPrompt = "## 修改任务\n" + nextPrompt + "\n\n"
                + "## 具体操作\n"
                + $"- 动作: {action}\n"
                + $"- 目标: {target}\n"
                + $"- 参数: {Dump(parameters, false)}\n"
                + sizeChangeNote + "\n"
                + "## 棋子外观修改指引（若涉及棋子颜色/大小/字形）\n"
                + $"- 棋子颜色：改 {targetConfigName} 的 theme.pieces.*_color / *bg 字段（红黑方同理）\n"
                + "- 棋子大小/比例/字大小：在 custom_css 用 `.piece` 覆盖并加 !important，例如：\n"
                + "  `.piece { width: 12% !important; height: 10% !important; font-size: 1.6rem !important; }`\n"
                + "  （部分棋类用 --piece-width/--piece-height/--piece-font-size 变量时也可一并覆盖；\n"
                + "   若改整体棋盘大小时改 layout.board_size）\n"
                + $"## 当前{configFilename}完整内容\n```json\n" + Dump(configData, true) + "\n```\n\n"
                + "## 要求\n"
                + "0. **优先输出 JSON Patch 数组**（RFC 6902 格式），仅描述需要修改的字段，避免输出完整配置。格式示例：\n"
                + "   [{\"op\": \"replace\", \"path\": \"/appearance/background_color\", \"value\": \"#abcdef\"}]\n"
                + "   如果无法生成 patch，再输出完整 JSON。\n"
                + $"1. 请根据上述任务修改{configFilename}\n"
                + $"2. 输出修改后的完整{configFilename}\n"
                + "3. 只修改需要修改的部分，保持其他部分不变\n"
                + "4. 确保JSON格式正确\n"
                + "5. 只输出JSON，不要输出其他内容";

            var (newConfig, errorMsg) = await CallCodeAiWithValidationAsync(
                PromptBuilder.UiModifierSystem, userPrompt, targetConfigName, configData,
                logEntry, 0.2, "unknown", false, null, cancellationToken).ConfigureAwait(false);

            if (newConfig == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = string.IsNullOrEmpty(errorMsg) ? "AI生成失败" : errorMsg,
                };
            }

            var modifiedConfigs = new JObject { [targetConfigName] = newConfig };
            if (autoBoardStateUpdate != null)
                modifiedConfigs["board_state"] = autoBoardStateUpdate;

            return new JObject
            {
                ["success"] = true,
                ["type"] = "applied",
                ["message"] = JsonHelpers.GetString(intent, "response_to_player", "界面已修改"),
                ["modified_configs"] = modifiedConfigs,
                ["classification"] = "D",
            };
        }

        /// <summary>D2类：HTML结构修改（通过区段替换 index.html）。</summary>
        async Task<JObject> HandleActionD2WithLogAsync(
            JObject intent, JObject configs, JObject logEntry, CancellationToken cancellationToken)
        {
            var codeGenLog = logEntry["code_generation"] as JObject;
            if (codeGenLog == null) { codeGenLog = new JObject(); logEntry["code_generation"] = codeGenLog; }

            var instruction = intent["structured_instruction"] as JObject ?? new JObject();
            var targetSections = instruction["target_sections"] as JArray ?? new JArray();
            var userCommand = JsonHelpers.GetString(intent, "response_to_player");
            var nextPrompt = JsonHelpers.GetString(intent, "next_ai_prompt");
            var action = JsonHelpers.GetString(instruction, "action");

            string[] d2Actions = { "modify_html", "add_element", "remove_element", "update_html",
                                   "change_html", "edit_section", "modify_structure", "add_button",
                                   "add_panel", "update_ui" };
            if (!string.IsNullOrEmpty(action) && !ContainsAny(action.ToLowerInvariant(), d2Actions))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "rejected",
                    ["message"] = $"D2类不处理此动作: {action}",
                    ["classification"] = "D",
                };
            }

            var htmlPath = string.IsNullOrEmpty(BaseDir) ? null : Path.Combine(BaseDir, "static", "index.html");
            if (htmlPath == null || !File.Exists(htmlPath))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "index.html 文件不存在",
                    ["classification"] = "D",
                };
            }

            var htmlContent = File.ReadAllText(htmlPath);

            var sections = new Dictionary<string, string>();
            foreach (var token in targetSections)
            {
                if (token.Type != JTokenType.String) continue;
                var sectionName = token.Value<string>();
                var pattern = $"<!-- SECTION: {Regex.Escape(sectionName)} -->(.*?)<!-- END: {Regex.Escape(sectionName)} -->";
                var match = Regex.Match(htmlContent, pattern, RegexOptions.Singleline);
                if (match.Success) sections[sectionName] = match.Groups[1].Value.Trim();
            }

            if (sections.Count == 0)
            {
                if (!(logEntry["errors"] is JArray errs))
                {
                    errs = new JArray();
                    logEntry["errors"] = errs;
                }
                var names = new List<string>();
                foreach (var token in targetSections)
                    if (token.Type == JTokenType.String) names.Add(token.Value<string>());
                errs.Add($"未找到目标区段：{string.Join(", ", names)}");
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"未找到目标HTML区段：{string.Join(", ", names)}",
                    ["classification"] = "D",
                };
            }

            var userPrompt = "## 修改任务\n" + nextPrompt + "\n\n"
                + "## 用户请求\n" + userCommand + "\n\n"
                + "## 具体操作\n"
                + $"- 动作: {JsonHelpers.GetString(instruction, "action")}\n"
                + $"- 目标: {JsonHelpers.GetString(instruction, "target")}\n"
                + $"- 参数: {Dump(instruction["parameters"] ?? new JObject(), false)}\n\n"
                + "## 需要修改的HTML区段：\n";
            foreach (var kv in sections)
                userPrompt += $"\n--- 区段: {kv.Key} ---\n{kv.Value}\n";

            userPrompt += "\n\n## 要求\n"
                + "请输出修改后的区段内容。格式为JSON对象：\n"
                + "```json\n{\n  \"section_name\": \"修改后的完整HTML内容\",\n  ...\n}\n```\n"
                + "只输出需要修改的区段，不需要修改的区段不要输出。不需要包含 <!-- SECTION --> 和 <!-- END --> 注释标记（系统会自动包裹）。";

            string response;
            double elapsed;
            try
            {
                (response, elapsed) = await CallDeepSeekAsync(
                    PromptBuilder.UiModifierSystem, userPrompt, 0.2, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                codeGenLog["success"] = false;
                codeGenLog["error"] = e.Message;
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = $"AI生成失败: {e.Message}",
                    ["classification"] = "D",
                };
            }

            codeGenLog["success"] = true;
            codeGenLog["elapsed_time"] = elapsed;
            codeGenLog["raw_output"] = response;

            var modifiedSections = ExtractJson(response);
            if (modifiedSections == null)
            {
                codeGenLog["parse_error"] = "无法解析为JSON对象";
                return new JObject
                {
                    ["success"] = false,
                    ["type"] = "error",
                    ["message"] = "AI输出解析失败：期望JSON对象",
                    ["classification"] = "D",
                };
            }

            var parsedSectionNames = new List<string>();
            foreach (var prop in modifiedSections.Properties()) parsedSectionNames.Add(prop.Name);
            codeGenLog["parsed_sections"] = new JArray(parsedSectionNames.ToArray());
            codeGenLog["modified_sections_detail"] = modifiedSections;

            foreach (var prop in modifiedSections.Properties())
            {
                var sectionName = prop.Name;
                if (prop.Value.Type != JTokenType.String) continue;
                var newContent = prop.Value.Value<string>();

                var cleaned = Regex.Replace(newContent, $"<!-- SECTION: {Regex.Escape(sectionName)} -->\\s*", "");
                cleaned = Regex.Replace(cleaned, $"\\s*<!-- END: {Regex.Escape(sectionName)} -->", "").Trim();

                var pattern = $"(<!-- SECTION: {Regex.Escape(sectionName)} -->)(.*?)(<!-- END: {Regex.Escape(sectionName)} -->)";
                var content = cleaned;
                htmlContent = Regex.Replace(htmlContent, pattern,
                    m => m.Groups[1].Value + "\n" + content + "\n" + m.Groups[3].Value,
                    RegexOptions.Singleline);
            }

            File.WriteAllText(htmlPath, htmlContent);

            return new JObject
            {
                ["success"] = true,
                ["type"] = "applied",
                ["message"] = $"已修改HTML区段：{string.Join(", ", parsedSectionNames)}，请刷新页面查看",
                ["modified_configs"] = new JObject(),
                ["classification"] = "D",
                ["refresh_page"] = true,
            };
        }

        // ── 辅助 ──

        static int CountAlive(JObject board)
        {
            var pieces = board?["pieces"] as JArray;
            var count = 0;
            if (pieces != null)
                foreach (var token in pieces)
                    if (token is JObject p && JsonHelpers.GetBool(p, "is_alive", true))
                        count++;
            return count;
        }
    }
}