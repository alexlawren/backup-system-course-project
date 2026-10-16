namespace Backup.Shared.Messages;

public class CreateBackupSetRequest
{
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LocalRootPath { get; set; } = string.Empty;
    public BackupSetAccessMode AccessMode { get; set; } = BackupSetAccessMode.Private;
}

public class CreateBackupSetResponse
{
    public Guid BackupSetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LocalRootPath { get; set; } = string.Empty;
    public BackupSetAccessMode AccessMode { get; set; } = BackupSetAccessMode.Private;
    public string? ShareCode { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
