namespace Backup.Shared.Messages;

public class DeleteFileMetadataRequest
{
    public Guid BackupSetId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
}

public class DeleteFileMetadataResponse
{
    public Guid? FileId { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public class RenameFileMetadataRequest
{
    public Guid BackupSetId { get; set; }
    public string OldRelativePath { get; set; } = string.Empty;
    public string NewRelativePath { get; set; } = string.Empty;
}

public class RenameFileMetadataResponse
{
    public Guid? FileId { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
