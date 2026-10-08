namespace Backup.MetadataService.Configuration;

public class MetadataServiceOptions
{
    public const string SectionName = "ServiceOptions";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5001;
    public bool ApplyMigrationsOnStartup { get; set; } = false;
}
