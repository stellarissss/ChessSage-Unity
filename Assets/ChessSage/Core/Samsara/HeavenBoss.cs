using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 天道 Boss 战系统（对应 legacy-web/samsara/heaven_boss.py 的 HeavenBossSystem）。
    /// 隐藏 Boss 战：六道通关且使用过真心祈求后进入与天道的象棋对决。
    /// 机械配置（棋子/规则/AI/胜负）取 tiandao_boss.json；
    /// 对白与资产（bgm/background）优先取 story.json.tiandao，回退机械配置。
    /// </summary>
    public sealed class HeavenBossSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _config;
        private readonly JObject _story;
        private readonly JObject _tiandao;

        public HeavenBossSystem(SamsaraState state, string bossConfigPath, string storyPath)
        {
            _state = state;
            _config = SamsaraJson.LoadObject(bossConfigPath) ?? new JObject();
            _story = SamsaraJson.LoadObject(storyPath) ?? new JObject();
            _tiandao = _story["tiandao"] as JObject ?? new JObject();
        }

        /// <summary>读取对白：优先 story.json.tiandao.boss_dialogues，回退旧机械配置 dialogues。</summary>
        private JArray GetDialogues(string key)
        {
            var storyDialogues = _tiandao["boss_dialogues"] as JObject;
            var storyDlg = storyDialogues == null ? null : storyDialogues[key] as JArray;
            if (storyDlg != null && storyDlg.Count > 0) return storyDlg;

            var configDialogues = _config["dialogues"] as JObject;
            var fallback = configDialogues == null ? null : configDialogues[key] as JArray;
            return fallback ?? new JArray();
        }

        /// <summary>返回机械配置（tiandao_boss.json）。</summary>
        public JObject GetConfig()
        {
            return _config;
        }

        /// <summary>检查是否可以进入天道 Boss 战。</summary>
        public JObject CanEnter()
        {
            if (!_state.AllRealmsCompleted())
            {
                return new JObject
                {
                    ["can_enter"] = false,
                    ["reason"] = "六道尚未全部通关",
                };
            }
            if (_state.GetPrayerCount() < 1)
            {
                return new JObject
                {
                    ["can_enter"] = false,
                    ["reason"] = "未使用过真心祈求（无需审判）",
                };
            }
            var boss = _state.GetTiandaoBossState();
            if (SamsaraJson.GetBool(boss, "defeated", false))
            {
                return new JObject
                {
                    ["can_enter"] = false,
                    ["reason"] = "天道已被击败（识破结局已触发）",
                    ["already_defeated"] = true,
                };
            }
            return new JObject
            {
                ["can_enter"] = true,
                ["reason"] = "六道通关 + 使用过祈求 → 天道Boss战",
            };
        }

        /// <summary>进入 Boss 战，返回初始配置、入场对白与尝试次数。</summary>
        public JObject EnterBattle()
        {
            var check = CanEnter();
            if (!SamsaraJson.GetBool(check, "can_enter", false))
            {
                var failure = SamsaraJson.Clone(check) ?? new JObject();
                failure["success"] = false;
                return failure;
            }

            _state.UpdateTiandaoBossState(new JObject
            {
                ["current_battle_active"] = true,
            });
            var boss = _state.GetTiandaoBossState();
            _state.UpdateTiandaoBossState(new JObject
            {
                ["attempt_count"] = SamsaraJson.GetInt(boss, "attempt_count", 0) + 1,
            });

            return new JObject
            {
                ["success"] = true,
                ["config"] = _config.DeepClone(),
                ["dialogues_on_enter"] = GetDialogues("on_enter"),
                ["dialogues_mid"] = GetDialogues("mid_battle"),
                ["attempt_count"] = SamsaraJson.GetInt(_state.GetTiandaoBossState(), "attempt_count", 1),
            };
        }

        /// <summary>返回 Boss 战初始棋盘配置。</summary>
        public JObject GetInitialBoard()
        {
            return _config["initial_board"] as JObject ?? new JObject();
        }

        /// <summary>返回 Boss 战规则。</summary>
        public JObject GetRules()
        {
            return _config["rules"] as JObject ?? new JObject();
        }

        /// <summary>返回棋子配置（天道无士，士位被车占据）。</summary>
        public JObject GetPiecesConfig()
        {
            return _config["pieces"] as JObject ?? new JObject();
        }

        /// <summary>Boss 战胜利处理：标记 defeated 并触发识破结局。</summary>
        public JObject OnWin()
        {
            _state.UpdateTiandaoBossState(new JObject
            {
                ["defeated"] = true,
                ["current_battle_active"] = false,
            });
            var bossBattle = _tiandao["boss_battle"] as JObject ?? new JObject();
            return new JObject
            {
                ["success"] = true,
                ["defeated"] = true,
                ["on_win_action"] = "trigger_ending_exposed",
                ["dialogues_on_win"] = GetDialogues("on_win"),
                ["next"] = SamsaraJson.GetString(bossBattle, "victory_triggers_ending", "exposed"),
                ["message"] = "天道Boss战胜利 → 触发识破结局",
            };
        }

        /// <summary>Boss 战失败处理（无限重试）。</summary>
        public JObject OnLose()
        {
            var boss = _state.GetTiandaoBossState();
            _state.UpdateTiandaoBossState(new JObject
            {
                ["current_battle_active"] = false,
            });
            var bossBattle = _tiandao["boss_battle"] as JObject ?? new JObject();
            return new JObject
            {
                ["success"] = true,
                ["defeated"] = false,
                ["on_lose_action"] = SamsaraJson.GetString(bossBattle, "on_lose_action", "retry"),
                ["retry_limit"] = SamsaraJson.GetInt(bossBattle, "retry_limit", -1),
                ["dialogues_on_lose"] = GetDialogues("on_lose"),
                ["attempt_count"] = SamsaraJson.GetInt(boss, "attempt_count", 0),
                ["can_retry"] = true,
                ["message"] = "天道Boss战失败 → 无限重试",
            };
        }

        /// <summary>返回 Boss 战状态。</summary>
        public JObject GetStatus()
        {
            var boss = _state.GetTiandaoBossState();
            return new JObject
            {
                ["defeated"] = SamsaraJson.GetBool(boss, "defeated", false),
                ["attempt_count"] = SamsaraJson.GetInt(boss, "attempt_count", 0),
                ["current_battle_active"] = SamsaraJson.GetBool(boss, "current_battle_active", false),
                ["can_enter"] = CanEnter(),
            };
        }

        /// <summary>返回 Boss 基础信息（供前端展示，资产优先取 story.json.tiandao）。</summary>
        public JObject GetBossInfo()
        {
            var bossBattle = _tiandao["boss_battle"] as JObject ?? new JObject();
            var aiConfig = _config["ai_config"] as JObject;
            var initialBoard = _config["initial_board"] as JObject;
            return new JObject
            {
                ["boss_id"] = SamsaraJson.GetString(_tiandao, "id", SamsaraJson.GetString(_config, "boss_id", "tiandao")),
                ["boss_name"] = SamsaraJson.GetString(_tiandao, "name", SamsaraJson.GetString(_config, "boss_name", "天道")),
                ["chess_type"] = SamsaraJson.GetString(bossBattle, "chess_type", SamsaraJson.GetString(_config, "chess_type", "xiangqi")),
                ["description"] = SamsaraJson.GetString(_tiandao, "description", SamsaraJson.GetString(_config, "description", "")),
                ["difficulty"] = SamsaraJson.GetString(aiConfig, "difficulty", "nightmare"),
                ["bgm"] = SamsaraJson.GetString(bossBattle, "bgm", SamsaraJson.GetString(_config, "bgm", "")),
                ["background"] = SamsaraJson.GetString(bossBattle, "background", SamsaraJson.GetString(_config, "background", "")),
                ["background_vortex"] = SamsaraJson.GetString(bossBattle, "background_vortex", SamsaraJson.GetString(_config, "background_vortex", "")),
                ["victory_condition"] = _config["victory_condition"] as JObject ?? new JObject(),
                ["defeat_condition"] = _config["defeat_condition"] as JObject ?? new JObject(),
                ["note"] = SamsaraJson.GetString(initialBoard, "note", ""),
            };
        }
    }
}