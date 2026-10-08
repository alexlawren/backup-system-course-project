using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backup.Client.Web.Services;

public class ClientStartupSyncWorker : BackgroundService
{
    private readonly ISyncProfileService _profileService;
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger<ClientStartupSyncWorker> _logger;

    public ClientStartupSyncWorker(
        ISyncProfileService profileService,
        ISyncEngine syncEngine,
        ILogger<ClientStartupSyncWorker> logger)
    {
        _profileService = profileService;
        _syncEngine = syncEngine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Give web host a brief moment to initialize
            await Task.Delay(500, stoppingToken);

            var profile = await _profileService.GetProfileAsync(stoppingToken);
            if (profile == null)
            {
                _logger.LogInformation("[ClientStartup] No saved sync profile found. Automatic synchronization idle.");
                return;
            }

            if (!profile.AutomaticSyncEnabled)
            {
                _logger.LogInformation("[ClientStartup] Saved sync profile '{Name}' has automatic sync disabled.", profile.Name);
                return;
            }

            if (string.IsNullOrWhiteSpace(profile.LocalRootPath) || !Directory.Exists(profile.LocalRootPath))
            {
                _logger.LogWarning("[ClientStartup] Saved local directory '{Path}' does not exist on disk. Automatic sync will not start.", profile.LocalRootPath);
                return;
            }

            _logger.LogInformation("[ClientStartup] Auto-starting sync for profile '{Name}' on directory '{Path}'", profile.Name, profile.LocalRootPath);
            await _syncEngine.StartAsync(profile, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClientStartup] Error during startup synchronization initialization: {Message}", ex.Message);
        }
    }
}
