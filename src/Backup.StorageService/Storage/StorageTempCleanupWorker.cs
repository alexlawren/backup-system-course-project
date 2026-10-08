using Backup.StorageService.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.StorageService.Storage;

/// <summary>
/// Background worker that periodically removes stale temporary upload artifacts (.tmp/*.part).
/// Never touches committed immutable versions in clients/.../versions/.
/// </summary>
public class StorageTempCleanupWorker : BackgroundService
{
    private readonly IFileStorageService _storageService;
    private readonly ILogger<StorageTempCleanupWorker> _logger;
    private readonly StorageTempCleanupOptions _options;

    public StorageTempCleanupWorker(
        IFileStorageService storageService,
        ILogger<StorageTempCleanupWorker> logger,
        IOptions<StorageTempCleanupOptions> options)
    {
        _storageService = storageService;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Executes a single cleanup pass, removing .tmp/*.part older than configured MaxAgeHours.
    /// Returns the number of temporary files deleted.
    /// </summary>
    public int CleanupStaleTempFiles()
    {
        var maxAge = TimeSpan.FromHours(Math.Max(0.01, _options.MaxAgeHours));
        var count = _storageService.CleanupExpiredTempFiles(maxAge);
        if (count > 0)
        {
            _logger.LogInformation("[StorageService] Cleaned up {Count} expired temporary file artifact(s).", count);
        }
        return count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[StorageService] Temp cleanup worker started (Interval: {Minutes}m, MaxAge: {Hours}h).",
            _options.CleanupIntervalMinutes,
            _options.MaxAgeHours);

        // Run an initial cleanup on startup
        try
        {
            CleanupStaleTempFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StorageService] Initial temporary file cleanup pass encountered an error.");
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                CleanupStaleTempFiles();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StorageService] Error during temporary file cleanup.");
            }
        }
    }
}
