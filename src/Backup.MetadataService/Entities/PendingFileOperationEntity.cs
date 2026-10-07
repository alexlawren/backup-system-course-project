namespace Backup.MetadataService.Entities;

public class PendingFileOperationEntity
{
    public Guid OperationId { get; set; }
    public Guid FileId { get; set; }
    public int ExpectedVersionNumber { get; set; }
    public long ExpectedSize { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    public FileEntity File { get; set; } = null!;
}
