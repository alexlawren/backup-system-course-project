using Backup.Shared.Storage;
using Xunit;

namespace Backup.Tests;

public class StorageConsistencyTests : IDisposable
{
    private readonly string _storageRoot;

    public StorageConsistencyTests()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), "consistency_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void StorageConsistencyVerifier_DetectsValidAndMissingBlobsCorrectly()
    {
        Guid clientId = Guid.NewGuid();
        Guid backupSetId = Guid.NewGuid();
        Guid fileId1 = Guid.NewGuid();
        Guid fileId2 = Guid.NewGuid();

        string key1 = StorageKeyHelper.BuildStorageKey(clientId, backupSetId, fileId1, 1);
        string key2 = StorageKeyHelper.BuildStorageKey(clientId, backupSetId, fileId2, 1);
        string key3 = StorageKeyHelper.BuildStorageKey(clientId, backupSetId, fileId1, 2);

        // Create blobs physically for key1 and key2, leave key3 missing
        StorageKeyHelper.TryResolvePhysicalPath(_storageRoot, key1, out string path1, out _);
        StorageKeyHelper.TryResolvePhysicalPath(_storageRoot, key2, out string path2, out _);

        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path2)!);
        File.WriteAllText(path1, "blob 1");
        File.WriteAllText(path2, "blob 2");

        var versions = new List<(string? StorageKey, int VersionNumber, string? RelativePath)>
        {
            (key1, 1, "file1.txt"),
            (key2, 1, "file2.txt"),
            (key3, 2, "file1.txt") // physical file not created
        };

        // Act
        var report = StorageConsistencyVerifier.Verify(_storageRoot, versions);

        // Assert
        Assert.Equal(3, report.TotalCommittedVersions);
        Assert.Equal(2, report.ValidBlobsCount);
        Assert.Equal(1, report.MissingBlobsCount);
        Assert.False(report.IsConsistent);
        Assert.Single(report.MissingBlobs);
        Assert.Equal(key3, report.MissingBlobs[0].StorageKey);
        Assert.Equal("file1.txt", report.MissingBlobs[0].RelativePath);
        Assert.Equal(2, report.MissingBlobs[0].VersionNumber);
    }

    [Fact]
    public void StorageConsistencyVerifier_AllBlobsPresent_ReportsIsConsistent()
    {
        Guid clientId = Guid.NewGuid();
        Guid backupSetId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        string key = StorageKeyHelper.BuildStorageKey(clientId, backupSetId, fileId, 1);
        StorageKeyHelper.TryResolvePhysicalPath(_storageRoot, key, out string path, out _);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");

        var versions = new List<(string? StorageKey, int VersionNumber, string? RelativePath)>
        {
            (key, 1, "file.txt")
        };

        // Act
        var report = StorageConsistencyVerifier.Verify(_storageRoot, versions);

        // Assert
        Assert.True(report.IsConsistent);
        Assert.Equal(1, report.TotalCommittedVersions);
        Assert.Equal(1, report.ValidBlobsCount);
        Assert.Equal(0, report.MissingBlobsCount);
        Assert.Empty(report.MissingBlobs);
    }
}
