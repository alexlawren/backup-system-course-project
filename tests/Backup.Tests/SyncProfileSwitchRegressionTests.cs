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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backup.Tests;

public class SyncProfileSwitchRegressionTests : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly string _testBaseDir;
    private readonly string _storageRoot;
    private readonly string _clientDataDir;
    private readonly string _oldRootDir;
    private readonly string _newRootDir;

    private readonly CancellationTokenSource _cts = new();

    private MetadataServerWorker? _metadataWorker;
    private StorageServerWorker? _storageWorker;
    private GatewayServerWorker? _gatewayWorker;

    private int _metadataPort;
    private int _storagePort;
    private int _gatewayPort;

    private DbContextOptions<MetadataDbContext>? _dbOptions;
    private TestDbContextFactory? _dbFactory;

    public SyncProfileSwitchRegressionTests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _testBaseDir = Path.Combine(Path.GetTempPath(), "profile_switch_tests_" + uniqueId);
        _storageRoot = Path.Combine(_testBaseDir, "storage");
        _clientDataDir = Path.Combine(_testBaseDir, "client_data");
        _oldRootDir = Path.Combine(_testBaseDir, "old_root");
        _newRootDir = Path.Combine(_testBaseDir, "new_root");

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_clientDataDir);
        Directory.CreateDirectory(_oldRootDir);
        Directory.CreateDirectory(_newRootDir);
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

        try { Directory.Delete(_testBaseDir, recursive: true); } catch { }
        _cts.Dispose();
    }

    private async Task<(BackupGatewayClient Client, SyncEngine Engine, IFileSystemWatcherService Watcher, ISyncProfileService ProfileService, Guid ClientId)> CreateClientAsync()
    {
        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var indexService = new LocalFileIndexService(Path.Combine(_clientDataDir, "index"));
        var profileService = new SyncProfileService(_clientDataDir);
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
            debounceDelayMs: 200,
            profileService: profileService);

        var reg = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "SwitchTestClient",
            MachineName = Environment.MachineName
        });
        Assert.True(reg.Success);
        await identityService.SaveStateAsync(reg.ClientId, Environment.MachineName, "SwitchTestClient");
        client.SetActiveClientId(reg.ClientId);

        return (client, syncEngine, watcher, profileService, reg.ClientId);
    }

    private async Task<Guid> CreateBackupSetAsync(BackupGatewayClient client, Guid clientId, string name, string rootPath)
    {
        var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = clientId,
            Name = name,
            LocalRootPath = rootPath
        });
        Assert.True(bs.Success, bs.ErrorMessage);
        return bs.BackupSetId;
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

    [Fact]
    public async Task SwitchProfile_UpdatesActiveRootPath()
    {
        var (client, engine, _, _, clientId) = await CreateClientAsync();
        try
        {
            var oldSetId = await CreateBackupSetAsync(client, clientId, "OldSet", _oldRootDir);
            var oldProfile = new SyncProfile { BackupSetId = oldSetId, Name = "OldSet", LocalRootPath = _oldRootDir, AutomaticSyncEnabled = true };
            await engine.StartAsync(oldProfile);

            Assert.Equal(Path.GetFullPath(_oldRootDir), engine.ActiveProfile?.LocalRootPath);
            Assert.Equal(Path.GetFullPath(_oldRootDir), engine.CurrentLocalRootPath);

            var newSetId = await CreateBackupSetAsync(client, clientId, "NewSet", _newRootDir);
            var newProfile = new SyncProfile { BackupSetId = newSetId, Name = "NewSet", LocalRootPath = _newRootDir, AutomaticSyncEnabled = true };
            await engine.SwitchProfileAsync(newProfile);

            Assert.NotNull(engine.ActiveProfile);
            Assert.Equal(Path.GetFullPath(_newRootDir), engine.ActiveProfile.LocalRootPath);
            Assert.Equal(Path.GetFullPath(_newRootDir), engine.CurrentLocalRootPath);
            Assert.Equal(newSetId, engine.CurrentBackupSetId);
            Assert.Equal("NewSet", engine.CurrentBackupSetName);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SwitchProfile_RestartsFileWatcherOnNewDirectory()
    {
        var (client, engine, watcher, _, clientId) = await CreateClientAsync();
        try
        {
            var oldSetId = await CreateBackupSetAsync(client, clientId, "OldSet", _oldRootDir);
            var oldProfile = new SyncProfile { BackupSetId = oldSetId, Name = "OldSet", LocalRootPath = _oldRootDir, AutomaticSyncEnabled = true };
            await engine.StartAsync(oldProfile);

            Assert.Equal(Path.GetFullPath(_oldRootDir), watcher.CurrentWatchedDirectory);

            var newSetId = await CreateBackupSetAsync(client, clientId, "NewSet", _newRootDir);
            var newProfile = new SyncProfile { BackupSetId = newSetId, Name = "NewSet", LocalRootPath = _newRootDir, AutomaticSyncEnabled = true };
            await engine.SwitchProfileAsync(newProfile);

            Assert.True(watcher.IsRunning);
            Assert.Equal(Path.GetFullPath(_newRootDir), watcher.CurrentWatchedDirectory);
            Assert.NotEqual(Path.GetFullPath(_oldRootDir), watcher.CurrentWatchedDirectory);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task FileCreatedAfterProfileSwitch_IsBackedUp()
    {
        var (client, engine, _, _, clientId) = await CreateClientAsync();
        try
        {
            var oldSetId = await CreateBackupSetAsync(client, clientId, "OldSet", _oldRootDir);
            var oldProfile = new SyncProfile { BackupSetId = oldSetId, Name = "OldSet", LocalRootPath = _oldRootDir, AutomaticSyncEnabled = true };
            await engine.StartAsync(oldProfile);

            var newSetId = await CreateBackupSetAsync(client, clientId, "NewSet", _newRootDir);
            var newProfile = new SyncProfile { BackupSetId = newSetId, Name = "NewSet", LocalRootPath = _newRootDir, AutomaticSyncEnabled = true };
            await engine.SwitchProfileAsync(newProfile);

            // Create test.txt in newRoot
            string testFilePath = Path.Combine(_newRootDir, "test.txt");
            await File.WriteAllTextAsync(testFilePath, "Content in New Root after profile switch");

            // Wait for debounce and upload
            await WaitForDbConditionAsync(async db =>
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == newSetId && x.RelativePath == "test.txt");
                return f != null && f.CurrentVersionNumber == 1;
            }, TimeSpan.FromSeconds(6));

            // Verify ChangeLog contains CREATE for new set
            var changeLog = await client.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = newSetId, Limit = 20 });
            Assert.True(changeLog.Success);
            Assert.Contains(changeLog.Entries, e => e.Action == "CREATE" && e.RelativePath == "test.txt");
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task OldDirectoryEvents_AfterSwitch_AreIgnored()
    {
        var (client, engine, _, _, clientId) = await CreateClientAsync();
        try
        {
            var oldSetId = await CreateBackupSetAsync(client, clientId, "OldSet", _oldRootDir);
            var oldProfile = new SyncProfile { BackupSetId = oldSetId, Name = "OldSet", LocalRootPath = _oldRootDir, AutomaticSyncEnabled = true };
            await engine.StartAsync(oldProfile);

            var newSetId = await CreateBackupSetAsync(client, clientId, "NewSet", _newRootDir);
            var newProfile = new SyncProfile { BackupSetId = newSetId, Name = "NewSet", LocalRootPath = _newRootDir, AutomaticSyncEnabled = true };
            await engine.SwitchProfileAsync(newProfile);

            // Create file in old directory
            string oldFilePath = Path.Combine(_oldRootDir, "old.txt");
            await File.WriteAllTextAsync(oldFilePath, "This should be completely ignored by new set");

            // Wait debounce window
            await Task.Delay(1000);

            // Verify old.txt was NOT created in newSet
            await using (var db = await _dbFactory!.CreateDbContextAsync())
            {
                var fileInNew = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == newSetId && x.RelativePath == "old.txt");
                Assert.Null(fileInNew);
            }

            var changeLog = await client.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = newSetId, Limit = 20 });
            Assert.True(changeLog.Success);
            Assert.DoesNotContain(changeLog.Entries, e => e.RelativePath == "old.txt");
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SwitchProfile_ForcesReconciliationOfNewRoot()
    {
        var (client, engine, _, _, clientId) = await CreateClientAsync();
        try
        {
            var oldSetId = await CreateBackupSetAsync(client, clientId, "OldSet", _oldRootDir);
            var oldProfile = new SyncProfile { BackupSetId = oldSetId, Name = "OldSet", LocalRootPath = _oldRootDir, AutomaticSyncEnabled = true };
            await engine.StartAsync(oldProfile);

            // Pre-create file in newRoot BEFORE switching profile
            string existingPreSwitchPath = Path.Combine(_newRootDir, "existing_pre_switch.txt");
            await File.WriteAllTextAsync(existingPreSwitchPath, "File created before profile switch was executed");

            var newSetId = await CreateBackupSetAsync(client, clientId, "NewSet", _newRootDir);
            var newProfile = new SyncProfile { BackupSetId = newSetId, Name = "NewSet", LocalRootPath = _newRootDir, AutomaticSyncEnabled = true };

            // Switch to new profile (must trigger initial reconciliation of new root)
            await engine.SwitchProfileAsync(newProfile);

            // File must be discovered and saved
            await WaitForDbConditionAsync(async db =>
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == newSetId && x.RelativePath == "existing_pre_switch.txt");
                return f != null && f.CurrentVersionNumber == 1;
            }, TimeSpan.FromSeconds(6));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SwitchProfile_DoesNotLeakOldBackupSetIndex()
    {
        var (client, engine, _, _, clientId) = await CreateClientAsync();
        try
        {
            var indexService = new LocalFileIndexService(Path.Combine(_clientDataDir, "index"));

            var oldSetId = await CreateBackupSetAsync(client, clientId, "OldSet", _oldRootDir);
            var oldProfile = new SyncProfile { BackupSetId = oldSetId, Name = "OldSet", LocalRootPath = _oldRootDir, AutomaticSyncEnabled = true };
            await engine.StartAsync(oldProfile);

            // Create file in old root
            string oldFilePath = Path.Combine(_oldRootDir, "old_only.txt");
            await File.WriteAllTextAsync(oldFilePath, "Old only content");

            await WaitForDbConditionAsync(async db =>
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == oldSetId && x.RelativePath == "old_only.txt");
                return f != null;
            }, TimeSpan.FromSeconds(6));

            LocalFileIndex? oldIndex = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000)
            {
                oldIndex = await indexService.LoadIndexAsync(oldSetId);
                if (oldIndex.Entries.ContainsKey("old_only.txt")) break;
                await Task.Delay(50);
            }
            Assert.NotNull(oldIndex);
            Assert.True(oldIndex.Entries.ContainsKey("old_only.txt"));

            // Switch to new profile
            var newSetId = await CreateBackupSetAsync(client, clientId, "NewSet", _newRootDir);
            var newProfile = new SyncProfile { BackupSetId = newSetId, Name = "NewSet", LocalRootPath = _newRootDir, AutomaticSyncEnabled = true };
            await engine.SwitchProfileAsync(newProfile);

            var newIndex = await indexService.LoadIndexAsync(newSetId);
            Assert.False(newIndex.Entries.ContainsKey("old_only.txt"), "New BackupSet index leaked entry from old BackupSet!");
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task RapidProfileSwitch_DoesNotLeaveMultipleWatchers()
    {
        var (client, engine, watcher, _, clientId) = await CreateClientAsync();
        try
        {
            string dirA = Path.Combine(_testBaseDir, "dirA");
            string dirB = Path.Combine(_testBaseDir, "dirB");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            var setOld = await CreateBackupSetAsync(client, clientId, "SetOld", _oldRootDir);
            var setA = await CreateBackupSetAsync(client, clientId, "SetA", dirA);
            var setB = await CreateBackupSetAsync(client, clientId, "SetB", dirB);

            var profileOld = new SyncProfile { BackupSetId = setOld, Name = "SetOld", LocalRootPath = _oldRootDir, AutomaticSyncEnabled = true };
            var profileA = new SyncProfile { BackupSetId = setA, Name = "SetA", LocalRootPath = dirA, AutomaticSyncEnabled = true };
            var profileB = new SyncProfile { BackupSetId = setB, Name = "SetB", LocalRootPath = dirB, AutomaticSyncEnabled = true };

            await engine.StartAsync(profileOld);

            // Rapid consecutive switches
            await engine.SwitchProfileAsync(profileA);
            await engine.SwitchProfileAsync(profileB);

            Assert.Equal(Path.GetFullPath(dirB), watcher.CurrentWatchedDirectory);
            Assert.Equal(Path.GetFullPath(dirB), engine.CurrentLocalRootPath);

            // Create file in dirB -> should sync to setB
            string fileB = Path.Combine(dirB, "fileB.txt");
            await File.WriteAllTextAsync(fileB, "Content B");

            await WaitForDbConditionAsync(async db =>
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == setB && x.RelativePath == "fileB.txt");
                return f != null;
            }, TimeSpan.FromSeconds(6));

            // Create file in dirA -> should NOT sync to setB
            string fileA = Path.Combine(dirA, "fileA.txt");
            await File.WriteAllTextAsync(fileA, "Content A");

            await Task.Delay(1000);

            await using (var db = await _dbFactory!.CreateDbContextAsync())
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == setB && x.RelativePath == "fileA.txt");
                Assert.Null(f);
            }
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Section11_FullE2EProfileSwitchLifecycleRegression()
    {
        // 1. Создать oldRoot
        string oldRoot = Path.Combine(_testBaseDir, "e2e_old_root");
        Directory.CreateDirectory(oldRoot);

        var (client, engine, watcher, profileService, clientId) = await CreateClientAsync();
        try
        {
            // 2. Активировать OldSet
            var oldSetId = await CreateBackupSetAsync(client, clientId, "OldSet", oldRoot);
            var oldProfile = new SyncProfile
            {
                BackupSetId = oldSetId,
                Name = "OldSet",
                LocalRootPath = oldRoot,
                AutomaticSyncEnabled = true
            };
            await profileService.SaveProfileAsync(oldProfile);

            // 3. Запустить sync
            await engine.StartAsync(oldProfile);
            Assert.True(engine.IsRunning);
            Assert.Equal(Path.GetFullPath(oldRoot), engine.CurrentLocalRootPath);
            Assert.Equal(Path.GetFullPath(oldRoot), watcher.CurrentWatchedDirectory);

            // 4. Создать oldRoot/a.txt
            string aPath = Path.Combine(oldRoot, "a.txt");
            await File.WriteAllTextAsync(aPath, "Content A Initial");

            // 5. Убедиться, что a.txt сохранён в OldSet
            await WaitForDbConditionAsync(async db =>
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == oldSetId && x.RelativePath == "a.txt");
                return f != null && f.CurrentVersionNumber == 1;
            }, TimeSpan.FromSeconds(6));

            // 6. Создать newRoot
            string newRoot = Path.Combine(_testBaseDir, "e2e_new_root");
            Directory.CreateDirectory(newRoot);

            // 7. Save & Activate NewSet
            var newSetId = await CreateBackupSetAsync(client, clientId, "NewSet", newRoot);
            var newProfile = new SyncProfile
            {
                BackupSetId = newSetId,
                Name = "NewSet",
                LocalRootPath = newRoot,
                AutomaticSyncEnabled = engine.IsRunning
            };
            await profileService.SaveProfileAsync(newProfile);
            await engine.SwitchProfileAsync(newProfile);

            // 8. Убедиться: ActiveRoot == newRoot
            Assert.Equal(Path.GetFullPath(newRoot), engine.CurrentLocalRootPath);
            Assert.Equal(Path.GetFullPath(newRoot), watcher.CurrentWatchedDirectory);
            Assert.True(engine.IsRunning);

            // Verify persisted sync profile is atomically updated
            var persistedProfile = await profileService.GetProfileAsync();
            Assert.NotNull(persistedProfile);
            Assert.Equal(newSetId, persistedProfile.BackupSetId);
            Assert.Equal(Path.GetFullPath(newRoot), Path.GetFullPath(persistedProfile.LocalRootPath));
            Assert.Equal("NewSet", persistedProfile.Name);

            // 9. Создать newRoot/b.txt
            string bPath = Path.Combine(newRoot, "b.txt");
            await File.WriteAllTextAsync(bPath, "Content B in NewSet");

            // 10 & 11. Дождаться watcher и убедиться, что b.txt появился в NewSet
            await WaitForDbConditionAsync(async db =>
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == newSetId && x.RelativePath == "b.txt");
                return f != null && f.CurrentVersionNumber == 1;
            }, TimeSpan.FromSeconds(6));

            // 12. ChangeLog NewSet содержит CREATE для b.txt
            var changeLog = await client.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = newSetId, Limit = 50 });
            Assert.True(changeLog.Success);
            Assert.Contains(changeLog.Entries, e => e.Action == "CREATE" && e.RelativePath == "b.txt");

            // 12b. Modify b.txt -> Verify v2 and MODIFY
            await File.WriteAllTextAsync(bPath, "Content B in NewSet Modified V2");
            await WaitForDbConditionAsync(async db =>
            {
                var f = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == newSetId && x.RelativePath == "b.txt");
                return f != null && f.CurrentVersionNumber == 2;
            }, TimeSpan.FromSeconds(6));

            var changeLogV2 = await client.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = newSetId, Limit = 50 });
            Assert.True(changeLogV2.Success);
            Assert.Contains(changeLogV2.Entries, e => e.Action == "MODIFY" && e.RelativePath == "b.txt");

            // 13. Создать oldRoot/c.txt
            string cPath = Path.Combine(oldRoot, "c.txt");
            await File.WriteAllTextAsync(cPath, "Content C in OldRoot - must NOT sync to NewSet");

            await Task.Delay(1000);

            // 14. Убедиться: c.txt НЕ появился в NewSet
            await using (var db = await _dbFactory!.CreateDbContextAsync())
            {
                var cInNew = await db.Files.FirstOrDefaultAsync(x => x.BackupSetId == newSetId && x.RelativePath == "c.txt");
                Assert.Null(cInNew);
            }
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task UI_State_ImmediatelyReflectsNewProfile_AfterSaveAndActivate()
    {
        var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var profileService = scope.ServiceProvider.GetRequiredService<ISyncProfileService>();
        var syncEngine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

        string oldUiDir = Path.Combine(_testBaseDir, "smoke_ui_test");
        string newUiDir = Path.Combine(_testBaseDir, "BackupDemo");
        Directory.CreateDirectory(oldUiDir);
        Directory.CreateDirectory(newUiDir);

        // 1. Initial state
        var oldSetId = Guid.NewGuid();
        var oldProfile = new SyncProfile
        {
            BackupSetId = oldSetId,
            Name = "DemoProjectSet",
            LocalRootPath = oldUiDir,
            AutomaticSyncEnabled = false
        };
        await profileService.SaveProfileAsync(oldProfile);
        await syncEngine.SwitchProfileAsync(oldProfile);

        // Verify initial UI
        var resp1 = await httpClient.GetAsync("/");
        var html1 = await resp1.Content.ReadAsStringAsync();
        Assert.Contains("DemoProjectSet", html1);
        Assert.Contains("smoke_ui_test", html1);

        // 2. Save & Activate DemoBackup
        var newSetId = Guid.NewGuid();
        var newProfile = new SyncProfile
        {
            BackupSetId = newSetId,
            Name = "DemoBackup",
            LocalRootPath = newUiDir,
            AutomaticSyncEnabled = false
        };
        await profileService.SaveProfileAsync(newProfile);
        await syncEngine.SwitchProfileAsync(newProfile);

        // Verify UI immediately shows DemoBackup and new root
        var resp2 = await httpClient.GetAsync("/");
        var html2 = await resp2.Content.ReadAsStringAsync();
        Assert.Contains("DemoBackup", html2);
        Assert.Contains(Path.GetFullPath(newUiDir), html2);
        Assert.DoesNotContain($">{Path.GetFullPath(oldUiDir)}<", html2);
        Assert.Equal(Path.GetFullPath(newUiDir), syncEngine.CurrentLocalRootPath);
        Assert.Equal("DemoBackup", syncEngine.CurrentBackupSetName);
    }
}
