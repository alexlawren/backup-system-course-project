namespace Backup.MetadataService.Configuration;

public class PendingOperationsCleanupOptions
{
    public const string SectionName = "PendingOperationsCleanup";

    /// <summary>
    /// Interval in seconds between cleanup passes of expired pending file operations. Default is 300 seconds (5 minutes).
    /// </summary>
    public int CleanupIntervalSeconds { get; set; } = 300;
}
