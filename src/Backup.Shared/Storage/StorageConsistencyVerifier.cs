namespace Backup.Shared.Storage;

public record MissingBlobInfo(string StorageKey, string? RelativePath, int VersionNumber, string ExpectedPhysicalPath);

public record StorageConsistencyReport
{
    public int TotalCommittedVersions { get; init; }
    public int ValidBlobsCount { get; init; }
    public int MissingBlobsCount { get; init; }
    public IReadOnlyList<MissingBlobInfo> MissingBlobs { get; init; } = Array.Empty<MissingBlobInfo>();
    public bool IsConsistent => MissingBlobsCount == 0;
}

/// <summary>
/// Diagnostic consistency verification helper.
/// Verifies that all committed FileVersion records with a StorageKey have a corresponding physical blob on disk.
/// Strictly non-destructive: performs no deletion or repair.
/// </summary>
public static class StorageConsistencyVerifier
{
    public static StorageConsistencyReport Verify(
        string storageRootPath,
        IEnumerable<(string? StorageKey, int VersionNumber, string? RelativePath)> versions)
    {
        int total = 0;
        int valid = 0;
        var missing = new List<MissingBlobInfo>();

        foreach (var (storageKey, versionNumber, relativePath) in versions)
        {
            if (string.IsNullOrWhiteSpace(storageKey))
            {
                continue;
            }

            total++;

            if (StorageKeyHelper.TryResolvePhysicalPath(storageRootPath, storageKey, out var physicalPath, out _))
            {
                if (File.Exists(physicalPath))
                {
                    valid++;
                }
                else
                {
                    missing.Add(new MissingBlobInfo(storageKey, relativePath, versionNumber, physicalPath));
                }
            }
            else
            {
                missing.Add(new MissingBlobInfo(storageKey, relativePath, versionNumber, physicalPath));
            }
        }

        return new StorageConsistencyReport
        {
            TotalCommittedVersions = total,
            ValidBlobsCount = valid,
            MissingBlobsCount = missing.Count,
            MissingBlobs = missing
        };
    }
}
