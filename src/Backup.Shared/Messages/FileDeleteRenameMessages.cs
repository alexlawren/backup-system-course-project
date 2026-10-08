namespace Backup.Shared.Messages;

public class FileDeletedRequest
{
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
}

public class FileDeletedResponse
{
    public Guid? FileId { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public class FileRenamedRequest
{
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public string OldRelativePath { get; set; } = string.Empty;
    public string NewRelativePath { get; set; } = string.Empty;
}

public class FileRenamedResponse
{
    public Guid? FileId { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
