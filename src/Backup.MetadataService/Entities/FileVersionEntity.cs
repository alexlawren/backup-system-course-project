namespace Backup.MetadataService.Entities;

public class FileVersionEntity
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string? StorageKey { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public FileEntity File { get; set; } = null!;
}
