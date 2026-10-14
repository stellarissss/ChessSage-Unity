using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Save
{
    /// <summary>
    /// 存档内容校验和（SHA-256）。
    ///
    /// 只对 <c>payload</c>（真正的游戏数据）求哈希，不包含 <c>meta</c> 与写入时间，
    /// 因此改元信息不会误判为篡改，而任何对游戏数据的改动都会导致校验和不匹配。
    /// 校验发生在**迁移之前**（迁移可能改写 payload），故旧档仍可先验真再平移。
    /// </summary>
    public static class SaveChecksum
    {
        public const string AlgorithmTag = "sha256";

        /// <summary>计算 payload 的规范字符串（紧凑 JSON，属性顺序由写入方固定）。</summary>
        public static string Canonicalize(JToken payload)
            => payload == null ? "null" : payload.ToString(Formatting.None);

        /// <summary>返回形如 <c>sha256:&lt;hex&gt;</c> 的校验和。</summary>
        public static string Compute(JToken payload)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Canonicalize(payload)));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return AlgorithmTag + ":" + sb.ToString();
        }

        /// <summary>校验存储的校验和是否与当前 payload 匹配（大小写不敏感的前缀 + 定长比较）。</summary>
        public static bool Verify(string storedChecksum, JToken payload)
        {
            if (string.IsNullOrEmpty(storedChecksum)) return false;
            return string.Equals(storedChecksum, Compute(payload), StringComparison.Ordinal);
        }
    }
}
