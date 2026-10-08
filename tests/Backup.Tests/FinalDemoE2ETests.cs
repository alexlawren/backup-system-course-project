using System.Diagnostics;
using Backup.Client.Web.Services;
using Backup.MetadataService;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Services;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Storage;
using Backup.StorageService;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Backup.SyncGateway;
using Backup.SyncGateway.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Backup.Tests;

public class FinalDemoE2ETests : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly ITestOutputHelper _output;
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

    public FinalDemoE2ETests(ITestOutputHelper output)
    {
        _output = output;
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_demo_" + uniqueId);
        _clientDataDir = Path.Combine(Path.GetTempPath(), "e2e_client_demo_" + uniqueId);
        _sourceDir = Path.Combine(Path.GetTempPath(), "e2e_source_demo_" + uniqueId);

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_clientDataDir);
        Directory.CreateDirectory(_sourceDir);
    }

    public async Task InitializeAsync()
    {
        // A. Startup (Steps 1 - 5)
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

        var sw = Stopwatch.StartNew();
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
    public async Task Section26_FinalDeterministicE2EDemoScenario_StepsA_Through_H()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = testCts.Token;

        var perfSw = new Stopwatch();

        // B. Registration (Steps 6 - 7)
        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var backupService = new BackupClientService(client, identityService);

        await backupService.EnsureRegisteredAsync(token);
        var clientState = await backupService.GetIdentityAsync(token);
        Assert.NotNull(clientState?.ClientId);
        Guid clientId = clientState.ClientId.Value;

        var setResp = await backupService.CreateBackupSetAsync("DemoFinalSet", _sourceDir, token);
        Assert.True(setResp.Success);
        Guid backupSetId = setResp.BackupSetId;

        // C. Initial state before watcher start (Steps 8 - 9)
        string fileAPath = Path.Combine(_sourceDir, "a.txt");
        File.WriteAllText(fileAPath, "Initial a content (v1)");

        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = "DemoFinalSet",
            LocalRootPath = _sourceDir,
            AutomaticSyncEnabled = true
        };

        var indexService = new LocalFileIndexService(_clientDataDir);
        var scanner = new DirectoryScanner();
        var watcherService = new FileSystemWatcherService();
        var stabilityHelper = new FileStabilityHelper();
        var suppressionService = new RestoreSuppressionService();

        var syncEngine = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcherService,
            stabilityHelper,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 250,
            suppressionService: suppressionService);

        perfSw.Restart();
        await syncEngine.StartAsync(profile, token);
        perfSw.Stop();
        long initialReconMs = perfSw.ElapsedMilliseconds;
        _output.WriteLine($"[PERF] Initial reconciliation duration: {initialReconMs} ms");

        // Verify a.txt is v1
        var filesResp = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
        Assert.Single(filesResp.Files);
        Assert.Equal("a.txt", filesResp.Files[0].RelativePath);
        Assert.Equal(1, filesResp.Files[0].CurrentVersionNumber);
        Guid fileAId = filesResp.Files[0].FileId;

        // D. Live watcher changes (Steps 10 - 13)
        // 10. create b.txt -> b v1
        string fileBPath = Path.Combine(_sourceDir, "b.txt");
        File.WriteAllText(fileBPath, "File b initial content (v1)");
        await WaitForConditionAsync(async () =>
        {
            var r = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
            return r.Files.Any(f => f.RelativePath == "b.txt" && !f.IsDeleted);
        }, TimeSpan.FromSeconds(5), token);

        // 11. modify a -> a v2
        await Task.Delay(150, token);
        File.WriteAllText(fileAPath, "Modified a content (v2)");
        await WaitForConditionAsync(async () =>
        {
            var r = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
            var a = r.Files.FirstOrDefault(f => f.FileId == fileAId);
            return a != null && a.CurrentVersionNumber == 2;
        }, TimeSpan.FromSeconds(5), token);

        // 12. rename b -> renamed.txt
        string renamedBPath = Path.Combine(_sourceDir, "renamed.txt");
        await Task.Delay(150, token);
        File.Move(fileBPath, renamedBPath);
        await WaitForConditionAsync(async () =>
        {
            var r = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
            return r.Files.Any(f => f.RelativePath == "renamed.txt" && !f.IsDeleted) &&
                   !r.Files.Any(f => f.RelativePath == "b.txt" && !f.IsDeleted);
        }, TimeSpan.FromSeconds(5), token);

        // 13. delete a -> IsDeleted
        await Task.Delay(150, token);
        File.Delete(fileAPath);
        await WaitForConditionAsync(async () =>
        {
            var r = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId, IncludeDeleted = true }, token);
            var a = r.Files.FirstOrDefault(f => f.FileId == fileAId);
            return a != null && a.IsDeleted;
        }, TimeSpan.FromSeconds(5), token);

        // E. Recreate a -> a v3 active (Step 14)
        await Task.Delay(150, token);
        File.WriteAllText(fileAPath, "Recreated a content (v3)");
        await WaitForConditionAsync(async () =>
        {
            var r = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
            var a = r.Files.FirstOrDefault(f => f.FileId == fileAId);
            return a != null && !a.IsDeleted && a.CurrentVersionNumber == 3;
        }, TimeSpan.FromSeconds(5), token);

        // F. Restore flow (Steps 15 - 18)
        // 15. modify a -> v4
        await Task.Delay(150, token);
        perfSw.Restart();
        File.WriteAllText(fileAPath, "Further modified a before restore (v4)");
        await WaitForConditionAsync(async () =>
        {
            var r = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
            var a = r.Files.FirstOrDefault(f => f.FileId == fileAId);
            return a != null && a.CurrentVersionNumber == 4;
        }, TimeSpan.FromSeconds(5), token);
        perfSw.Stop();
        _output.WriteLine($"[PERF] Single small file backup duration: {perfSw.ElapsedMilliseconds} ms");

        // 16. Restore a version 1
        perfSw.Restart();
        var restoreResult = await syncEngine.RestoreVersionAsync(backupSetId, fileAId, 1, token);
        perfSw.Stop();
        _output.WriteLine($"[PERF] Restore small file duration: {perfSw.ElapsedMilliseconds} ms");
        Assert.True(restoreResult.Success, $"Restore failed: {restoreResult.ErrorMessage}");

        // 17. restored version becomes v5 RESTORE
        Assert.Equal(5, restoreResult.NewCurrentVersionNumber);
        Assert.Equal("Initial a content (v1)", File.ReadAllText(fileAPath));

        // 18. watcher duplicate absent: wait to make sure suppression prevented duplicate v6
        await Task.Delay(600, token);
        var postRestoreFiles = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
        var postA = postRestoreFiles.Files.First(f => f.FileId == fileAId);
        Assert.Equal(5, postA.CurrentVersionNumber); // stays 5!

        // G. Restart recovery (Steps 19 - 22)
        // 19. stop sync
        await syncEngine.StopAsync(token);
        await syncEngine.DisposeAsync();

        // 20. offline modify renamed.txt
        await Task.Delay(150, token);
        File.WriteAllText(renamedBPath, "renamed.txt modified offline during restart!");

        // 21. restart
        var syncEngineRestarted = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            new FileSystemWatcherService(),
            stabilityHelper,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 250,
            suppressionService: new RestoreSuppressionService());

        // 22. reconciliation creates next version
        await syncEngineRestarted.StartAsync(profile, token);
        await WaitForConditionAsync(async () =>
        {
            var r = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
            var renamed = r.Files.FirstOrDefault(f => f.RelativePath == "renamed.txt");
            return renamed != null && renamed.CurrentVersionNumber > 1;
        }, TimeSpan.FromSeconds(5), token);

        await syncEngineRestarted.StopAsync(token);
        await syncEngineRestarted.DisposeAsync();

        // H. Final Verifications (Steps 23 - 29)
        // 23. GetFiles
        var finalFiles = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, token);
        Assert.True(finalFiles.Success);
        Assert.Equal(2, finalFiles.Files.Count(f => !f.IsDeleted));

        // 24. GetVersions for file A
        var versionsA = await client.GetVersionsAsync(new GetVersionsRequest
        {
            FileId = fileAId
        }, token);
        Assert.True(versionsA.Success);
        Assert.Equal(5, versionsA.Versions.Count);

        // 25. GetChangeLog
        var changeLog = await client.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = backupSetId }, token);
        Assert.True(changeLog.Success);
        Assert.Contains(changeLog.Entries, e => e.Action == "CREATE");
        Assert.Contains(changeLog.Entries, e => e.Action == "MODIFY");
        Assert.Contains(changeLog.Entries, e => e.Action == "RENAME");
        Assert.Contains(changeLog.Entries, e => e.Action == "DELETE");
        Assert.Contains(changeLog.Entries, e => e.Action == "RESTORE");

        // 26 & 27. DB consistency and physical blobs
        await using var db = await _dbFactory!.CreateDbContextAsync(token);
        var allDbVersions = await db.FileVersions
            .Where(v => v.File.BackupSetId == backupSetId)
            .Select(v => new { v.StorageKey, v.VersionNumber, v.File.RelativePath })
            .ToListAsync(token);

        var report = StorageConsistencyVerifier.Verify(
            _storageRoot,
            allDbVersions.Select(v => (v.StorageKey, v.VersionNumber, (string?)v.RelativePath)));

        Assert.True(report.IsConsistent, $"DB/Storage inconsistent: {report.MissingBlobsCount} blobs missing");
        Assert.True(report.ValidBlobsCount > 0);
        Assert.Equal(0, report.MissingBlobsCount);

        // 28. No .tmp/*.part leftover files in storage
        string tmpDir = Path.Combine(_storageRoot, ".tmp");
        if (Directory.Exists(tmpDir))
        {
            var partFiles = Directory.GetFiles(tmpDir, "*.part", SearchOption.AllDirectories);
            Assert.Empty(partFiles);
        }

        // 29. No unexpected restore temp files (.restore.*.tmp) in source directory
        var leftoverRestoreTmps = Directory.GetFiles(_sourceDir, ".restore.*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftoverRestoreTmps);
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> predicate, TimeSpan timeout, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !token.IsCancellationRequested)
        {
            if (await predicate()) return;
            await Task.Delay(50, token);
        }
        Assert.True(await predicate(), "Condition not satisfied within timeout: " + timeout);
    }
}
