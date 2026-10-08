using System.Text.Json;
using Backup.Client.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class LocalFileIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _indexDir;

    public LocalFileIndexTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "backup_index_tests_" + Guid.NewGuid().ToString("N"));
        _indexDir = Path.Combine(_testDir, "index");
        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(_indexDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Section28_CacheReused_WhenSizeAndLastWriteTimeUtcUnchanged()
    {
        string localFolder = Path.Combine(_testDir, "source");
        Directory.CreateDirectory(localFolder);

        string filePath = Path.Combine(localFolder, "file.txt");
        await File.WriteAllTextAsync(filePath, "Hello World Content");
        var fileInfo = new FileInfo(filePath);

        var backupSetId = Guid.NewGuid();
        var indexService = new LocalFileIndexService(_indexDir);

        var index = new LocalFileIndex { BackupSetId = backupSetId };
        string fakeHash = "cached_fake_sha256";
        index.Entries["file.txt"] = new LocalFileIndexEntry
        {
            RelativePath = "file.txt",
            Size = fileInfo.Length,
            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
            Sha256 = fakeHash
        };
        await indexService.SaveIndexAsync(index);

        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var loadedIndex = await indexService.LoadIndexAsync(backupSetId);

        var result = await scanner.ScanDirectoryAsync(localFolder, loadedIndex);

        // Should reuse the cached hash without recalculating
        Assert.Equal(1, result.CacheHitCount);
        Assert.Equal(0, result.HashComputedCount);
        var entry = Assert.Single(result.ManifestEntries);
        Assert.Equal(fakeHash, entry.Sha256);
    }

    [Fact]
    public async Task Section28_HashRecalculated_WhenSizeChanges()
    {
        string localFolder = Path.Combine(_testDir, "source2");
        Directory.CreateDirectory(localFolder);

        string filePath = Path.Combine(localFolder, "file.txt");
        await File.WriteAllTextAsync(filePath, "Initial Content");
        var fileInfo = new FileInfo(filePath);

        var backupSetId = Guid.NewGuid();
        var indexService = new LocalFileIndexService(_indexDir);

        var index = new LocalFileIndex { BackupSetId = backupSetId };
        index.Entries["file.txt"] = new LocalFileIndexEntry
        {
            RelativePath = "file.txt",
            Size = fileInfo.Length,
            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
            Sha256 = "old_cached_hash"
        };
        await indexService.SaveIndexAsync(index);

        // Append to file so size changes
        await File.AppendAllTextAsync(filePath, " Modified with longer size");

        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var loadedIndex = await indexService.LoadIndexAsync(backupSetId);

        var result = await scanner.ScanDirectoryAsync(localFolder, loadedIndex);

        Assert.Equal(0, result.CacheHitCount);
        Assert.Equal(1, result.HashComputedCount);
        var entry = Assert.Single(result.ManifestEntries);
        Assert.NotEqual("old_cached_hash", entry.Sha256);
    }

    [Fact]
    public async Task Section28_HashRecalculated_WhenLastWriteTimeUtcChanges()
    {
        string localFolder = Path.Combine(_testDir, "source3");
        Directory.CreateDirectory(localFolder);

        string filePath = Path.Combine(localFolder, "file.txt");
        await File.WriteAllTextAsync(filePath, "Same Size");
        var fileInfo = new FileInfo(filePath);

        var backupSetId = Guid.NewGuid();
        var indexService = new LocalFileIndexService(_indexDir);

        var index = new LocalFileIndex { BackupSetId = backupSetId };
        index.Entries["file.txt"] = new LocalFileIndexEntry
        {
            RelativePath = "file.txt",
            Size = fileInfo.Length,
            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc.AddMinutes(-10), // older timestamp cached
            Sha256 = "old_hash"
        };
        await indexService.SaveIndexAsync(index);

        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var loadedIndex = await indexService.LoadIndexAsync(backupSetId);

        var result = await scanner.ScanDirectoryAsync(localFolder, loadedIndex);

        Assert.Equal(0, result.CacheHitCount);
        Assert.Equal(1, result.HashComputedCount);
    }

    [Fact]
    public async Task Section28_CorruptedCacheRebuiltSafely()
    {
        var backupSetId = Guid.NewGuid();
        var indexService = new LocalFileIndexService(_indexDir);
        string indexPath = indexService.GetIndexPath(backupSetId);

        // Write completely corrupt JSON
        await File.WriteAllTextAsync(indexPath, "{ corrupted: not valid json at all [[[[");

        // Should not throw, should return empty index
        var loaded = await indexService.LoadIndexAsync(backupSetId);
        Assert.NotNull(loaded);
        Assert.Equal(backupSetId, loaded.BackupSetId);
        Assert.Empty(loaded.Entries);

        // Can safely save new index over it
        loaded.Entries["test.txt"] = new LocalFileIndexEntry
        {
            RelativePath = "test.txt",
            Size = 10,
            LastWriteTimeUtc = DateTime.UtcNow,
            Sha256 = "valid_hash"
        };
        await indexService.SaveIndexAsync(loaded);

        var reloaded = await indexService.LoadIndexAsync(backupSetId);
        Assert.Single(reloaded.Entries);
    }

    [Fact]
    public async Task Section28_RemovedLocalFile_RemovedFromRebuiltIndex()
    {
        string localFolder = Path.Combine(_testDir, "source4");
        Directory.CreateDirectory(localFolder);

        // Only create remaining.txt on disk
        await File.WriteAllTextAsync(Path.Combine(localFolder, "remaining.txt"), "I remain");

        var backupSetId = Guid.NewGuid();
        var indexService = new LocalFileIndexService(_indexDir);

        // Preload index with removed.txt and remaining.txt
        var index = new LocalFileIndex { BackupSetId = backupSetId };
        index.Entries["removed.txt"] = new LocalFileIndexEntry { RelativePath = "removed.txt", Size = 5, LastWriteTimeUtc = DateTime.UtcNow, Sha256 = "h1" };
        index.Entries["remaining.txt"] = new LocalFileIndexEntry { RelativePath = "remaining.txt", Size = 8, LastWriteTimeUtc = DateTime.UtcNow, Sha256 = "h2" };
        await indexService.SaveIndexAsync(index);

        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var scanResult = await scanner.ScanDirectoryAsync(localFolder, index);

        // Scanned entries should only contain remaining.txt
        Assert.Single(scanResult.ManifestEntries);
        Assert.Equal("remaining.txt", scanResult.ManifestEntries[0].RelativePath);
        Assert.DoesNotContain("removed.txt", scanResult.ScannedIndexEntries.Keys);
    }

    [Fact]
    public async Task Section28_AtomicCacheWrite_DoesNotLeaveTargetHalfWritten()
    {
        var backupSetId = Guid.NewGuid();
        var indexService = new LocalFileIndexService(_indexDir);
        string finalPath = indexService.GetIndexPath(backupSetId);

        var index = new LocalFileIndex { BackupSetId = backupSetId };
        for (int i = 0; i < 500; i++)
        {
            index.Entries[$"file_{i}.txt"] = new LocalFileIndexEntry
            {
                RelativePath = $"file_{i}.txt",
                Size = i * 10,
                LastWriteTimeUtc = DateTime.UtcNow,
                Sha256 = new string('a', 64)
            };
        }

        await indexService.SaveIndexAsync(index);

        Assert.True(File.Exists(finalPath));
        string content = await File.ReadAllTextAsync(finalPath);
        var deserialized = JsonSerializer.Deserialize<LocalFileIndex>(content);
        Assert.NotNull(deserialized);
        Assert.Equal(500, deserialized.Entries.Count);
    }
}
