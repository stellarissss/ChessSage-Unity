using System;

namespace ChessSage.Core.Save
{
    /// <summary>存档系统异常基类。</summary>
    public class SaveException : Exception
    {
        public SaveException(string message) : base(message) { }
        public SaveException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>指定的存档槽/自动存档不存在。</summary>
    public sealed class SaveNotFoundException : SaveException
    {
        public SaveNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// 存档损坏或被篡改：JSON 无法解析、缺少校验和、或校验和不匹配。
    /// 加载路径**必须**抛出本异常，绝不静默返回坏档（否则玩家进度会无声丢失/污染）。
    /// </summary>
    public sealed class SaveCorruptedException : SaveException
    {
        public SaveCorruptedException(string message) : base(message) { }
        public SaveCorruptedException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>存档的 schema 版本高于当前程序可识别版本，无法安全向下读取。</summary>
    public sealed class SaveVersionException : SaveException
    {
        public int SaveVersion { get; }
        public int SupportedVersion { get; }

        public SaveVersionException(int saveVersion, int supportedVersion)
            : base($"存档版本 {saveVersion} 高于当前支持的版本 {supportedVersion}，请更新游戏后再读取")
        {
            SaveVersion = saveVersion;
            SupportedVersion = supportedVersion;
        }
    }
}
