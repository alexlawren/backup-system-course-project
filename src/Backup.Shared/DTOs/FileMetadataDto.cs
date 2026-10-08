namespace Backup.Shared.DTOs;

public class FileMetadataDto
{
    public Guid FileId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public int CurrentVersionNumber { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public long? CurrentSize { get; set; }
    public string? CurrentSha256 { get; set; }
}
