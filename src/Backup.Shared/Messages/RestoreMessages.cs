namespace Backup.Shared.Messages;

public class RestoreRequest
{
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
}

public class RestoreReadyResponse
{
    public Guid FileId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RestoreCompletedNotification
{
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public Guid FileId { get; set; }
    public int RestoredFromVersion { get; set; }
    public int NewVersionNumber { get; set; }
    public bool Success { get; set; } = true;
    public string? Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RestoreCompletedResponse
{
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public class RestoreDownloadResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string TempFilePath { get; set; } = string.Empty;
    public string TargetFilePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public Guid FileId { get; set; }
}

public class RestoreExecutionResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = "SUCCESS"; // SUCCESS, LOCAL_RESTORED_SYNC_PENDING, FAILED
    public string RelativePath { get; set; } = string.Empty;
    public string TargetLocalPath { get; set; } = string.Empty;
    public int SourceVersionNumber { get; set; }
    public int? NewCurrentVersionNumber { get; set; }
    public string Sha256Verified { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
