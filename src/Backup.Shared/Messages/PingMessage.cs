namespace Backup.Shared.Messages;

public class PingMessage
{
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
