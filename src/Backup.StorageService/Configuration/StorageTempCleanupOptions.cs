namespace Backup.StorageService.Configuration;

public class StorageTempCleanupOptions
{
    public const string SectionName = "StorageTempCleanup";

    /// <summary>
    /// Age threshold in hours beyond which .tmp/*.part files are considered stale. Default is 1 hour.
    /// </summary>
    public int MaxAgeHours { get; set; } = 1;

    /// <summary>
    /// Interval in minutes between background cleanup checks. Default is 60 minutes.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 60;
}
