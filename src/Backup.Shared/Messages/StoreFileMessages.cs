namespace Backup.Shared.Messages;

public class StoreFileRequest
{
    public Guid OperationId { get; set; }
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public long ExpectedSize { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
}

public class StoreFileReadyResponse
{
    public Guid OperationId { get; set; }
    public bool Accepted { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class StoreFileResultResponse
{
    public Guid OperationId { get; set; }
    public bool Success { get; set; }
    public string? StorageKey { get; set; }
    public long ActualSize { get; set; }
    public string? ActualSha256 { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
