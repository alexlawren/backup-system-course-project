namespace Backup.Shared.Messages;

public enum BackupChangeOrigin
{
    Normal,
    Restore
}

public class FileChangeRequest
{
    public Guid BackupSetId { get; set; }
    public Guid? ClientId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public BackupChangeOrigin ChangeOrigin { get; set; } = BackupChangeOrigin.Normal;
}

public class UploadRequiredResponse
{
    public bool UploadRequired { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? FileId { get; set; }
    public int VersionNumber { get; set; }
}

public class UploadCompletedResponse
{
    public bool Success { get; set; } = true;
    public Guid? FileId { get; set; }
    public int? VersionNumber { get; set; }
    public string? StorageKey { get; set; }
    public bool AlreadyCommitted { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
