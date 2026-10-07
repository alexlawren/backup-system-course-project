namespace Backup.Shared.Messages;

public class HelloResponse
{
    public string ServerName { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public string? Role { get; set; }
    public string? ServiceName { get; set; }
    public bool Success { get; set; } = true;
    public string? Message { get; set; }
}
