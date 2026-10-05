using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 记忆碎片系统（对应 legacy-web/samsara/memory_fragments.py 的 MemoryFragmentSystem）。
    /// 解锁条件：某道全程无作弊通关；集齐 6 个是真我结局的必要条件。
    /// </summary>
    public sealed class MemoryFragmentSystem
    {
        private readonly SamsaraState _state;
        private readonly JObject _story;

        public MemoryFragmentSystem(SamsaraState state, string storyPath)
        {
            _state = state;
            _story = SamsaraJson.LoadObject(storyPath) ?? new JObject();
        }

        /// <summary>获取某道的记忆碎片剧情数据。</summary>
        public JObject GetFragmentData(string realm)
        {
            var realms = _story["realms"] as JObject;
            var realmData = realms == null ? null : realms[realm] as JObject;
            return realmData == null ? new JObject() : (realmData["memory_fragment"] as JObject ?? new JObject());
        }

        /// <summary>检查某道记忆碎片是否满足解锁条件。</summary>
        public bool CheckUnlockCondition(string realm)
        {
            return _state.IsRealmNoCheatClear(realm);
        }

        /// <summary>尝试解锁某道的记忆碎片，返回 {success, already_unlocked, fragment, reason}。</summary>
        public JObject TryUnlock(string realm)
        {
            var frags = _state.GetMemoryFragmentsUnlocked();

            if (SamsaraJson.GetBool(frags, realm, false))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["already_unlocked"] = true,
                    ["fragment"] = GetFragmentData(realm),
                    ["reason"] = "已解锁",
                };
            }

            if (!CheckUnlockCondition(realm))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["already_unlocked"] = false,
                    ["fragment"] = JValue.CreateNull(),
                    ["reason"] = "需全程无作弊通关此道",
                };
            }

            _state.UnlockMemoryFragment(realm);
            return new JObject
            {
                ["success"] = true,
                ["already_unlocked"] = false,
                ["fragment"] = GetFragmentData(realm),
                ["reason"] = "解锁成功",
            };
        }

        /// <summary>返回全部 6 道记忆碎片状态。</summary>
        public JArray GetAllFragmentsStatus()
        {
            var frags = _state.GetMemoryFragmentsUnlocked();
            var result = new JArray();
            foreach (var realm in SamsaraConstants.Realms)
            {
                var fragData = GetFragmentData(realm);
                result.Add(new JObject
                {
                    ["realm"] = realm,
                    ["unlocked"] = SamsaraJson.GetBool(frags, realm, false),
                    ["title"] = SamsaraJson.GetString(fragData, "title", ""),
                    ["id"] = SamsaraJson.GetString(fragData, "id", ""),
                    ["cg"] = SamsaraJson.GetString(fragData, "cg", ""),
                    ["condition_met"] = CheckUnlockCondition(realm),
                });
            }
            return result;
        }

        /// <summary>返回已解锁的记忆碎片完整数据。</summary>
        public JArray GetUnlockedFragments()
        {
            var frags = _state.GetMemoryFragmentsUnlocked();
            var result = new JArray();
            foreach (var realm in SamsaraConstants.Realms)
            {
                if (!SamsaraJson.GetBool(frags, realm, false)) continue;
                var fragData = SamsaraJson.Clone(GetFragmentData(realm)) ?? new JObject();
                fragData["realm"] = realm;
                result.Add(fragData);
            }
            return result;
        }

        /// <summary>是否集齐全部 6 个记忆碎片。</summary>
        public bool AllCollected()
        {
            return _state.AllMemoryFragmentsCollected();
        }

        public int GetUnlockCount()
        {
            var frags = _state.GetMemoryFragmentsUnlocked();
            var count = 0;
            foreach (var prop in frags.Properties())
            {
                if (SamsaraJson.GetBool(frags, prop.Name, false)) count++;
            }
            return count;
        }
    }
}