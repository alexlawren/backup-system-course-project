namespace Backup.Shared.Messages;

public class PrepareVersionRequest
{
    public Guid BackupSetId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public BackupChangeOrigin ChangeOrigin { get; set; } = BackupChangeOrigin.Normal;
    public Guid? ActorClientId { get; set; }
    public Guid? ClientId { get; set; }
    public string? ConflictOriginalPath { get; set; }
    public int? ConflictOriginalVersion { get; set; }
}

public class PrepareVersionResponse
{
    public bool UploadRequired { get; set; }
    public Guid? OperationId { get; set; }
    public Guid FileId { get; set; }
    public Guid ClientId { get; set; }
    public int ExpectedVersionNumber { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
