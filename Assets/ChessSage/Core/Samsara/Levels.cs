using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 关卡系统（对应 legacy-web/samsara/levels.py 的 LevelSystem）。
    /// 关卡池真源为 level_pools.json，残局数据真源为 puzzles.json。
    /// </summary>
    public sealed class LevelSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _levelPools;
        private readonly JObject _puzzles;

        public LevelSystem(SamsaraState state, string levelPoolsPath, string puzzlesPath)
        {
            _state = state;
            _levelPools = SamsaraJson.LoadObject(levelPoolsPath) ?? new JObject();
            _puzzles = SamsaraJson.LoadObject(puzzlesPath) ?? new JObject();
        }

        /// <summary>返回某道的原始关卡池配置（不存在返回 null）。</summary>
        public JObject GetLevelPool(string realm)
        {
            var pool = _levelPools[realm] as JObject;
            return SamsaraJson.Clone(pool);
        }

        /// <summary>载入指定道/关卡的配置（含 realm/game_type/puzzle_data 等派生字段）。</summary>
        public JObject LoadLevel(string realm = null, int? levelIndex = null)
        {
            if (realm == null) realm = _state.GetString("current_realm", "hell");
            if (levelIndex == null) levelIndex = _state.GetInt("current_level", 0);

            var pool = _levelPools[realm] as JObject;
            var levels = pool == null ? null : pool["levels"] as JArray;
            if (pool == null || levels == null || levelIndex.Value >= levels.Count) return null;

            var level = levels[levelIndex.Value] as JObject;
            if (level == null) return null;

            var result = SamsaraJson.Clone(level) ?? new JObject();
            result["realm"] = realm;
            result["realm_name"] = SamsaraConstants.RealmName(realm);
            result["game_type"] = SamsaraJson.GetString(pool, "game_type", "");
            result["realm_icon"] = SamsaraJson.GetString(pool, "icon", "");
            result["level_index"] = levelIndex.Value;
            result["total_levels"] = levels.Count;

            if (SamsaraJson.GetString(level, "type", "") == "puzzle")
            {
                var puzzleId = SamsaraJson.GetString(level, "puzzle_id", null);
                if (!string.IsNullOrEmpty(puzzleId))
                {
                    var gameType = SamsaraJson.GetString(pool, "game_type", "");
                    var byGame = _puzzles[gameType] as JObject;
                    var puzzle = byGame == null ? null : byGame[puzzleId];
                    if (puzzle != null) result["puzzle_data"] = puzzle.DeepClone();
                }
            }
            return result;
        }

        /// <summary>载入指定道的沙盒配置。</summary>
        public JObject LoadSandbox(string realm = null)
        {
            if (realm == null) realm = _state.GetString("current_realm", "hell");
            var pool = _levelPools[realm] as JObject;
            if (pool == null) return null;

            var sandbox = pool["sandbox"] as JObject ?? new JObject();
            var gameType = SamsaraJson.GetString(pool, "game_type", "");
            var realmName = SamsaraConstants.RealmName(realm);

            var result = new JObject();
            result["type"] = "sandbox";
            result["realm"] = realm;
            result["realm_name"] = realmName;
            result["game_type"] = gameType;
            result["realm_icon"] = SamsaraJson.GetString(pool, "icon", "");
            result["name"] = SamsaraJson.GetString(sandbox, "name", realmName + " · 沙盒");
            result["description"] = SamsaraJson.GetString(sandbox, "description", "");
            result["ai_personality"] = SamsaraJson.GetString(sandbox, "ai_personality", "normal");
            result["ai_depth"] = SamsaraJson.GetInt(sandbox, "ai_depth", 3);
            result["turn_limit"] = gameType == "weiqi" ? 40 : 20;
            result["objective"] = new JObject { ["type"] = "checkmate" };
            return result;
        }

        public JObject GetCurrentLevel()
        {
            return LoadLevel();
        }

        public int GetTotalLevels(string realm = null)
        {
            if (realm == null) realm = _state.GetString("current_realm", "hell");
            var pool = _levelPools[realm] as JObject;
            var levels = pool == null ? null : pool["levels"] as JArray;
            return levels == null ? 0 : levels.Count;
        }

        /// <summary>推进到下一关；已到最后一关返回 success=false。</summary>
        public JObject AdvanceToNextLevel()
        {
            var realm = _state.GetString("current_realm", "hell");
            var currentLevel = _state.GetInt("current_level", 0);
            var total = GetTotalLevels(realm);
            if (currentLevel < total - 1)
            {
                _state.AdvanceLevel();
                return new JObject
                {
                    ["success"] = true,
                    ["new_level"] = LoadLevel(),
                };
            }
            return new JObject
            {
                ["success"] = false,
                ["reason"] = "已到达最后一关",
            };
        }

        /// <summary>返回某道的进度摘要。</summary>
        public JObject GetRealmProgress(string realm = null)
        {
            if (realm == null) realm = _state.GetString("current_realm", "hell");
            var pool = _levelPools[realm] as JObject;
            var progressRoot = _state.Get("realm_progress") as JObject;
            var progress = progressRoot == null ? null : progressRoot[realm] as JObject;
            var levels = pool == null ? null : pool["levels"] as JArray;

            return new JObject
            {
                ["realm"] = realm,
                ["name"] = pool == null ? "" : SamsaraJson.GetString(pool, "name", ""),
                ["icon"] = pool == null ? "" : SamsaraJson.GetString(pool, "icon", ""),
                ["game_type"] = pool == null ? "" : SamsaraJson.GetString(pool, "game_type", ""),
                ["total_levels"] = levels == null ? 0 : levels.Count,
                ["levels_passed"] = SamsaraJson.GetInt(progress, "levels_passed", 0),
                ["completed"] = SamsaraJson.GetBool(progress, "completed", false),
                ["sandbox_unlocked"] = _state.IsSandboxUnlocked(realm),
                ["levels"] = levels == null ? new JArray() : levels.DeepClone(),
            };
        }

        public JArray GetAllRealmsProgress()
        {
            var result = new JArray();
            foreach (var prop in _levelPools.Properties())
                result.Add(GetRealmProgress(prop.Name));
            return result;
        }

        /// <summary>返回某道全部关卡（含 completed/current/locked 状态）。</summary>
        public JObject GetRealmLevels(string realm)
        {
            var pool = _levelPools[realm] as JObject;
            if (pool == null) return null;

            var progressRoot = _state.Get("realm_progress") as JObject;
            var progress = progressRoot == null ? null : progressRoot[realm] as JObject;
            var levelsPassed = SamsaraJson.GetInt(progress, "levels_passed", 0);
            var levels = pool["levels"] as JArray ?? new JArray();

            var levelsWithStatus = new JArray();
            for (var i = 0; i < levels.Count; i++)
            {
                var levelCopy = SamsaraJson.Clone(levels[i] as JObject) ?? new JObject();
                levelCopy["index"] = i;
                levelCopy["status"] = i < levelsPassed ? "completed" : (i == levelsPassed ? "current" : "locked");
                levelsWithStatus.Add(levelCopy);
            }

            return new JObject
            {
                ["realm"] = realm,
                ["name"] = SamsaraJson.GetString(pool, "name", ""),
                ["icon"] = SamsaraJson.GetString(pool, "icon", ""),
                ["game_type"] = SamsaraJson.GetString(pool, "game_type", ""),
                ["levels"] = levelsWithStatus,
                ["total_levels"] = levels.Count,
                ["levels_passed"] = levelsPassed,
                ["completed"] = SamsaraJson.GetBool(progress, "completed", false),
                ["sandbox_unlocked"] = _state.IsSandboxUnlocked(realm),
                ["sandbox"] = pool["sandbox"] as JObject ?? new JObject(),
            };
        }
    }
}