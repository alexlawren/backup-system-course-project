namespace Backup.MetadataService.Entities;

public class BackupSetMemberEntity
{
    public Guid BackupSetId { get; set; }
    public Guid ClientId { get; set; }
    public string Role { get; set; } = "MEMBER";
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }

    public BackupSetEntity BackupSet { get; set; } = null!;
    public ClientEntity Client { get; set; } = null!;
}
