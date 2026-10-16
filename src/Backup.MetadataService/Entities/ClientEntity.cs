namespace Backup.MetadataService.Entities;

public class ClientEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    public ICollection<BackupSetEntity> BackupSets { get; set; } = new List<BackupSetEntity>();
    public ICollection<ChangeLogEntity> ChangeLogs { get; set; } = new List<ChangeLogEntity>();
    public ICollection<BackupSetMemberEntity> Memberships { get; set; } = new List<BackupSetMemberEntity>();
}
