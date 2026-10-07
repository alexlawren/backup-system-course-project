namespace Backup.StorageService.Configuration;

public class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = "data/storage";
}
