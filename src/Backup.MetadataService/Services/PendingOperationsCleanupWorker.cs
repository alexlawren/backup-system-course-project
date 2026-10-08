using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.MetadataService.Services;

/// <summary>
/// Background worker that periodically removes expired reservations from pending_file_operations.
/// Never deletes committed FileVersion records and never alters CurrentVersionNumber.
/// </summary>
public class PendingOperationsCleanupWorker : BackgroundService
{
    private readonly IDbContextFactory<MetadataDbContext> _dbContextFactory;
    private readonly ILogger<PendingOperationsCleanupWorker> _logger;
    private readonly PendingOperationsCleanupOptions _options;

    public PendingOperationsCleanupWorker(
        IDbContextFactory<MetadataDbContext> dbContextFactory,
        ILogger<PendingOperationsCleanupWorker> logger,
        IOptions<PendingOperationsCleanupOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Executes a single cleanup pass, removing all pending operations whose ExpiresAtUtc &lt; UtcNow.
    /// Returns the number of expired reservations deleted.
    /// </summary>
    public async Task<int> CleanupStaleOperationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var stale = await db.PendingFileOperations
            .Where(p => p.ExpiresAtUtc < now)
            .ToListAsync(cancellationToken);

        if (stale.Count > 0)
        {
            db.PendingFileOperations.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[MetadataService] Cleaned up {Count} expired pending file operation reservation(s).", stale.Count);
        }

        return stale.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.CleanupIntervalSeconds));
        _logger.LogInformation("[MetadataService] Stale pending operations cleanup worker started (Interval: {IntervalSeconds}s).", _options.CleanupIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await CleanupStaleOperationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MetadataService] Error during stale pending operations cleanup.");
            }
        }
    }
}
