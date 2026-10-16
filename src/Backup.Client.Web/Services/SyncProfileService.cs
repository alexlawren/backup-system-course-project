using System.Text.Json;

namespace Backup.Client.Web.Services;

public class SyncProfile
{
    public Guid BackupSetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LocalRootPath { get; set; } = string.Empty;
    public bool AutomaticSyncEnabled { get; set; }
    public Backup.Shared.Messages.BackupSetAccessMode AccessMode { get; set; } = Backup.Shared.Messages.BackupSetAccessMode.Private;
    public string? ShareCode { get; set; }
}

public interface ISyncProfileService
{
    Task<SyncProfile?> GetProfileAsync(CancellationToken cancellationToken = default);
    Task SaveProfileAsync(SyncProfile profile, CancellationToken cancellationToken = default);
    Task ClearProfileAsync(CancellationToken cancellationToken = default);
}

public class SyncProfileService : ISyncProfileService
{
    private readonly string _profileFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SyncProfileService(string? baseDirectory = null)
    {
        string baseDir = baseDirectory ?? ResolveDefaultDataClientDir();
        Directory.CreateDirectory(baseDir);
        _profileFilePath = Path.Combine(baseDir, "sync-profile.json");
    }

    private static string ResolveDefaultDataClientDir()
    {
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(current, "data", "client"));
            }
            current = Directory.GetParent(current)?.FullName;
        }

        string? baseDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(baseDir, "data", "client"));
            }
            baseDir = Directory.GetParent(baseDir)?.FullName;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data", "client"));
    }

    public async Task<SyncProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_profileFilePath))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(_profileFilePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SyncProfile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveProfileAsync(SyncProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            string? directory = Path.GetDirectoryName(_profileFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempFile = $"{_profileFilePath}.tmp.{Guid.NewGuid():N}";
            string json = JsonSerializer.Serialize(profile, JsonOptions);
            await File.WriteAllTextAsync(tempFile, json, cancellationToken);

            File.Move(tempFile, _profileFilePath, overwrite: true);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ClearProfileAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_profileFilePath))
            {
                File.Delete(_profileFilePath);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
