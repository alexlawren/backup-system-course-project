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

public class RestoreE2ETests : IAsyncLifetime
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

    public RestoreE2ETests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_restore_" + uniqueId);
        _clientDataDir = Path.Combine(Path.GetTempPath(), "e2e_client_data_restore_" + uniqueId);
        _sourceDir = Path.Combine(Path.GetTempPath(), "e2e_source_restore_" + uniqueId);

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
    public async Task Section36_FullRestoreLifecycle_V1_V2_V3_RestoreV1_CreatesV4_NoWatcherDuplicate()
    {
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = testTimeoutCts.Token;

        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var indexService = new LocalFileIndexService(Path.Combine(_clientDataDir, "index"));
        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var watcher = new FileSystemWatcherService(NullLogger<FileSystemWatcherService>.Instance);
        var stability = new FileStabilityHelper(NullLogger<FileStabilityHelper>.Instance);
        var suppressionService = new RestoreSuppressionService();

        var syncEngine = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcher,
            stability,
            NullLogger<SyncEngine>.Instance,
            suppressionService: suppressionService,
            debounceDelayMs: 200);

        // 1. Register Client and create BackupSet
        var reg = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "RestoreStation",
            MachineName = Environment.MachineName
        }, token);
        Assert.True(reg.Success);
        await identityService.SaveStateAsync(reg.ClientId, Environment.MachineName, "RestoreStation", token);

        var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "RestoreSet_" + Guid.NewGuid().ToString("N")[..8],
            LocalRootPath = _sourceDir
        }, token);
        Assert.True(bs.Success);
        var backupSetId = bs.BackupSetId;

        // Step 1: Create file.txt = "version one"
        string filePath = Path.Combine(_sourceDir, "file.txt");
        await File.WriteAllTextAsync(filePath, "version one", token);

        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = bs.Name,
            LocalRootPath = _sourceDir,
            AutomaticSyncEnabled = true
        };

        // Step 2: Initial sync -> v1
        await syncEngine.StartAsync(profile, token);
        Assert.True(syncEngine.IsRunning);

        Guid fileId;
        string v1Sha;
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var f = await db.Files.Include(x => x.Versions).FirstOrDefaultAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "file.txt", token);
            Assert.NotNull(f);
            Assert.Equal(1, f.CurrentVersionNumber);
            fileId = f.Id;
            v1Sha = f.Versions.First(v => v.VersionNumber == 1).Sha256;
        }

        // Step 3-4: Modify -> "version two", automatic sync -> v2
        await File.WriteAllTextAsync(filePath, "version two", token);
        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId, token);
            return f != null && f.CurrentVersionNumber == 2;
        }, TimeSpan.FromSeconds(6));

        // Step 5-6: Modify -> "version three", automatic sync -> v3
        await File.WriteAllTextAsync(filePath, "version three", token);
        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId, token);
            return f != null && f.CurrentVersionNumber == 3;
        }, TimeSpan.FromSeconds(6));

        // Step 7-8: Select version 1 and Restore
        var restoreResult = await syncEngine.RestoreVersionAsync(backupSetId, fileId, 1, token);
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.Equal("SUCCESS", restoreResult.Status);
        Assert.Equal(4, restoreResult.NewCurrentVersionNumber);
        Assert.Equal(v1Sha, restoreResult.Sha256Verified);

        // Verify local file content
        string restoredContent = await File.ReadAllTextAsync(filePath, token);
        Assert.Equal("version one", restoredContent);

        // Verify DB state
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var f = await db.Files.Include(x => x.Versions).FirstAsync(x => x.Id == fileId, token);
            Assert.Equal(4, f.CurrentVersionNumber);
            Assert.False(f.IsDeleted);
            Assert.Equal(4, f.Versions.Count);

            var v1 = f.Versions.First(v => v.VersionNumber == 1);
            var v2 = f.Versions.First(v => v.VersionNumber == 2);
            var v3 = f.Versions.First(v => v.VersionNumber == 3);
            var v4 = f.Versions.First(v => v.VersionNumber == 4);

            Assert.Equal(v1Sha, v4.Sha256);
            Assert.Equal(v1.Size, v4.Size);

            // Verify ChangeLogs
            var logs = await db.ChangeLogs
                .Where(cl => cl.FileId == fileId)
                .OrderBy(cl => cl.CreatedAtUtc)
                .ToListAsync(token);

            Assert.True(logs.Count >= 4);
            var actions = logs.Select(l => l.Action).ToList();
            Assert.Equal("CREATE", actions[0]);
            Assert.Equal("MODIFY", actions[1]);
            Assert.Equal("MODIFY", actions[2]);
            Assert.Equal("RESTORE", actions[3]);
        }

        // Verify physical storage: v1, v2, v3, v4 blobs exist
        string[] storedBlobs = Directory.GetFiles(_storageRoot, "*.*", SearchOption.AllDirectories);
        Assert.True(storedBlobs.Length >= 3, "At least distinct blobs for v1, v2, v3 should exist in storage.");

        // Wait a short time to verify Watcher echo was suppressed and did not accidentally create v5
        await Task.Delay(1000, token);

        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var f = await db.Files.Include(x => x.Versions).FirstAsync(x => x.Id == fileId, token);
            Assert.Equal(4, f.CurrentVersionNumber);
            Assert.Equal(4, f.Versions.Count);
        }

        await syncEngine.StopAsync(token);
    }

    [Fact]
    public async Task Section37_UnsyncedLocalChangeE2E_BackedUpBeforeRestore_ZeroDataLoss()
    {
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = testTimeoutCts.Token;

        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var indexService = new LocalFileIndexService(Path.Combine(_clientDataDir, "index"));
        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var watcher = new FileSystemWatcherService(NullLogger<FileSystemWatcherService>.Instance);
        var stability = new FileStabilityHelper(NullLogger<FileStabilityHelper>.Instance);
        var suppressionService = new RestoreSuppressionService();

        var syncEngine = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcher,
            stability,
            NullLogger<SyncEngine>.Instance,
            suppressionService: suppressionService,
            debounceDelayMs: 200);

        var reg = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "UnsyncedEditStation",
            MachineName = Environment.MachineName
        }, token);
        await identityService.SaveStateAsync(reg.ClientId, Environment.MachineName, "UnsyncedEditStation", token);

        var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "UnsyncedSet_" + Guid.NewGuid().ToString("N")[..8],
            LocalRootPath = _sourceDir
        }, token);
        var backupSetId = bs.BackupSetId;

        // 1. Create file -> v1
        string filePath = Path.Combine(_sourceDir, "doc.txt");
        await File.WriteAllTextAsync(filePath, "initial v1 content", token);

        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = bs.Name,
            LocalRootPath = _sourceDir,
            AutomaticSyncEnabled = true
        };

        await syncEngine.StartAsync(profile, token);

        Guid fileId;
        string v1Sha;
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var f = await db.Files.Include(x => x.Versions).FirstAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "doc.txt", token);
            fileId = f.Id;
            v1Sha = f.Versions.First(v => v.VersionNumber == 1).Sha256;
        }

        // 2. Modify -> v2
        await File.WriteAllTextAsync(filePath, "saved v2 content", token);
        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId, token);
            return f != null && f.CurrentVersionNumber == 2;
        }, TimeSpan.FromSeconds(6));

        // 3. User modifies file to unsynced local content and immediately invokes Restore v1
        string unsyncedContent = "crucial unsynced local content that must be backed up first";
        await File.WriteAllTextAsync(filePath, unsyncedContent, token);

        // Call restore v1
        var restoreResult = await syncEngine.RestoreVersionAsync(backupSetId, fileId, 1, token);
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.Equal("SUCCESS", restoreResult.Status);
        Assert.Equal(4, restoreResult.NewCurrentVersionNumber);

        // 4. Verify local file now has v1 content
        string currentDiskContent = await File.ReadAllTextAsync(filePath, token);
        Assert.Equal("initial v1 content", currentDiskContent);

        // 5. Verify database: v3 was saved containing the unsynced edit, and v4 is RESTORE containing v1
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var f = await db.Files.Include(x => x.Versions).FirstAsync(x => x.Id == fileId, token);
            Assert.Equal(4, f.CurrentVersionNumber);
            Assert.Equal(4, f.Versions.Count);

            var v1 = f.Versions.First(v => v.VersionNumber == 1);
            var v2 = f.Versions.First(v => v.VersionNumber == 2);
            var v3 = f.Versions.First(v => v.VersionNumber == 3);
            var v4 = f.Versions.First(v => v.VersionNumber == 4);

            Assert.Equal(v1Sha, v4.Sha256);
            Assert.NotEqual(v1Sha, v3.Sha256);
            Assert.NotEqual(v2.Sha256, v3.Sha256);

            // ChangeLog verification
            var logs = await db.ChangeLogs
                .Where(cl => cl.FileId == fileId)
                .OrderBy(cl => cl.CreatedAtUtc)
                .ToListAsync(token);

            Assert.True(logs.Count >= 4);
            var actions = logs.Select(l => l.Action).ToList();
            Assert.Equal("CREATE", actions[0]);
            Assert.Equal("MODIFY", actions[1]);
            Assert.Equal("MODIFY", actions[2]); // v3 pre-backup
            Assert.Equal("RESTORE", actions[3]); // v4 restore
        }

        await syncEngine.StopAsync(token);
    }

    [Fact]
    public async Task Section38_DeleteAndRestoreE2E_ReactivatesFileAndIncrementsVersion()
    {
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = testTimeoutCts.Token;

        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var indexService = new LocalFileIndexService(Path.Combine(_clientDataDir, "index"));
        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var watcher = new FileSystemWatcherService(NullLogger<FileSystemWatcherService>.Instance);
        var stability = new FileStabilityHelper(NullLogger<FileStabilityHelper>.Instance);
        var suppressionService = new RestoreSuppressionService();

        var syncEngine = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcher,
            stability,
            NullLogger<SyncEngine>.Instance,
            suppressionService: suppressionService,
            debounceDelayMs: 200);

        var reg = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "DeleteRestoreStation",
            MachineName = Environment.MachineName
        }, token);
        await identityService.SaveStateAsync(reg.ClientId, Environment.MachineName, "DeleteRestoreStation", token);

        var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "DeleteRestoreSet_" + Guid.NewGuid().ToString("N")[..8],
            LocalRootPath = _sourceDir
        }, token);
        var backupSetId = bs.BackupSetId;

        // 1. Create file -> v1
        string filePath = Path.Combine(_sourceDir, "sub", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "report v1", token);

        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = bs.Name,
            LocalRootPath = _sourceDir,
            AutomaticSyncEnabled = true
        };

        await syncEngine.StartAsync(profile, token);

        Guid fileId;
        string v1Sha;
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var f = await db.Files.Include(x => x.Versions).FirstAsync(x => x.BackupSetId == backupSetId && x.RelativePath == "sub/report.txt", token);
            fileId = f.Id;
            v1Sha = f.Versions.First(v => v.VersionNumber == 1).Sha256;
        }

        // 2. Modify -> v2
        await File.WriteAllTextAsync(filePath, "report v2", token);
        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId, token);
            return f != null && f.CurrentVersionNumber == 2;
        }, TimeSpan.FromSeconds(6));

        // 3. Delete file and delete parent directory locally
        File.Delete(filePath);
        Directory.Delete(Path.GetDirectoryName(filePath)!);

        // Wait for delete to sync
        await WaitForDbConditionAsync(async db =>
        {
            var f = await db.Files.FirstOrDefaultAsync(x => x.Id == fileId, token);
            return f != null && f.IsDeleted;
        }, TimeSpan.FromSeconds(6));

        // 4. Restore v1 of the deleted file
        var restoreResult = await syncEngine.RestoreVersionAsync(backupSetId, fileId, 1, token);
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.True(restoreResult.Status == "SUCCESS", $"Status was {restoreResult.Status}: {restoreResult.ErrorMessage}");
        Assert.Equal(3, restoreResult.NewCurrentVersionNumber);

        // 5. Verify local file and directory recreated
        Assert.True(File.Exists(filePath), "Restored file must exist on disk");
        string localContent = await File.ReadAllTextAsync(filePath, token);
        Assert.Equal("report v1", localContent);

        // 6. Verify database state
        await using (var db = await _dbFactory!.CreateDbContextAsync(token))
        {
            var f = await db.Files.Include(x => x.Versions).FirstAsync(x => x.Id == fileId, token);
            Assert.False(f.IsDeleted, "File must be reactivated");
            Assert.Equal(3, f.CurrentVersionNumber);
            Assert.Equal(3, f.Versions.Count);

            var v3 = f.Versions.First(v => v.VersionNumber == 3);
            Assert.Equal(v1Sha, v3.Sha256);

            var logs = await db.ChangeLogs
                .Where(cl => cl.FileId == fileId)
                .OrderBy(cl => cl.CreatedAtUtc)
                .ToListAsync(token);

            Assert.True(logs.Count >= 4);
            var actions = logs.Select(l => l.Action).ToList();
            Assert.Equal("CREATE", actions[0]);
            Assert.Equal("MODIFY", actions[1]);
            Assert.Equal("DELETE", actions[2]);
            Assert.Equal("RESTORE", actions[3]);
        }

        await syncEngine.StopAsync(token);
    }

    [Fact]
    public async Task Section39_FileBrowserQueryE2E_ClientGatewayMetadataFlow()
    {
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = testTimeoutCts.Token;

        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);

        // 1. Register Client and create BackupSet
        var reg = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "QueryBrowserStation",
            MachineName = Environment.MachineName
        }, token);
        Assert.True(reg.Success);

        var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "QuerySet_" + Guid.NewGuid().ToString("N")[..8],
            LocalRootPath = _sourceDir
        }, token);
        Assert.True(bs.Success);
        var backupSetId = bs.BackupSetId;

        // 2. Create 2 files and back them up
        string path1 = Path.Combine(_sourceDir, "docs", "alpha.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);
        await File.WriteAllTextAsync(path1, "Alpha v1 content", token);

        string path2 = Path.Combine(_sourceDir, "beta.txt");
        await File.WriteAllTextAsync(path2, "Beta v1 content", token);

        var resp1 = await client.BackupFileAsync(backupSetId, "docs/alpha.txt", path1, reg.ClientId, token);
        Assert.True(resp1.Success);

        var resp2 = await client.BackupFileAsync(backupSetId, "beta.txt", path2, reg.ClientId, token);
        Assert.True(resp2.Success);

        // Update alpha to v2
        await File.WriteAllTextAsync(path1, "Alpha v2 content modified", token);
        var resp1v2 = await client.BackupFileAsync(backupSetId, "docs/alpha.txt", path1, reg.ClientId, token);
        Assert.True(resp1v2.Success);

        // 3. Query GetFilesAsync via Gateway
        var filesResp = await client.GetFilesAsync(new GetFilesRequest
        {
            BackupSetId = backupSetId,
            IncludeDeleted = true
        }, token);

        Assert.True(filesResp.Success);
        Assert.NotNull(filesResp.Files);
        Assert.Equal(2, filesResp.Files.Count);

        var alphaFile = filesResp.Files.FirstOrDefault(f => f.RelativePath == "docs/alpha.txt");
        Assert.NotNull(alphaFile);
        Assert.Equal(2, alphaFile.CurrentVersionNumber);
        Assert.False(alphaFile.IsDeleted);

        var betaFile = filesResp.Files.FirstOrDefault(f => f.RelativePath == "beta.txt");
        Assert.NotNull(betaFile);
        Assert.Equal(1, betaFile.CurrentVersionNumber);
        Assert.False(betaFile.IsDeleted);

        // 4. Query GetVersionsAsync for alphaFile
        var versionsResp = await client.GetVersionsAsync(new GetVersionsRequest
        {
            FileId = alphaFile.FileId
        }, token);

        Assert.True(versionsResp.Success);
        Assert.NotNull(versionsResp.Versions);
        Assert.Equal(2, versionsResp.Versions.Count);
        // Ordered descending
        Assert.Equal(2, versionsResp.Versions[0].VersionNumber);
        Assert.Equal(1, versionsResp.Versions[1].VersionNumber);

        // 5. Query GetChangeLogAsync for backup set
        var changeLogResp = await client.GetChangeLogAsync(new GetChangeLogRequest
        {
            BackupSetId = backupSetId,
            Limit = 50
        }, token);

        Assert.True(changeLogResp.Success);
        Assert.NotNull(changeLogResp.Entries);
        Assert.True(changeLogResp.Entries.Count >= 3);
        Assert.Contains(changeLogResp.Entries, e => e.FileId == alphaFile.FileId && e.Action == "CREATE");
        Assert.Contains(changeLogResp.Entries, e => e.FileId == alphaFile.FileId && e.Action == "MODIFY");
        Assert.Contains(changeLogResp.Entries, e => e.FileId == betaFile.FileId && e.Action == "CREATE");
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
