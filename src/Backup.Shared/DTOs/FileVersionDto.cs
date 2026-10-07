namespace Backup.Shared.DTOs;

public class FileVersionDto
{
    public int VersionNumber { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string? StorageKey { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
