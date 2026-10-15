using System.Text.Json;

namespace Backup.Client.Web.Services;

public class LocalFileIndexEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public class LocalFileIndex
{
    public Guid BackupSetId { get; set; }
    public Dictionary<string, LocalFileIndexEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface ILocalFileIndexService
{
    Task<LocalFileIndex> LoadIndexAsync(Guid backupSetId, CancellationToken cancellationToken = default);
    Task SaveIndexAsync(LocalFileIndex index, CancellationToken cancellationToken = default);
    Task DeleteIndexAsync(Guid backupSetId, CancellationToken cancellationToken = default);
}

public class LocalFileIndexService : ILocalFileIndexService
{
    private readonly string _indexDirectory;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LocalFileIndexService(string? baseDirectory = null)
    {
        _indexDirectory = baseDirectory ?? ResolveDefaultIndexDir();
        Directory.CreateDirectory(_indexDirectory);
    }

    private static string ResolveDefaultIndexDir()
    {
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(current, "data", "client", "index"));
            }
            current = Directory.GetParent(current)?.FullName;
        }

        string? baseDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(baseDir, "data", "client", "index"));
            }
            baseDir = Directory.GetParent(baseDir)?.FullName;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data", "client", "index"));
    }

    public string GetIndexPath(Guid backupSetId) => Path.Combine(_indexDirectory, $"{backupSetId}.json");

    public async Task<LocalFileIndex> LoadIndexAsync(Guid backupSetId, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            string filePath = GetIndexPath(backupSetId);
            if (!File.Exists(filePath))
            {
                return new LocalFileIndex { BackupSetId = backupSetId };
            }

            try
            {
                string json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var loaded = JsonSerializer.Deserialize<LocalFileIndex>(json, JsonOptions);
                if (loaded == null)
                {
                    return new LocalFileIndex { BackupSetId = backupSetId };
                }

                var index = new LocalFileIndex
                {
                    BackupSetId = loaded.BackupSetId,
                    Entries = new Dictionary<string, LocalFileIndexEntry>(loaded.Entries, StringComparer.OrdinalIgnoreCase)
                };
                return index;
            }
            catch
            {
                // If index file is corrupted, safely return an empty index to allow rebuilding via directory scan
                return new LocalFileIndex { BackupSetId = backupSetId };
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveIndexAsync(LocalFileIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_indexDirectory);
            string finalPath = GetIndexPath(index.BackupSetId);
            string tempPath = $"{finalPath}.tmp.{Guid.NewGuid():N}";

            string json = JsonSerializer.Serialize(index, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);

            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeleteIndexAsync(Guid backupSetId, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            string filePath = GetIndexPath(backupSetId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
