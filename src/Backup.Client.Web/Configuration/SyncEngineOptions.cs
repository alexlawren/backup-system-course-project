namespace Backup.Client.Web.Configuration;

public class SyncEngineOptions
{
    public const string SectionName = "SyncEngineOptions";

    public int SharedSyncIntervalSeconds { get; set; } = 5;
    public int DebounceDelayMs { get; set; } = 750;
}
