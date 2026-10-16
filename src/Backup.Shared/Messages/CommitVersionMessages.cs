namespace Backup.Shared.Messages;

public class CommitVersionRequest
{
    public Guid OperationId { get; set; }
    public string? StorageKey { get; set; }
    public long ActualSize { get; set; }
    public string ActualSha256 { get; set; } = string.Empty;
    public Guid? ActorClientId { get; set; }
    public string? ConflictOriginalPath { get; set; }
    public int? ConflictOriginalVersion { get; set; }
}

public class CommitVersionResponse
{
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public string? StorageKey { get; set; }
    public bool Success { get; set; } = true;
    public bool AlreadyCommitted { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
