namespace Backup.Shared.Messages;

public class DeleteFileMetadataRequest
{
    public Guid BackupSetId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public Guid? ActorClientId { get; set; }
    public Guid? ClientId { get; set; }
}

public class DeleteFileMetadataResponse
{
    public Guid? FileId { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class RenameFileMetadataRequest
{
    public Guid BackupSetId { get; set; }
    public string OldRelativePath { get; set; } = string.Empty;
    public string NewRelativePath { get; set; } = string.Empty;
    public Guid? ActorClientId { get; set; }
    public Guid? ClientId { get; set; }
}

public class RenameFileMetadataResponse
{
    public Guid? FileId { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
