using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Save
{
    /// <summary>
    /// 存档格式迁移器：把旧版本存档文件补齐/改名为当前 schema。
    ///
    /// 版本历史：
    ///   v1 → 仅含 payload.samsara；meta 用 <c>level</c> 表示关卡，且无游玩时长字段。
    ///   v2 → payload 增加 <c>game</c>（对局进度，默认空对象）；
    ///        meta 的 <c>level</c> 更名为 <c>current_level</c>，新增 <c>playtime_seconds</c>（默认 0）、
    ///        <c>variant</c>（默认 ""）、<c>autosave</c>（默认 false）。
    ///
    /// 迁移只改写文档结构，**不负责**校验和校验——校验必须在迁移前对原始 payload 完成，
    /// 否则迁移引入的新字段会让旧档的校验和失配。
    /// </summary>
    public static class SaveMigrator
    {
        /// <summary>当前程序写出的 schema 版本。</summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>读取文档里声明的 schema 版本；缺失或非法时按最早的 v1 处理。</summary>
        public static int ReadVersion(JObject document)
        {
            var version = SaveMetadata.ReadInt(document, "schema_version", 1);
            return version < 1 ? 1 : version;
        }

        /// <summary>
        /// 就地把 <paramref name="document"/> 迁移到 <see cref="CurrentSchemaVersion"/>；
        /// <paramref name="fromVersion"/> 回传进入时的版本。已是最新版本时为无操作（幂等）。
        /// </summary>
        public static JObject Migrate(JObject document, out int fromVersion)
        {
            fromVersion = ReadVersion(document);

            if (fromVersion < 2) MigrateV1ToV2(document);

            document["schema_version"] = CurrentSchemaVersion;
            return document;
        }

        /// <summary>v1 → v2：payload 增加 game；meta 更名 level→current_level 并补默认字段。</summary>
        static void MigrateV1ToV2(JObject document)
        {
            if (!(document["payload"] is JObject payload))
            {
                payload = new JObject();
                document["payload"] = payload;
            }
            if (payload["samsara"] == null) payload["samsara"] = new JObject();
            if (payload["game"] == null) payload["game"] = new JObject();

            if (!(document["meta"] is JObject meta))
            {
                meta = new JObject();
                document["meta"] = meta;
            }
            // 旧字段 level 更名为 current_level（仅在目标缺失时映射，避免覆盖已有新字段）。
            if (meta["current_level"] == null && meta["level"] != null)
                meta["current_level"] = meta["level"];
            if (meta["playtime_seconds"] == null) meta["playtime_seconds"] = 0.0;
            if (meta["variant"] == null) meta["variant"] = string.Empty;
            if (meta["autosave"] == null) meta["autosave"] = false;
        }
    }
}
