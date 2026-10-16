namespace Backup.MetadataService.Entities;

public class ChangeLogEntity
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public Guid? FileId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid? ActorClientId { get; set; }

    public ClientEntity Client { get; set; } = null!;
    public ClientEntity? ActorClient { get; set; }
    public BackupSetEntity BackupSet { get; set; } = null!;
    public FileEntity? File { get; set; }
}
