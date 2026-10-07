namespace Backup.MetadataService.Entities;

public class FileEntity
{
    public Guid Id { get; set; }
    public Guid BackupSetId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public int CurrentVersionNumber { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public BackupSetEntity BackupSet { get; set; } = null!;
    public ICollection<FileVersionEntity> Versions { get; set; } = new List<FileVersionEntity>();
    public ICollection<PendingFileOperationEntity> PendingOperations { get; set; } = new List<PendingFileOperationEntity>();
    public ICollection<ChangeLogEntity> ChangeLogs { get; set; } = new List<ChangeLogEntity>();
}
