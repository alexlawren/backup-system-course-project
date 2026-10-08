namespace Backup.Shared.Messages;

public static class SyncPlanReasons
{
    public const string New = "NEW";
    public const string Changed = "CHANGED";
    public const string ServerDeleted = "SERVER_DELETED";
}

public class ManifestFileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public class ManifestRequest
{
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public IReadOnlyList<ManifestFileEntry> Files { get; set; } = Array.Empty<ManifestFileEntry>();
}

public class SyncPlanUploadItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class SyncPlanResponse
{
    public Guid BackupSetId { get; set; }
    public IReadOnlyList<SyncPlanUploadItem> Upload { get; set; } = Array.Empty<SyncPlanUploadItem>();
    public IReadOnlyList<string> Delete { get; set; } = Array.Empty<string>();
    public int UnchangedCount { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
