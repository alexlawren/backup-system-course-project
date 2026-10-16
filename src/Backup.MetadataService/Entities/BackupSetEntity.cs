namespace Backup.MetadataService.Entities;

public class BackupSetEntity
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LocalRootPath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public string AccessMode { get; set; } = "PRIVATE";
    public string? ShareCode { get; set; }

    public ClientEntity Client { get; set; } = null!;
    public ICollection<FileEntity> Files { get; set; } = new List<FileEntity>();
    public ICollection<ChangeLogEntity> ChangeLogs { get; set; } = new List<ChangeLogEntity>();
    public ICollection<BackupSetMemberEntity> Members { get; set; } = new List<BackupSetMemberEntity>();
}
