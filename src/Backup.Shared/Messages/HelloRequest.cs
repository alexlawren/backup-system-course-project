namespace Backup.Shared.Messages;

public class HelloRequest
{
    public string ClientName { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public string? Role { get; set; }
    public string? MachineName { get; set; }
    public Guid? ClientId { get; set; }
}
