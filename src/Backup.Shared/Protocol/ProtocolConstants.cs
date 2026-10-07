namespace Backup.Shared.Protocol;

public static class ProtocolConstants
{
    public const string Magic = "BKP1";
    public const int Version = 1;
    public const uint MaxHeaderLength = 64 * 1024; // 64 KiB (65536 bytes)
    public const int HeaderLengthFieldSize = 4; // 4 bytes UInt32 BigEndian
}
