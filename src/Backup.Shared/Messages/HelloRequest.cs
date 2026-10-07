namespace Backup.Shared.Messages;

public class HelloRequest
{
    public string ClientName { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
}
