using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Json
{
    /// <summary>
    /// 针对 <see cref="JArray"/> 的「值比较」扩展方法。
    ///
    /// 背景（P0 缺陷 D-01）：legacy Python 用 <c>x in list</c> 做**值**比较，C# 移植时若直接调用
    /// Newtonsoft.Json 13.0.3 的 <c>JArray.Contains</c>，命中的是
    /// <see cref="System.Collections.Generic.ICollection{T}.Contains"/>，其实现走的是
    /// **JToken 引用相等**：
    /// <code>
    /// new JArray { "hell" }.Contains("hell")                    // => false（每次都新建 JValue，引用不同）
    /// JToken.DeepEquals(new JValue("hell"), new JValue("hell")) // => true
    /// </code>
    /// 因此对「每次新建的标量」做成员判定会**静默恒为 false**，令存档幂等判定、目标结算等逻辑失效。
    ///
    /// 本类提供按值比较的替代方法。凡对应 Python <c>in list</c> 语义的调用点都必须改用它，
    /// 禁止回退到 <c>JArray.Contains</c>（该行为在 Newtonsoft 13.x 下不会改变）。
    /// </summary>
    public static class JArrayExtensions
    {
        /// <summary>
        /// 按「值」判断 <see cref="JArray"/> 是否包含指定字符串（等价于 Python <c>value in list</c>）。
        /// null 安全：数组为 null、元素 token 为 null 或 JSON null 时均视为不匹配。
        ///
        /// ⚠️ 不要用 <c>JArray.Contains</c> 替代——那是 JToken 引用比较（Newtonsoft 13.x），
        /// 对每次新建的 <c>JValue("x")</c> 恒为 false，会让成员判定静默失效（见缺陷 D-01）。
        /// </summary>
        /// <param name="array">待检查的 JSON 数组；可为 null。</param>
        /// <param name="value">要匹配的字符串值。</param>
        /// <returns>数组中存在值等于 <paramref name="value"/> 的元素时为 true。</returns>
        public static bool ContainsValue(this JArray array, string value)
        {
            if (array == null) return false;
            foreach (var token in array)
            {
                if (token == null || token.Type == JTokenType.Null) continue;
                if (token.Value<string>() == value) return true;
            }
            return false;
        }
    }
}
