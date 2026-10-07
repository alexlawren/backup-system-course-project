namespace Backup.StorageService.Configuration;

public class StorageServiceOptions
{
    public const string SectionName = "ServiceOptions";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5002;
}
