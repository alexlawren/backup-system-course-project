using Backup.Client.Web.Services;
using Backup.MetadataService;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Services;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.StorageService;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Backup.SyncGateway;
using Backup.SyncGateway.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backup.Tests;

public class RestartRecoveryTests : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly string _storageRoot;
    private readonly string _clientDataDir;
    private readonly string _sourceDir;

    private readonly CancellationTokenSource _cts = new();

    private MetadataServerWorker? _metadataWorker;
    private StorageServerWorker? _storageWorker;
    private GatewayServerWorker? _gatewayWorker;

    private int _metadataPort;
    private int _storagePort;
    private int _gatewayPort;

    private DbContextOptions<MetadataDbContext>? _dbOptions;
    private TestDbContextFactory? _dbFactory;

    public RestartRecoveryTests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_restart_" + uniqueId);
        _clientDataDir = Path.Combine(Path.GetTempPath(), "e2e_client_restart_" + uniqueId);
        _sourceDir = Path.Combine(Path.GetTempPath(), "e2e_source_restart_" + uniqueId);

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_clientDataDir);
        Directory.CreateDirectory(_sourceDir);
    }

    public async Task InitializeAsync()
    {
        _dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        _dbFactory = new TestDbContextFactory(_dbOptions);

        var metadataAppService = new MetadataApplicationService(_dbFactory, NullLogger<MetadataApplicationService>.Instance);
        var metaReader = new ProtocolFrameReader();
        var metaWriter = new ProtocolFrameWriter();
        var metaDispatcher = new MetadataMessageDispatcher(metadataAppService, metaReader, metaWriter, NullLogger<MetadataMessageDispatcher>.Instance);

        var metaOptions = Options.Create(new MetadataServiceOptions { Host = "127.0.0.1", Port = 0 });
        _metadataWorker = new MetadataServerWorker(NullLogger<MetadataServerWorker>.Instance, metaOptions, metaDispatcher, metaReader);
        _ = _metadataWorker.StartAsync(_cts.Token);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_metadataWorker.BoundPort == null || _metadataWorker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 8000) throw new TimeoutException("MetadataServerWorker failed to bind.");
            await Task.Delay(20);
        }
        _metadataPort = _metadataWorker.BoundPort.Value;

        var storageOptions = Options.Create(new StorageServiceOptions { Host = "127.0.0.1", Port = 0 });
        var storageService = new FileStorageService(NullLogger<FileStorageService>.Instance, _storageRoot);
        _storageWorker = new StorageServerWorker(NullLogger<StorageServerWorker>.Instance, NullLoggerFactory.Instance, storageOptions, storageService);
        _ = _storageWorker.StartAsync(_cts.Token);

        sw.Restart();
        while (_storageWorker.BoundPort == null || _storageWorker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 8000) throw new TimeoutException("StorageServerWorker failed to bind.");
            await Task.Delay(20);
        }
        _storagePort = _storageWorker.BoundPort.Value;

        var gatewayOptions = Options.Create(new SyncGateway.Configuration.GatewayOptions { Host = "127.0.0.1", Port = 0 });
        var downstreamOptions = Options.Create(new DownstreamServicesOptions
        {
            MetadataService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = _metadataPort },
            StorageService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = _storagePort }
        });

        _gatewayWorker = new GatewayServerWorker(NullLogger<GatewayServerWorker>.Instance, NullLoggerFactory.Instance, gatewayOptions, downstreamOptions);
        _ = _gatewayWorker.StartAsync(_cts.Token);

        sw.Restart();
        while (_gatewayWorker.BoundPort == null || _gatewayWorker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 8000) throw new TimeoutException("GatewayServerWorker failed to bind.");
            await Task.Delay(20);
        }
        _gatewayPort = _gatewayWorker.BoundPort.Value;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();

        if (_gatewayWorker != null) try { await _gatewayWorker.StopAsync(CancellationToken.None); } catch { }
        if (_storageWorker != null) try { await _storageWorker.StopAsync(CancellationToken.None); } catch { }
        if (_metadataWorker != null) try { await _metadataWorker.StopAsync(CancellationToken.None); } catch { }

        try { Directory.Delete(_storageRoot, recursive: true); } catch { }
        try { Directory.Delete(_clientDataDir, recursive: true); } catch { }
        try { Directory.Delete(_sourceDir, recursive: true); } catch { }

        _cts.Dispose();
    }

    [Fact]
    public async Task OfflineModification_DetectedAndCommitted_UponRestartReconciliation()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = testCts.Token;

        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var backupService = new BackupClientService(client, identityService);

        await backupService.EnsureRegisteredAsync(token);
        var reg = await backupService.GetIdentityAsync(token);
        var setResp = await backupService.CreateBackupSetAsync("RestartTestSet", _sourceDir, token);
        Guid backupSetId = setResp.BackupSetId;

        // Write initial file
        string filePath = Path.Combine(_sourceDir, "offline_test.txt");
        File.WriteAllText(filePath, "Version 1 content");

        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = "RestartTestSet",
            LocalRootPath = _sourceDir,
            AutomaticSyncEnabled = true
        };

        var indexService = new LocalFileIndexService(_clientDataDir);
        var scanner = new DirectoryScanner();
        var watcherService = new FileSystemWatcherService();
        var stabilityHelper = new FileStabilityHelper();

        var syncEngine1 = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcherService,
            stabilityHelper,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 200);

        // 1. Initial sync runs
        await syncEngine1.StartAsync(profile, token);
        Assert.False(syncEngine1.ReconciliationRequired);

        // Verify version 1 exists on server
        var filesResp1 = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
        Assert.Single(filesResp1.Files);
        Assert.Equal(1, filesResp1.Files[0].CurrentVersionNumber);

        // 2. Stop sync engine completely
        await syncEngine1.StopAsync(token);
        await syncEngine1.DisposeAsync();

        // 3. Modify file while offline
        await Task.Delay(100, token); // ensure timestamp advances
        File.WriteAllText(filePath, "Version 2 modified offline!");

        // 4. Create and start a fresh SyncEngine instance (simulating client app restart)
        var syncEngine2 = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            new FileSystemWatcherService(),
            stabilityHelper,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 200);

        await syncEngine2.StartAsync(profile, token);

        // 5. Verify reconciliation detected the change and committed version 2
        var filesResp2 = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
        Assert.Single(filesResp2.Files);
        Assert.Equal(2, filesResp2.Files[0].CurrentVersionNumber);

        var versionsResp = await client.GetVersionsAsync(new GetVersionsRequest
        {
            FileId = filesResp2.Files[0].FileId
        }, token);
        Assert.Equal(2, versionsResp.Versions.Count);

        await syncEngine2.StopAsync(token);
        await syncEngine2.DisposeAsync();
    }
}
