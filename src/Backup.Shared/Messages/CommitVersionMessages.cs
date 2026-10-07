namespace Backup.Shared.Messages;

public class CommitVersionRequest
{
    public Guid OperationId { get; set; }
    public string? StorageKey { get; set; }
    public long ActualSize { get; set; }
    public string ActualSha256 { get; set; } = string.Empty;
}

public class CommitVersionResponse
{
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public string? StorageKey { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
