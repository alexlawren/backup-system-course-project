namespace Backup.Shared.Messages;

public class GetStoredFileRequest
{
    public string StorageKey { get; set; } = string.Empty;
}

public class GetStoredFileResultResponse
{
    public string StorageKey { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
