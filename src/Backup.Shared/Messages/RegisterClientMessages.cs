namespace Backup.Shared.Messages;

public class RegisterClientRequest
{
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
}

public class RegisterClientResponse
{
    public Guid ClientId { get; set; }
    public bool Created { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
