using Backup.Client.Web.Services;
using Backup.MetadataService;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
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

public class AutomaticSyncE2ETests : IAsyncLifetime
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

    public AutomaticSyncE2ETests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_sync_" + uniqueId);
        _clientDataDir = Path.Combine(Path.GetTempPath(), "e2e_client_data_sync_" + uniqueId);
        _sourceDir = Path.Combine(Path.GetTempPath(), "e2e_source_sync_" + uniqueId);

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_clientDataDir);
        Directory.CreateDirectory(_sourceDir);
    }

    public async Task InitializeAsync()
    {
        // 1. MetadataService
        _dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        _dbFactory = new TestDbContextFactory(_dbOptions);

        var metadataAppService = new MetadataApplicationService(_dbFactory, NullLogger<MetadataApplicationService>.Instance);
        var metaReader = new ProtocolFrameReader();
        var metaWriter = new ProtocolFrameWriter();
        var metaDispatcher = new MetadataMessageDispatcher(metadataAppService, metaReader, metaWriter, NullLogger<MetadataMessageDispatcher>.Instance);

        var metaOptions = Options.Create(new MetadataServiceOptions { Host = "127.0.0.1", Port = 0 });
        _metadataWorker = new MetadataServerWorker(
            NullLogger<MetadataServerWorker>.Instance,
            metaOptions,
            metaDispatcher,
            metaReader);

        _ = _metadataWorker.StartAsync(_cts.Token);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_metadataWorker.BoundPort == null || _metadataWorker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 8000) throw new TimeoutException("MetadataServerWorker failed to bind.");
            await Task.Delay(20);
        }
        _metadataPort = _metadataWorker.BoundPort.Value;

        // 2. StorageService
        var storageOptions = Options.Create(new StorageServiceOptions { Host = "127.0.0.1", Port = 0 });
        var storageService = new FileStorageService(NullLogger<FileStorageService>.Instance, _storageRoot);
        _storageWorker = new StorageServerWorker(
            NullLogger<StorageServerWorker>.Instance,
            NullLoggerFactory.Instance,
            storageOptions,
            storageService);

        _ = _storageWorker.StartAsync(_cts.Token);

        sw.Restart();
        while (_storageWorker.BoundPort == null || _storageWorker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 8000) throw new TimeoutException("StorageServerWorker failed to bind.");
            await Task.Delay(20);
        }
        _storagePort = _storageWorker.BoundPort.Value;

        // 3. SyncGateway
        var gatewayOptions = Options.Create(new SyncGateway.Configuration.GatewayOptions { Host = "127.0.0.1", Port = 0 });
        var downstreamOptions = Options.Create(new DownstreamServicesOptions
        {
            MetadataService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = _metadataPort },
            StorageService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = _storagePort }
        });

        _gatewayWorker = new GatewayServerWorker(
            NullLogger<GatewayServerWorker>.Instance,
            NullLoggerFactory.Instance,
            gatewayOptions,
            downstreamOptions);

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

        if (_gatewayWorker != null)
        {
            try { await _gatewayWorker.StopAsync(CancellationToken.None); } catch { }
        }
        if (_storageWorker != null)
        {
            try { await _storageWorker.StopAsync(CancellationToken.None); } catch { }
        }
        if (_metadataWorker != null)
        {
            try { await _metadataWorker.StopAsync(CancellationToken.None); } catch { }
        }

        try { Directory.Delete(_storageRoot, recursive: true); } catch { }
        try { Directory.Delete(_clientDataDir, recursive: true); } catch { }
        try { Directory.Delete(_sourceDir, recursive: true); } catch { }

        _cts.Dispose();
    }

    [Fact]
    public async Task Section30_FullAutomaticSyncLifecycle_Create_Modify_Rename_Delete_Recreate()
    {
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = testTimeoutCts.Token;

        // 1. Setup client components
        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var indexService = new LocalFileIndexService(Path.Combine(_clientDataDir, "index"));
        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var watcher = new FileSystemWatcherService(NullLogger<FileSystemWatcherService>.Instance);
        var stability = new FileStabilityHelper(NullLogger<FileStabilityHelper>.Instance);

        var syncEngine = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcher,
            stability,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 250);

        // 2. Register client & Create BackupSet
        var reg = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "E2E-Station",
            MachineName = Environment.MachineName
        }, token);
        Assert.True(reg.Success);
        await identityService.SaveStateAsync(reg.ClientId, Environment.MachineName, "E2E-Station", token);

        var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "AutoSyncSet_" + Guid.NewGuid().ToString("N")[..8],
            LocalRootPath = _sourceDir
        }, token);
        Assert.True(bs.Success);
        var backupSetId = bs.BackupSetId;

        // 3. Place a.txt BEFORE start of automatic sync
        string aPath = Path.Combine(_sourceDir, "a.txt");
        await File.WriteAllTextAsync(aPath, "Content A Version 1", token);

        // 4. Start automatic sync (triggers initial reconciliation)
        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = bs.Name,
            LocalRootPath = _sourceDir,
            AutomaticSyncEnabled = true
        };

        await syncEngine.StartAsync(profile, token);
        Assert.True(syncEngine.IsRunning);

        // 5. Verify a.txt -> version 1
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var aFile = await db.Files.FirstOrDefaultAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "a.txt", token);
            Assert.NotNull(aFile);
            Assert.Equal(1, aFile.CurrentVersionNumber);
            Assert.False(aFile.IsDeleted);
        }

        // 6. Create b.txt via live watcher
        string bPath = Path.Combine(_sourceDir, "b.txt");
        await File.WriteAllTextAsync(bPath, "Content B Initial", token);

        // Wait for debounce and sync
        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "b.txt", token);
            return f != null && f.CurrentVersionNumber == 1;
        }, TimeSpan.FromSeconds(6));

        // Verify b.txt is v1
        Guid bFileId;
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var bFile = await db.Files.FirstAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "b.txt", token);
            Assert.Equal(1, bFile.CurrentVersionNumber);
            Assert.False(bFile.IsDeleted);
            bFileId = bFile.Id;
        }

        // 7. Modify a.txt
        await File.WriteAllTextAsync(aPath, "Content A Version 2 Modified", token);

        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "a.txt", token);
            return f != null && f.CurrentVersionNumber == 2;
        }, TimeSpan.FromSeconds(6));

        // 8. Rename b.txt -> renamed.txt
        string renamedPath = Path.Combine(_sourceDir, "renamed.txt");
        File.Move(bPath, renamedPath);

        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "renamed.txt", token);
            return f != null && f.Id == bFileId;
        }, TimeSpan.FromSeconds(6));

        // Verify FileId preserved on rename
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var renamedFile = await db.Files.FirstAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "renamed.txt", token);
            Assert.Equal(bFileId, renamedFile.Id);
            Assert.False(renamedFile.IsDeleted);
        }

        // 9. Delete a.txt
        File.Delete(aPath);

        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "a.txt", token);
            return f != null && f.IsDeleted;
        }, TimeSpan.FromSeconds(6));

        // Verify versions 1 and 2 still exist
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var aFile = await db.Files.Include(f => f.Versions).FirstAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "a.txt", token);
            Assert.True(aFile.IsDeleted);
            Assert.Equal(2, aFile.Versions.Count);
        }

        // 10. Recreate a.txt with previous content (Section 16 verification)
        await File.WriteAllTextAsync(aPath, "Content A Version 1", token);

        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "a.txt", token);
            return f != null && !f.IsDeleted && f.CurrentVersionNumber == 3;
        }, TimeSpan.FromSeconds(6));

        // 11. Verify ChangeLog in PostgreSQL
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var changeLogs = await db.ChangeLogs
                .Where(c => c.BackupSetId == backupSetId)
                .OrderBy(c => c.CreatedAtUtc)
                .ToListAsync(token);

            Assert.True(changeLogs.Count >= 5);
            Assert.Contains(changeLogs, c => c.Action == "CREATE");
            Assert.Contains(changeLogs, c => c.Action == "MODIFY");
            Assert.Contains(changeLogs, c => c.Action == "RENAME");
            Assert.Contains(changeLogs, c => c.Action == "DELETE");
        }

        // 12. Stop automatic sync
        await syncEngine.StopAsync(token);
        Assert.False(syncEngine.IsRunning);
    }

    [Fact]
    public async Task Section31_InitialReconciliation_OfflineChangesHandledCorrectly()
    {
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = testTimeoutCts.Token;

        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state-recon.json"));
        var indexService = new LocalFileIndexService(Path.Combine(_clientDataDir, "index-recon"));
        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var watcher = new FileSystemWatcherService(NullLogger<FileSystemWatcherService>.Instance);
        var stability = new FileStabilityHelper(NullLogger<FileStabilityHelper>.Instance);

        var syncEngine = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcher,
            stability,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 250);

        // 1. Setup client and BackupSet
        var reg = await client.RegisterClientAsync(new RegisterClientRequest { Name = "OfflineStation" }, token);
        await identityService.SaveStateAsync(reg.ClientId, Environment.MachineName, "OfflineStation", token);

        string reconSource = Path.Combine(Path.GetTempPath(), "e2e_recon_source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reconSource);

        var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "ReconSet_" + Guid.NewGuid().ToString("N")[..8],
            LocalRootPath = reconSource
        }, token);
        var backupSetId = bs.BackupSetId;

        // 2. Pre-populate server with: old.txt, same.txt, changed.txt
        string oldPath = Path.Combine(reconSource, "old.txt");
        string samePath = Path.Combine(reconSource, "same.txt");
        string changedPath = Path.Combine(reconSource, "changed.txt");

        await File.WriteAllTextAsync(oldPath, "Old Content", token);
        await File.WriteAllTextAsync(samePath, "Same Content", token);
        await File.WriteAllTextAsync(changedPath, "Changed Content Original", token);

        await client.BackupFileAsync(backupSetId, "old.txt", oldPath, reg.ClientId, cancellationToken: token);
        await client.BackupFileAsync(backupSetId, "same.txt", samePath, reg.ClientId, cancellationToken: token);
        await client.BackupFileAsync(backupSetId, "changed.txt", changedPath, reg.ClientId, cancellationToken: token);

        // 3. Make offline modifications:
        // - Delete old.txt
        File.Delete(oldPath);
        // - same.txt remains untouched
        // - Modify changed.txt
        await File.WriteAllTextAsync(changedPath, "Changed Content Modified New", token);
        // - Create new.txt
        string newPath = Path.Combine(reconSource, "new.txt");
        await File.WriteAllTextAsync(newPath, "New Content", token);

        // 4. Run reconciliation via SyncEngine
        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = bs.Name,
            LocalRootPath = reconSource,
            AutomaticSyncEnabled = true
        };

        await syncEngine.StartAsync(profile, token);

        // 5. Verify database state
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var oldFile = await db.Files.FirstAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "old.txt", token);
            Assert.True(oldFile.IsDeleted, "old.txt must be marked as deleted");

            var sameFile = await db.Files.Include(f => f.Versions).FirstAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "same.txt", token);
            Assert.False(sameFile.IsDeleted);
            Assert.Equal(1, sameFile.CurrentVersionNumber);
            Assert.Single(sameFile.Versions); // No redundant version created

            var chFile = await db.Files.Include(f => f.Versions).FirstAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "changed.txt", token);
            Assert.False(chFile.IsDeleted);
            Assert.Equal(2, chFile.CurrentVersionNumber);
            Assert.Equal(2, chFile.Versions.Count);

            var nFile = await db.Files.Include(f => f.Versions).FirstAsync(f => f.BackupSetId == backupSetId && f.RelativePath == "new.txt", token);
            Assert.False(nFile.IsDeleted);
            Assert.Equal(1, nFile.CurrentVersionNumber);
            Assert.Single(nFile.Versions);
        }

        // Verify storage files exist
        Assert.True(Directory.GetFiles(_storageRoot, "*.*", SearchOption.AllDirectories).Length >= 4);

        await syncEngine.StopAsync(token);
        try { Directory.Delete(reconSource, recursive: true); } catch { }
    }

    private async Task WaitForDbConditionAsync(Func<MetadataDbContext, Task<bool>> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();
            if (await condition(db))
            {
                return;
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Database condition was not met within {timeout.TotalSeconds} seconds.");
    }
}
