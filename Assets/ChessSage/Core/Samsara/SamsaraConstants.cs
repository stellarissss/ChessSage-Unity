using System.Collections.Generic;

namespace ChessSage.Core.Samsara
{
    /// <summary>
    /// 六道轮回核心常量（对应 legacy-web/samsara/state.py 顶部定义）。
    /// </summary>
    public static class SamsaraConstants
    {
        /// <summary>六道顺序（state.py REALMS）。</summary>
        public static readonly string[] Realms =
        {
            "hell", "hungry", "animal", "human", "asura", "heaven"
        };

        /// <summary>一次性技能 id（每关一次，state.py ONE_TIME_SKILL_IDS）。</summary>
        public static readonly string[] OneTimeSkillIds =
        {
            "stealth_t2a", "stealth_t3a"
        };

        private static readonly Dictionary<string, string> RealmNameMap = new Dictionary<string, string>
        {
            { "hell", "地狱道" },
            { "hungry", "饿鬼道" },
            { "animal", "畜生道" },
            { "human", "人道" },
            { "asura", "阿修罗道" },
            { "heaven", "天道" },
        };

        /// <summary>返回道的显示名（state.py REALM_NAMES），未知道返回空串。</summary>
        public static string RealmName(string realm)
        {
            string name;
            if (realm != null && RealmNameMap.TryGetValue(realm, out name)) return name;
            return string.Empty;
        }

        /// <summary>返回全部道显示名映射的副本。</summary>
        public static Dictionary<string, string> RealmNames()
        {
            return new Dictionary<string, string>(RealmNameMap);
        }
    }
}