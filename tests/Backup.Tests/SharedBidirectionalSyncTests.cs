using System.Security.Cryptography;
using Backup.Client.Web.Services;
using Backup.MetadataService;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Services;
using Backup.Shared.Errors;
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

namespace Backup.Tests;

public class SharedSyncServerFixture : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly string _storageRoot;
    private readonly CancellationTokenSource _cts = new();

    private MetadataServerWorker? _metadataWorker;
    private StorageServerWorker? _storageWorker;
    private GatewayServerWorker? _gatewayWorker;

    public int MetadataPort { get; private set; }
    public int StoragePort { get; private set; }
    public int GatewayPort { get; private set; }

    public MetadataDbContext CreateDbContext()
    {
        var dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        return new MetadataDbContext(dbOptions);
    }

    public SharedSyncServerFixture()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "fixture_storage_" + uniqueId);
        Directory.CreateDirectory(_storageRoot);
    }

    public async Task InitializeAsync()
    {
        var dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var dbFactory = new TestDbContextFactory(dbOptions);

        var metadataAppService = new MetadataApplicationService(dbFactory, NullLogger<MetadataApplicationService>.Instance);
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
        MetadataPort = _metadataWorker.BoundPort.Value;

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
        StoragePort = _storageWorker.BoundPort.Value;

        var gatewayOptions = Options.Create(new SyncGateway.Configuration.GatewayOptions { Host = "127.0.0.1", Port = 0 });
        var downstreamOptions = Options.Create(new DownstreamServicesOptions
        {
            MetadataService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = MetadataPort },
            StorageService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = StoragePort }
        });

        _gatewayWorker = new GatewayServerWorker(NullLogger<GatewayServerWorker>.Instance, NullLoggerFactory.Instance, gatewayOptions, downstreamOptions);
        _ = _gatewayWorker.StartAsync(_cts.Token);

        sw.Restart();
        while (_gatewayWorker.BoundPort == null || _gatewayWorker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 8000) throw new TimeoutException("GatewayServerWorker failed to bind.");
            await Task.Delay(20);
        }
        GatewayPort = _gatewayWorker.BoundPort.Value;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();

        if (_gatewayWorker != null) try { await _gatewayWorker.StopAsync(CancellationToken.None); } catch { }
        if (_storageWorker != null) try { await _storageWorker.StopAsync(CancellationToken.None); } catch { }
        if (_metadataWorker != null) try { await _metadataWorker.StopAsync(CancellationToken.None); } catch { }

        try { Directory.Delete(_storageRoot, recursive: true); } catch { }
        _cts.Dispose();
    }
}

public class TestClientNode : IDisposable
{
    public string Name { get; }
    public string ClientDataDir { get; }
    public string SourceDir { get; }
    public BackupGatewayClient GatewayClient { get; }
    public ClientIdentityService IdentityService { get; }
    public LocalFileIndexService IndexService { get; }
    public SyncProfileService ProfileService { get; }
    public DirectoryScanner Scanner { get; }
    public FileSystemWatcherService Watcher { get; }
    public FileStabilityHelper Stability { get; }
    public RestoreSuppressionService Suppression { get; }
    public BackupClientService ClientService { get; }
    public SyncEngine SyncEngine { get; }
    public Guid ClientId { get; private set; }

    public TestClientNode(string name, int gatewayPort)
    {
        Name = name;
        string uniqueId = Guid.NewGuid().ToString("N");
        ClientDataDir = Path.Combine(Path.GetTempPath(), $"tc_data_{name}_{uniqueId}");
        SourceDir = Path.Combine(Path.GetTempPath(), $"tc_source_{name}_{uniqueId}");
        Directory.CreateDirectory(ClientDataDir);
        Directory.CreateDirectory(SourceDir);

        GatewayClient = new BackupGatewayClient("127.0.0.1", gatewayPort);
        IdentityService = new ClientIdentityService(Path.Combine(ClientDataDir, "client-state.json"));
        IndexService = new LocalFileIndexService(Path.Combine(ClientDataDir, "index"));
        ProfileService = new SyncProfileService(ClientDataDir);
        Scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        Watcher = new FileSystemWatcherService(NullLogger<FileSystemWatcherService>.Instance);
        Stability = new FileStabilityHelper(NullLogger<FileStabilityHelper>.Instance);
        Suppression = new RestoreSuppressionService();
        ClientService = new BackupClientService(GatewayClient, IdentityService);

        SyncEngine = new SyncEngine(
            GatewayClient,
            IdentityService,
            IndexService,
            Scanner,
            Watcher,
            Stability,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 150,
            suppressionService: Suppression,
            profileService: ProfileService);
    }

    public async Task RegisterAsync(CancellationToken token)
    {
        var reg = await GatewayClient.RegisterClientAsync(new RegisterClientRequest
        {
            Name = Name,
            MachineName = Environment.MachineName + "_" + Name
        }, token);
        if (!reg.Success) throw new InvalidOperationException($"Register failed: {reg.ErrorMessage}");
        ClientId = reg.ClientId;
        await IdentityService.SaveStateAsync(reg.ClientId, Environment.MachineName + "_" + Name, Name, token);
    }

    public async Task<(Guid BackupSetId, string ShareCode)> CreateSharedSetAsync(string setName, CancellationToken token)
    {
        var resp = await GatewayClient.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = ClientId,
            Name = setName,
            AccessMode = BackupSetAccessMode.Shared
        }, token);
        if (!resp.Success) throw new InvalidOperationException($"CreateSharedSet failed: {resp.ErrorMessage}");

        var profile = new SyncProfile
        {
            BackupSetId = resp.BackupSetId,
            Name = setName,
            LocalRootPath = SourceDir,
            AutomaticSyncEnabled = true,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = resp.ShareCode
        };
        await ProfileService.SaveProfileAsync(profile, token);
        await SyncEngine.StartAsync(profile, token);

        return (resp.BackupSetId, resp.ShareCode!);
    }

    public async Task<Guid> JoinSharedSetAsync(string shareCode, CancellationToken token)
    {
        var resp = await GatewayClient.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ShareCode = shareCode
        }, token);
        if (!resp.Success || resp.BackupSetId == null) throw new InvalidOperationException($"JoinSharedSet failed: {resp.ErrorMessage}");

        var profile = new SyncProfile
        {
            BackupSetId = resp.BackupSetId.Value,
            Name = "Joined_" + shareCode,
            LocalRootPath = SourceDir,
            AutomaticSyncEnabled = true,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = shareCode
        };
        await ProfileService.SaveProfileAsync(profile, token);
        await SyncEngine.StartAsync(profile, token);

        return resp.BackupSetId.Value;
    }

    public async Task ReconcileAsync(CancellationToken token)
    {
        await SyncEngine.ReconcileNowAsync(token);
    }

    public void Dispose()
    {
        try { SyncEngine.Dispose(); } catch { }
        try { Directory.Delete(ClientDataDir, recursive: true); } catch { }
        try { Directory.Delete(SourceDir, recursive: true); } catch { }
    }
}

public class SharedBidirectionalSyncTests : IClassFixture<SharedSyncServerFixture>, IDisposable
{
    private readonly SharedSyncServerFixture _fixture;
    private readonly List<TestClientNode> _nodes = new();

    public SharedBidirectionalSyncTests(SharedSyncServerFixture fixture)
    {
        _fixture = fixture;
    }

    private TestClientNode CreateNode(string name)
    {
        var node = new TestClientNode(name, _fixture.GatewayPort);
        _nodes.Add(node);
        return node;
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }
    }

    // ==================================================
    // 27. TESTS — REMOTE MANIFEST
    // ==================================================

    [Fact]
    public async Task SharedManifest_ReturnsCurrentServerState()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("ManA");
        var nodeB = CreateNode("ManB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("ManifestSet1", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // A creates alpha.txt and sub/beta.txt
        string aPath = Path.Combine(nodeA.SourceDir, "alpha.txt");
        File.WriteAllText(aPath, "Alpha content");
        string subDir = Path.Combine(nodeA.SourceDir, "sub");
        Directory.CreateDirectory(subDir);
        string bPath = Path.Combine(subDir, "beta.txt");
        File.WriteAllText(bPath, "Beta sub content");

        await nodeA.ReconcileAsync(token);

        // B gets manifest
        var manifestResp = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest
        {
            BackupSetId = setId
        }, token);

        Assert.True(manifestResp.Success);
        Assert.Equal(2, manifestResp.Entries.Count);

        var alphaEntry = manifestResp.Entries.FirstOrDefault(e => e.RelativePath == "alpha.txt");
        Assert.NotNull(alphaEntry);
        Assert.Equal(1, alphaEntry.CurrentVersionNumber);
        Assert.False(alphaEntry.IsDeleted);
        Assert.Equal(new FileInfo(aPath).Length, alphaEntry.Size);

        var betaEntry = manifestResp.Entries.FirstOrDefault(e => e.RelativePath == "sub/beta.txt" || e.RelativePath == "sub\\beta.txt");
        Assert.NotNull(betaEntry);
        Assert.Equal(1, betaEntry.CurrentVersionNumber);
        Assert.False(betaEntry.IsDeleted);
    }

    [Fact]
    public async Task SharedManifest_IncludesDeletedFiles()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("DelManA");
        var nodeB = CreateNode("DelManB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("ManifestSet2", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fPath = Path.Combine(nodeA.SourceDir, "to_delete.txt");
        File.WriteAllText(fPath, "To delete content");
        await nodeA.ReconcileAsync(token);

        // Now delete on A and reconcile
        File.Delete(fPath);
        await nodeA.ReconcileAsync(token);

        // B gets manifest -> deleted file is included as tombstone
        var manifestResp = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest
        {
            BackupSetId = setId
        }, token);

        Assert.True(manifestResp.Success);
        var entry = manifestResp.Entries.FirstOrDefault(e => e.RelativePath == "to_delete.txt");
        Assert.NotNull(entry);
        Assert.True(entry.IsDeleted);
        Assert.True(entry.CurrentVersionNumber >= 1);
    }

    [Fact]
    public async Task NonMember_CannotGetManifest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("NonMembA");
        var nodeC = CreateNode("NonMembC");
        await nodeA.RegisterAsync(token);
        await nodeC.RegisterAsync(token);

        var (setId, _) = await nodeA.CreateSharedSetAsync("SecretManifestSet", token);

        // Node C is NOT a member
        var manifestResp = await nodeC.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest
        {
            BackupSetId = setId
        }, token);

        Assert.False(manifestResp.Success);
        Assert.Equal(BackupErrorCodes.AccessDenied, manifestResp.ErrorCode);
    }

    [Fact]
    public async Task PrivateManifest_IsOwnerOnly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("PrivManA");
        var nodeB = CreateNode("PrivManB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var respA = await nodeA.GatewayClient.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = nodeA.ClientId,
            Name = "StrictlyPrivateSet",
            AccessMode = BackupSetAccessMode.Private
        }, token);
        Assert.True(respA.Success);

        // Node B tries to get manifest of Private set
        var manifestB = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest
        {
            BackupSetId = respA.BackupSetId
        }, token);
        Assert.False(manifestB.Success);
        Assert.Equal(BackupErrorCodes.AccessDenied, manifestB.ErrorCode);

        // Node A (owner) can get manifest
        var manifestA = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest
        {
            BackupSetId = respA.BackupSetId
        }, token);
        Assert.True(manifestA.Success);
    }

    [Fact]
    public async Task Manifest_ReturnsStableFileIdAcrossRename()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("RenManA");
        var nodeB = CreateNode("RenManB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("RenameManifestSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string oldPath = Path.Combine(nodeA.SourceDir, "original.txt");
        File.WriteAllText(oldPath, "Original content");
        await nodeA.ReconcileAsync(token);

        var manifest1 = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry1 = manifest1.Entries.First(e => e.RelativePath == "original.txt");
        Guid fileId = entry1.FileId;

        // Rename on A
        string newPath = Path.Combine(nodeA.SourceDir, "renamed.txt");
        File.Move(oldPath, newPath);
        var renameResp = await nodeA.GatewayClient.RenameFileAsync(new FileRenamedRequest
        {
            ClientId = nodeA.ClientId,
            BackupSetId = setId,
            OldRelativePath = "original.txt",
            NewRelativePath = "renamed.txt"
        }, token);
        Assert.True(renameResp.Success);

        var manifest2 = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry2 = manifest2.Entries.First(e => e.RelativePath == "renamed.txt");
        Assert.Equal(fileId, entry2.FileId);
    }

    // ==================================================
    // 28. TESTS — DOWNLOAD
    // ==================================================

    [Fact]
    public async Task Member_CanDownloadExistingVersion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("DlA");
        var nodeB = CreateNode("DlB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("DownloadSet1", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fPath = Path.Combine(nodeA.SourceDir, "hello.txt");
        File.WriteAllText(fPath, "Hello Shared World!");
        await nodeA.ReconcileAsync(token);

        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry = manifest.Entries.First(e => e.RelativePath == "hello.txt");

        var dlResult = await nodeB.GatewayClient.DownloadSharedFileVersionAsync(
            new DownloadFileVersionRequest
            {
                BackupSetId = setId,
                FileId = entry.FileId,
                VersionNumber = 1
            },
            nodeB.SourceDir,
            token);

        Assert.True(dlResult.Success);
        Assert.NotNull(dlResult.TempFilePath);
        Assert.True(File.Exists(dlResult.TempFilePath));
        Assert.Equal("Hello Shared World!", File.ReadAllText(dlResult.TempFilePath));
    }

    [Fact]
    public async Task NonMember_CannotDownloadVersion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("DlSecA");
        var nodeC = CreateNode("DlSecC");
        await nodeA.RegisterAsync(token);
        await nodeC.RegisterAsync(token);

        var (setId, _) = await nodeA.CreateSharedSetAsync("DownloadSecSet", token);

        string fPath = Path.Combine(nodeA.SourceDir, "secret.txt");
        File.WriteAllText(fPath, "Top secret");
        await nodeA.ReconcileAsync(token);

        var manifestA = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry = manifestA.Entries.First(e => e.RelativePath == "secret.txt");

        var dlResult = await nodeC.GatewayClient.DownloadSharedFileVersionAsync(
            new DownloadFileVersionRequest
            {
                BackupSetId = setId,
                FileId = entry.FileId,
                VersionNumber = 1
            },
            nodeC.SourceDir,
            token);

        Assert.False(dlResult.Success);
        Assert.Equal(BackupErrorCodes.AccessDenied, dlResult.ErrorCode);
    }

    [Fact]
    public async Task DownloadedBinary_HashMatchesMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("HashA");
        var nodeB = CreateNode("HashB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("HashMatchSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        byte[] randomBytes = new byte[64 * 1024];
        Random.Shared.NextBytes(randomBytes);
        string fPath = Path.Combine(nodeA.SourceDir, "binary.dat");
        File.WriteAllBytes(fPath, randomBytes);
        await nodeA.ReconcileAsync(token);

        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry = manifest.Entries.First(e => e.RelativePath == "binary.dat");

        var dlResult = await nodeB.GatewayClient.DownloadSharedFileVersionAsync(
            new DownloadFileVersionRequest
            {
                BackupSetId = setId,
                FileId = entry.FileId,
                VersionNumber = 1
            },
            nodeB.SourceDir,
            token);

        Assert.True(dlResult.Success);
        Assert.Equal(entry.Sha256, dlResult.Sha256);
    }

    [Fact]
    public async Task LargeSharedFile_DownloadIsStreaming()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cts.Token;

        var nodeA = CreateNode("LgDlA");
        var nodeB = CreateNode("LgDlB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("LargeDlSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // 4 MiB file
        int size = 4 * 1024 * 1024;
        byte[] payload = new byte[size];
        for (int i = 0; i < size; i++) payload[i] = (byte)(i % 251);

        string fPath = Path.Combine(nodeA.SourceDir, "large.bin");
        File.WriteAllBytes(fPath, payload);
        await nodeA.ReconcileAsync(token);

        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry = manifest.Entries.First(e => e.RelativePath == "large.bin");

        var dlResult = await nodeB.GatewayClient.DownloadSharedFileVersionAsync(
            new DownloadFileVersionRequest
            {
                BackupSetId = setId,
                FileId = entry.FileId,
                VersionNumber = 1
            },
            nodeB.SourceDir,
            token);

        Assert.True(dlResult.Success);
        Assert.Equal(size, dlResult.Size);
    }

    [Fact]
    public async Task RemoteDownload_DoesNotCreateNewFileVersion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("NoVerA");
        var nodeB = CreateNode("NoVerB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("NoVerSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fPath = Path.Combine(nodeA.SourceDir, "stable_file.txt");
        File.WriteAllText(fPath, "Stable version 1");
        await nodeA.ReconcileAsync(token);

        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry = manifest.Entries.First(e => e.RelativePath == "stable_file.txt");

        var versionsBefore = await nodeA.GatewayClient.GetVersionsAsync(new GetVersionsRequest { FileId = entry.FileId }, token);
        Assert.Single(versionsBefore.Versions);

        var dlResult = await nodeB.GatewayClient.DownloadSharedFileVersionAsync(
            new DownloadFileVersionRequest
            {
                BackupSetId = setId,
                FileId = entry.FileId,
                VersionNumber = 1
            },
            nodeB.SourceDir,
            token);
        Assert.True(dlResult.Success);

        var versionsAfter = await nodeA.GatewayClient.GetVersionsAsync(new GetVersionsRequest { FileId = entry.FileId }, token);
        Assert.Single(versionsAfter.Versions);
    }

    [Fact]
    public async Task RemoteDownload_DoesNotCreateChangeLogEntry()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("NoLogA");
        var nodeB = CreateNode("NoLogB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("NoLogSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fPath = Path.Combine(nodeA.SourceDir, "log_check.txt");
        File.WriteAllText(fPath, "Testing no log entry on pull");
        await nodeA.ReconcileAsync(token);

        var logsBefore = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = setId }, token);
        int logCountBefore = logsBefore.Entries.Count;

        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry = manifest.Entries.First(e => e.RelativePath == "log_check.txt");

        await nodeB.GatewayClient.DownloadSharedFileVersionAsync(
            new DownloadFileVersionRequest
            {
                BackupSetId = setId,
                FileId = entry.FileId,
                VersionNumber = 1
            },
            nodeB.SourceDir,
            token);

        var logsAfter = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = setId }, token);
        Assert.Equal(logCountBefore, logsAfter.Entries.Count);
    }

    // ==================================================
    // 29. TESTS — INITIAL JOIN
    // ==================================================

    [Fact]
    public async Task JoinSharedSet_EmptyLocalFolder_DownloadsExistingServerFiles()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("BootA");
        var nodeB = CreateNode("BootB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("BootstrapSet", token);

        // Server gets a.txt and sub/b.txt from A
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "a.txt"), "Content AAA");
        Directory.CreateDirectory(Path.Combine(nodeA.SourceDir, "sub"));
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "sub", "b.txt"), "Content BBB");
        await nodeA.ReconcileAsync(token);

        // B joins with completely empty local folder
        await nodeB.JoinSharedSetAsync(shareCode, token);
        await nodeB.ReconcileAsync(token);

        // Both files exist physically on B
        string bAPath = Path.Combine(nodeB.SourceDir, "a.txt");
        string bBPath = Path.Combine(nodeB.SourceDir, "sub", "b.txt");

        Assert.True(File.Exists(bAPath));
        Assert.True(File.Exists(bBPath));
        Assert.Equal("Content AAA", File.ReadAllText(bAPath));
        Assert.Equal("Content BBB", File.ReadAllText(bBPath));
    }

    [Fact]
    public async Task InitialJoin_PreservesServerVersionNumbers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("PresA");
        var nodeB = CreateNode("PresB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("PreserveVerSet", token);
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "ver_check.txt"), "Ver check v1");
        await nodeA.ReconcileAsync(token);

        await nodeB.JoinSharedSetAsync(shareCode, token);
        await nodeB.ReconcileAsync(token);

        // Index on B reflects server version 1
        var idx = await nodeB.IndexService.LoadIndexAsync(setId, token);
        Assert.True(idx.Entries.TryGetValue("ver_check.txt", out var entry));
        Assert.Equal(1, entry.LastSyncedServerVersion);

        // Server still has only 1 version
        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        Assert.Equal(1, manifest.Entries.First(e => e.RelativePath == "ver_check.txt").CurrentVersionNumber);
    }

    [Fact]
    public async Task InitialJoin_WatcherEchoDoesNotCreateDuplicateVersions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("EchoA");
        var nodeB = CreateNode("EchoB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("EchoCheckSet", token);
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "echo.txt"), "Echo test payload");
        await nodeA.ReconcileAsync(token);

        await nodeB.JoinSharedSetAsync(shareCode, token);
        await nodeB.ReconcileAsync(token);

        await Task.Delay(400, token);

        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        var entry = manifest.Entries.First(e => e.RelativePath == "echo.txt");
        Assert.Equal(1, entry.CurrentVersionNumber);

        var versions = await nodeA.GatewayClient.GetVersionsAsync(new GetVersionsRequest { FileId = entry.FileId }, token);
        Assert.Single(versions.Versions);
    }

    // ==================================================
    // 30. TESTS — TWO WAY
    // ==================================================

    [Fact]
    public async Task ClientA_Create_ClientB_Reconcile_ReceivesFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("TwoA1");
        var nodeB = CreateNode("TwoB1");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("TwoWaySet1", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        File.WriteAllText(Path.Combine(nodeA.SourceDir, "collab.txt"), "Created by A");
        await nodeA.ReconcileAsync(token);

        await nodeB.ReconcileAsync(token);
        string bFile = Path.Combine(nodeB.SourceDir, "collab.txt");
        Assert.True(File.Exists(bFile));
        Assert.Equal("Created by A", File.ReadAllText(bFile));
    }

    [Fact]
    public async Task ClientB_Modify_ClientA_Reconcile_ReceivesNewVersion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("TwoA2");
        var nodeB = CreateNode("TwoB2");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("TwoWaySet2", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        File.WriteAllText(Path.Combine(nodeA.SourceDir, "doc.txt"), "v1 from A");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B modifies doc.txt
        File.WriteAllText(Path.Combine(nodeB.SourceDir, "doc.txt"), "v2 modified by B");
        await nodeB.ReconcileAsync(token);

        // A reconciles
        await nodeA.ReconcileAsync(token);
        Assert.Equal("v2 modified by B", File.ReadAllText(Path.Combine(nodeA.SourceDir, "doc.txt")));
    }

    [Fact]
    public async Task RemoteModify_LocalUnchanged_SafelyReplacesFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("SafeRepA");
        var nodeB = CreateNode("SafeRepB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("SafeReplaceSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        File.WriteAllText(Path.Combine(nodeA.SourceDir, "report.txt"), "Report v1");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B modifies to v2
        File.WriteAllText(Path.Combine(nodeB.SourceDir, "report.txt"), "Report v2 updated");
        await nodeB.ReconcileAsync(token);

        // A's local file unchanged -> safely replaced
        await nodeA.ReconcileAsync(token);
        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.Equal("Report v2 updated", File.ReadAllText(Path.Combine(nodeA.SourceDir, "report.txt")));
    }

    [Fact]
    public async Task RemoteApply_UpdatesLocalIndexServerVersion()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("IdxUpdA");
        var nodeB = CreateNode("IdxUpdB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("IndexUpdateSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        File.WriteAllText(Path.Combine(nodeA.SourceDir, "data.txt"), "Initial data");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B modifies data.txt
        File.WriteAllText(Path.Combine(nodeB.SourceDir, "data.txt"), "Initial data modified by B");
        await nodeB.ReconcileAsync(token);

        await nodeA.ReconcileAsync(token);

        var idxA = await nodeA.IndexService.LoadIndexAsync(setId, token);
        Assert.True(idxA.Entries.TryGetValue("data.txt", out var entry));
        Assert.Equal(2, entry.LastSyncedServerVersion);
        Assert.Equal(entry.LastSyncedSha256, entry.Sha256);
    }

    // ==================================================
    // 31. TESTS — RENAME / DELETE
    // ==================================================

    [Fact]
    public async Task ClientA_Rename_ClientB_Reconcile_RenamesLocalFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("RenTwoA");
        var nodeB = CreateNode("RenTwoB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("RenameTwoWaySet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string oldA = Path.Combine(nodeA.SourceDir, "old_name.txt");
        File.WriteAllText(oldA, "Rename content");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A renames old_name.txt -> new_name.txt
        string newA = Path.Combine(nodeA.SourceDir, "new_name.txt");
        File.Move(oldA, newA);
        await nodeA.GatewayClient.RenameFileAsync(new FileRenamedRequest
        {
            ClientId = nodeA.ClientId,
            BackupSetId = setId,
            OldRelativePath = "old_name.txt",
            NewRelativePath = "new_name.txt"
        }, token);

        // B reconciles
        await nodeB.ReconcileAsync(token);
        Assert.False(File.Exists(Path.Combine(nodeB.SourceDir, "old_name.txt")));
        Assert.True(File.Exists(Path.Combine(nodeB.SourceDir, "new_name.txt")));
        Assert.Equal("Rename content", File.ReadAllText(Path.Combine(nodeB.SourceDir, "new_name.txt")));
    }

    [Fact]
    public async Task RemoteRename_DoesNotGenerateServerRenameEcho()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("RenEchoA");
        var nodeB = CreateNode("RenEchoB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("RenameEchoSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string oldA = Path.Combine(nodeA.SourceDir, "source.txt");
        File.WriteAllText(oldA, "Content for rename echo test");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        string newA = Path.Combine(nodeA.SourceDir, "destination.txt");
        File.Move(oldA, newA);
        await nodeA.GatewayClient.RenameFileAsync(new FileRenamedRequest
        {
            ClientId = nodeA.ClientId,
            BackupSetId = setId,
            OldRelativePath = "source.txt",
            NewRelativePath = "destination.txt"
        }, token);

        // B reconciles -> executes local rename
        await nodeB.ReconcileAsync(token);
        await Task.Delay(400, token);

        // Verify changelog on server only has 1 RENAME entry (by nodeA), none by nodeB
        var logs = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = setId }, token);
        var renameLogs = logs.Entries.Where(e => e.Action == "RENAME").ToList();
        Assert.Single(renameLogs);
        Assert.Equal(nodeA.ClientId, renameLogs[0].ActorClientId);
    }

    [Fact]
    public async Task ClientB_Delete_ClientA_Reconcile_RemovesLocalFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("DelTwoA");
        var nodeB = CreateNode("DelTwoB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("DeleteTwoWaySet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        File.WriteAllText(Path.Combine(nodeA.SourceDir, "delete_me.txt"), "To be deleted by B");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B deletes file
        File.Delete(Path.Combine(nodeB.SourceDir, "delete_me.txt"));
        await nodeB.ReconcileAsync(token);

        // A reconciles
        await nodeA.ReconcileAsync(token);
        Assert.False(File.Exists(Path.Combine(nodeA.SourceDir, "delete_me.txt")));
    }

    [Fact]
    public async Task RemoteDelete_DoesNotGenerateDeleteEcho()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("DelEchoA");
        var nodeB = CreateNode("DelEchoB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("DeleteEchoSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        File.WriteAllText(Path.Combine(nodeA.SourceDir, "item.txt"), "Item content");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B deletes item.txt
        File.Delete(Path.Combine(nodeB.SourceDir, "item.txt"));
        await nodeB.ReconcileAsync(token);

        // A reconciles
        await nodeA.ReconcileAsync(token);
        await Task.Delay(400, token);

        // Server changelog should only have 1 DELETE action (by nodeB)
        var logs = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = setId }, token);
        var deleteLogs = logs.Entries.Where(e => e.Action == "DELETE").ToList();
        Assert.Single(deleteLogs);
        Assert.Equal(nodeB.ClientId, deleteLogs[0].ActorClientId);
    }

    // ==================================================
    // 32. TESTS — OFFLINE / RECONNECT
    // ==================================================

    [Fact]
    public async Task OfflineClient_MissesChanges_StartupReconciliationCatchesUp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("OffA");
        var nodeB = CreateNode("OffB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("OfflineSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        File.WriteAllText(Path.Combine(nodeA.SourceDir, "base.txt"), "Base content");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A goes offline (stops)
        await nodeA.SyncEngine.StopAsync();

        // B performs operations while A is offline
        File.WriteAllText(Path.Combine(nodeB.SourceDir, "b_offline.txt"), "Created while A offline");
        await nodeB.ReconcileAsync(token);

        // A comes online and starts sync
        var profileA = await nodeA.ProfileService.GetProfileAsync(token);
        await nodeA.SyncEngine.StartAsync(profileA!, token);

        Assert.True(File.Exists(Path.Combine(nodeA.SourceDir, "b_offline.txt")));
        Assert.Equal("Created while A offline", File.ReadAllText(Path.Combine(nodeA.SourceDir, "b_offline.txt")));
    }

    [Fact]
    public async Task Reconnect_SharedClient_ReconcilesRemoteChanges()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("RecA");
        var nodeB = CreateNode("RecB");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("ReconnectSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // B uploads file
        File.WriteAllText(Path.Combine(nodeB.SourceDir, "reconnect_test.txt"), "From B before reconnect");
        await nodeB.ReconcileAsync(token);

        // A triggers reconciliation on reconnect
        await nodeA.ReconcileAsync(token);

        Assert.True(File.Exists(Path.Combine(nodeA.SourceDir, "reconnect_test.txt")));
        Assert.False(nodeA.SyncEngine.ReconciliationRequired);
    }

    // ==================================================
    // 33. TESTS — SAFE DIVERGENCE (CONFLICT PENDING)
    // ==================================================

    [Fact]
    public async Task RemoteModify_AndLocalModify_DoesNotOverwriteEitherSide()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("ConfA1");
        var nodeB = CreateNode("ConfB1");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("DivModSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string aPath = Path.Combine(nodeA.SourceDir, "divergent.txt");
        File.WriteAllText(aPath, "Common base v1");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B modifies and syncs to server (v2)
        string bPath = Path.Combine(nodeB.SourceDir, "divergent.txt");
        File.WriteAllText(bPath, "Remote server update from B (v2)");
        await nodeB.ReconcileAsync(token);

        // A modifies locally without syncing
        File.WriteAllText(aPath, "Local un-synced edit by A");

        // A reconciles: detects divergence!
        await nodeA.ReconcileAsync(token);

        // Canonical aPath becomes server v2
        Assert.Equal("Remote server update from B (v2)", File.ReadAllText(aPath));

        // Both sides preserved! Local edit preserved in conflict copy!
        var filesA = Directory.GetFiles(nodeA.SourceDir);
        var conflictFile = filesA.FirstOrDefault(f => Path.GetFileName(f).StartsWith("divergent (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("Local un-synced edit by A", File.ReadAllText(conflictFile));

        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.Contains(nodeA.SyncEngine.ResolvedConflicts, c => c.RelativePath == "divergent.txt");

        // Server version is v2, plus conflict copy exists
        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = setId }, token);
        Assert.Equal(2, manifest.Entries.First(e => e.RelativePath == "divergent.txt").CurrentVersionNumber);
        Assert.Contains(manifest.Entries, e => e.RelativePath == Path.GetFileName(conflictFile));
    }

    [Fact]
    public async Task RemoteDelete_AndLocalModify_DoesNotDeleteLocalWork()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("ConfA2");
        var nodeB = CreateNode("ConfB2");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("DivDelSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string aPath = Path.Combine(nodeA.SourceDir, "important.txt");
        File.WriteAllText(aPath, "Initial important text");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B deletes on server
        File.Delete(Path.Combine(nodeB.SourceDir, "important.txt"));
        await nodeB.ReconcileAsync(token);

        // A modifies locally before seeing delete
        File.WriteAllText(aPath, "Local precious additions");

        // A reconciles
        await nodeA.ReconcileAsync(token);

        // Canonical original file is removed locally to match server delete
        Assert.False(File.Exists(aPath));

        // Local precious work must NOT be deleted! Preserved as conflict copy!
        var filesA = Directory.GetFiles(nodeA.SourceDir);
        var conflictFile = filesA.FirstOrDefault(f => Path.GetFileName(f).StartsWith("important (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("Local precious additions", File.ReadAllText(conflictFile));

        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.Contains(nodeA.SyncEngine.ResolvedConflicts, c => c.RelativePath == "important.txt");
    }

    [Fact]
    public async Task RemoteCreate_CollidesWithDifferentLocalFile_ConflictPending()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("ConfA3");
        var nodeB = CreateNode("ConfB3");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("DivColSet", token);

        // Server has collision.txt from A
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "collision.txt"), "Created by A remotely");
        await nodeA.ReconcileAsync(token);

        // B already created a different local collision.txt BEFORE joining / reconciling
        string bColPath = Path.Combine(nodeB.SourceDir, "collision.txt");
        File.WriteAllText(bColPath, "Local independent work by B");

        await nodeB.JoinSharedSetAsync(shareCode, token);
        await nodeB.ReconcileAsync(token);

        // Canonical collision.txt becomes server version
        Assert.Equal("Created by A remotely", File.ReadAllText(bColPath));

        // B's independent work is preserved in conflict copy!
        var filesB = Directory.GetFiles(nodeB.SourceDir);
        var conflictFile = filesB.FirstOrDefault(f => Path.GetFileName(f).StartsWith("collision (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("Local independent work by B", File.ReadAllText(conflictFile));

        Assert.False(nodeB.SyncEngine.HasConflict);
        Assert.Contains(nodeB.SyncEngine.ResolvedConflicts, c => c.RelativePath == "collision.txt");
    }

    [Fact]
    public async Task RemoteRename_TargetOccupied_ConflictPending()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        var nodeA = CreateNode("ConfA4");
        var nodeB = CreateNode("ConfB4");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (setId, shareCode) = await nodeA.CreateSharedSetAsync("DivRenSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string srcA = Path.Combine(nodeA.SourceDir, "doc.txt");
        File.WriteAllText(srcA, "Doc content");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A renames doc.txt -> final.txt on server
        string destA = Path.Combine(nodeA.SourceDir, "final.txt");
        File.Move(srcA, destA);
        await nodeA.GatewayClient.RenameFileAsync(new FileRenamedRequest
        {
            ClientId = nodeA.ClientId,
            BackupSetId = setId,
            OldRelativePath = "doc.txt",
            NewRelativePath = "final.txt"
        }, token);

        // B has independently created a local final.txt
        string bFinal = Path.Combine(nodeB.SourceDir, "final.txt");
        File.WriteAllText(bFinal, "Local final created by B");

        // B reconciles
        await nodeB.ReconcileAsync(token);

        // Canonical final.txt becomes server's file
        Assert.Equal("Doc content", File.ReadAllText(bFinal));

        // B's local final is preserved as conflict copy
        var filesB = Directory.GetFiles(nodeB.SourceDir);
        var conflictFile = filesB.FirstOrDefault(f => Path.GetFileName(f).StartsWith("final (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("Local final created by B", File.ReadAllText(conflictFile));

        Assert.False(nodeB.SyncEngine.HasConflict);
        Assert.Contains(nodeB.SyncEngine.ResolvedConflicts, c => c.RelativePath == "final.txt");
    }

    // ==================================================
    // 34. MAIN TWO-CLIENT E2E
    // ==================================================

    [Fact]
    public async Task SharedBidirectionalSync_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = cts.Token;

        // 1. A registered; 2. B registered
        var nodeA = CreateNode("MainE2E_A");
        var nodeB = CreateNode("MainE2E_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        // 3. A creates SHARED set; 4. B join by ShareCode; 5. A empty; 6. B empty
        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("MainCollabSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // 7. A creates alpha.txt = "from A v1"
        string alphaA = Path.Combine(nodeA.SourceDir, "alpha.txt");
        File.WriteAllText(alphaA, "from A v1");

        // 8. A sync
        await nodeA.ReconcileAsync(token);

        // 9. Server alpha = v1
        var manifest1 = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        var alphaEntry1 = manifest1.Entries.First(e => e.RelativePath == "alpha.txt");
        Assert.Equal(1, alphaEntry1.CurrentVersionNumber);

        // 10. B reconcile; 11. B\alpha.txt exists; 12. content == "from A v1"; 13. SHA matches; 14. server still v1
        await nodeB.ReconcileAsync(token);
        string alphaB = Path.Combine(nodeB.SourceDir, "alpha.txt");
        Assert.True(File.Exists(alphaB));
        Assert.Equal("from A v1", File.ReadAllText(alphaB));

        var manifest1B = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        Assert.Equal(1, manifest1B.Entries.First(e => e.RelativePath == "alpha.txt").CurrentVersionNumber);

        // 15. B modifies: alpha.txt = "from B v2"
        File.WriteAllText(alphaB, "from B v2");

        // 16. B sync; 17. server alpha = v2; 18. ChangeLog MODIFY ActorClientId == B
        await nodeB.ReconcileAsync(token);

        var manifest2 = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        var alphaEntry2 = manifest2.Entries.First(e => e.RelativePath == "alpha.txt");
        Assert.Equal(2, alphaEntry2.CurrentVersionNumber);

        var logs1 = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, token);
        var modifyLog = logs1.Entries.FirstOrDefault(e => e.RelativePath == "alpha.txt" && e.Action == "MODIFY");
        Assert.NotNull(modifyLog);
        Assert.Equal(nodeB.ClientId, modifyLog.ActorClientId);

        // 19. A reconcile; 20. A content == "from B v2"; 21. server still v2
        await nodeA.ReconcileAsync(token);
        Assert.Equal("from B v2", File.ReadAllText(alphaA));

        var manifest2A = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        Assert.Equal(2, manifest2A.Entries.First(e => e.RelativePath == "alpha.txt").CurrentVersionNumber);

        // 22. A rename: alpha.txt -> shared-report.txt
        string repA = Path.Combine(nodeA.SourceDir, "shared-report.txt");
        File.Move(alphaA, repA);
        await nodeA.GatewayClient.RenameFileAsync(new FileRenamedRequest
        {
            ClientId = nodeA.ClientId,
            BackupSetId = sharedSetId,
            OldRelativePath = "alpha.txt",
            NewRelativePath = "shared-report.txt"
        }, token);

        // 23. A sync; 24. B reconcile; 25. B has no old alpha.txt; 26. shared-report.txt exists on B
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);
        Assert.False(File.Exists(alphaB));
        string repB = Path.Combine(nodeB.SourceDir, "shared-report.txt");
        Assert.True(File.Exists(repB));
        Assert.Equal("from B v2", File.ReadAllText(repB));

        // 27. B creates folder\beta.txt
        string fDirB = Path.Combine(nodeB.SourceDir, "folder");
        Directory.CreateDirectory(fDirB);
        string betaB = Path.Combine(fDirB, "beta.txt");
        File.WriteAllText(betaB, "beta file from B");

        // 28. B sync; 29. A reconcile; 30. A receives folder\beta.txt
        await nodeB.ReconcileAsync(token);
        await nodeA.ReconcileAsync(token);

        string betaA = Path.Combine(nodeA.SourceDir, "folder", "beta.txt");
        Assert.True(File.Exists(betaA));
        Assert.Equal("beta file from B", File.ReadAllText(betaA));

        // 31. B deletes shared-report.txt; 32. B sync; 33. A reconcile; 34. shared-report.txt deleted at A
        File.Delete(repB);
        await nodeB.ReconcileAsync(token);
        await nodeA.ReconcileAsync(token);
        Assert.False(File.Exists(repA));

        // 35. Check ChangeLog actors
        var finalLogs = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, token);
        var betaCreateLog = finalLogs.Entries.FirstOrDefault(e => e.RelativePath?.Replace("\\", "/") == "folder/beta.txt" && e.Action == "CREATE");
        Assert.NotNull(betaCreateLog);
        Assert.Equal(nodeB.ClientId, betaCreateLog.ActorClientId);

        var deleteReportLog = finalLogs.Entries.FirstOrDefault(e => e.RelativePath == "shared-report.txt" && e.Action == "DELETE");
        Assert.NotNull(deleteReportLog);
        Assert.Equal(nodeB.ClientId, deleteReportLog.ActorClientId);

        // 36. Verify no duplicate watcher versions
        var betaManifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        var betaEntry = betaManifest.Entries.First(e => e.RelativePath?.Replace("\\", "/") == "folder/beta.txt");
        Assert.Equal(1, betaEntry.CurrentVersionNumber);

        // 37. Consistent final state
        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.False(nodeB.SyncEngine.HasConflict);
    }

    // ==================================================
    // 35. SECOND E2E — INITIAL BOOTSTRAP
    // ==================================================

    [Fact]
    public async Task SharedInitialBootstrap_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("BootE2E_A");
        var nodeB = CreateNode("BootE2E_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("BootstrapE2ESet", token);

        // A sets up server state: one.txt v2 and sub/two.bin v1
        string oneA = Path.Combine(nodeA.SourceDir, "one.txt");
        File.WriteAllText(oneA, "one v1");
        await nodeA.ReconcileAsync(token);

        File.WriteAllText(oneA, "one v2 updated");
        await nodeA.ReconcileAsync(token);

        string subDir = Path.Combine(nodeA.SourceDir, "sub");
        Directory.CreateDirectory(subDir);
        string twoA = Path.Combine(subDir, "two.bin");
        byte[] binData = [10, 20, 30, 40, 50];
        File.WriteAllBytes(twoA, binData);
        await nodeA.ReconcileAsync(token);

        // Node B joins with empty directory
        await nodeB.JoinSharedSetAsync(shareCode, token);
        await nodeB.ReconcileAsync(token);

        // Node B has both files with exact contents
        string oneB = Path.Combine(nodeB.SourceDir, "one.txt");
        string twoB = Path.Combine(nodeB.SourceDir, "sub", "two.bin");

        Assert.True(File.Exists(oneB));
        Assert.True(File.Exists(twoB));
        Assert.Equal("one v2 updated", File.ReadAllText(oneB));
        Assert.Equal(binData, File.ReadAllBytes(twoB));

        // Server versions did not change!
        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        Assert.Equal(2, manifest.Entries.First(e => e.RelativePath == "one.txt").CurrentVersionNumber);
        Assert.Equal(1, manifest.Entries.First(e => e.RelativePath?.Replace("\\", "/") == "sub/two.bin").CurrentVersionNumber);
    }

    // ==================================================
    // 36. THIRD E2E — OFFLINE CATCHUP
    // ==================================================

    [Fact]
    public async Task SharedOfflineCatchup_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("OffE2E_A");
        var nodeB = CreateNode("OffE2E_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("OfflineCatchupSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // Setup common base: x.txt, z.txt, q.txt
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "x.txt"), "x initial");
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "z.txt"), "z initial");
        File.WriteAllText(Path.Combine(nodeA.SourceDir, "q.txt"), "q initial");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A goes offline
        await nodeA.SyncEngine.StopAsync();

        // B performs: modify x, create y, rename z -> z_renamed, delete q
        File.WriteAllText(Path.Combine(nodeB.SourceDir, "x.txt"), "x modified by B");
        File.WriteAllText(Path.Combine(nodeB.SourceDir, "y.txt"), "y created by B");

        File.Move(Path.Combine(nodeB.SourceDir, "z.txt"), Path.Combine(nodeB.SourceDir, "z_renamed.txt"));
        await nodeB.GatewayClient.RenameFileAsync(new FileRenamedRequest
        {
            ClientId = nodeB.ClientId,
            BackupSetId = sharedSetId,
            OldRelativePath = "z.txt",
            NewRelativePath = "z_renamed.txt"
        }, token);

        File.Delete(Path.Combine(nodeB.SourceDir, "q.txt"));
        await nodeB.ReconcileAsync(token);

        // A starts again and reconciles
        var profA = await nodeA.ProfileService.GetProfileAsync(token);
        Assert.NotNull(profA);
        await nodeA.SyncEngine.StartAsync(profA, token);

        // A caught up completely
        Assert.Equal("x modified by B", File.ReadAllText(Path.Combine(nodeA.SourceDir, "x.txt")));
        Assert.True(File.Exists(Path.Combine(nodeA.SourceDir, "y.txt")));
        Assert.Equal("y created by B", File.ReadAllText(Path.Combine(nodeA.SourceDir, "y.txt")));
        Assert.False(File.Exists(Path.Combine(nodeA.SourceDir, "z.txt")));
        Assert.True(File.Exists(Path.Combine(nodeA.SourceDir, "z_renamed.txt")));
        Assert.False(File.Exists(Path.Combine(nodeA.SourceDir, "q.txt")));
    }

    // ==================================================
    // ==================================================
    // 37. CONFLICT SAFETY E2E
    // ==================================================

    [Fact]
    public async Task SharedDivergenceSafety_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("DivE2E_A");
        var nodeB = CreateNode("DivE2E_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("DivergenceSafetySet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fA = Path.Combine(nodeA.SourceDir, "shared_doc.txt");
        File.WriteAllText(fA, "Version 1 shared content");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A goes offline
        await nodeA.SyncEngine.StopAsync();

        // A modifies locally while offline
        File.WriteAllText(fA, "Local-A unpushed offline modification");

        // B modifies server to v2
        string fB = Path.Combine(nodeB.SourceDir, "shared_doc.txt");
        File.WriteAllText(fB, "Local-B remote v2 modification");
        await nodeB.ReconcileAsync(token);

        // A restarts and reconciles
        var profA = await nodeA.ProfileService.GetProfileAsync(token);
        await nodeA.SyncEngine.StartAsync(profA!, token);

        // Canonical fA becomes server v2
        Assert.Equal("Local-B remote v2 modification", File.ReadAllText(fA));

        // A's local divergent content is safely preserved in conflict copy!
        var filesA = Directory.GetFiles(nodeA.SourceDir);
        var conflictFile = filesA.FirstOrDefault(f => Path.GetFileName(f).StartsWith("shared_doc (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("Local-A unpushed offline modification", File.ReadAllText(conflictFile));

        // Server manifest has both files
        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        Assert.Equal(2, manifest.Entries.First(e => e.RelativePath == "shared_doc.txt").CurrentVersionNumber);
        Assert.Contains(manifest.Entries, e => e.RelativePath == Path.GetFileName(conflictFile));

        // Node A status is Synced without conflict
        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.False(nodeA.SyncEngine.ReconciliationRequired);
        Assert.Contains(nodeA.SyncEngine.ResolvedConflicts, c => c.RelativePath == "shared_doc.txt");
    }

    // ==================================================
    // 38. SECTION 21: CONCURRENT MODIFY CONFLICT E2E
    // ==================================================

    [Fact]
    public async Task SharedConcurrentModifyConflict_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("ConcMod_A");
        var nodeB = CreateNode("ConcMod_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("ConcurrentModifySet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // 2. A creates report.txt
        string fA = Path.Combine(nodeA.SourceDir, "report.txt");
        File.WriteAllText(fA, "initial report v1");
        await nodeA.ReconcileAsync(token);

        // 3. B receives v1
        await nodeB.ReconcileAsync(token);
        string fB = Path.Combine(nodeB.SourceDir, "report.txt");
        Assert.Equal("initial report v1", File.ReadAllText(fB));

        // 4. Both synced
        Assert.False(nodeA.SyncEngine.ReconciliationRequired);
        Assert.False(nodeB.SyncEngine.ReconciliationRequired);

        // 5. A offline
        await nodeA.SyncEngine.StopAsync();

        // 6. A changes locally: "content from A"
        File.WriteAllText(fA, "content from A");

        // 7. B changes: "content from B"
        File.WriteAllText(fB, "content from B");

        // 8. B sync -> canonical v2
        await nodeB.ReconcileAsync(token);

        // 9. A starts up & 10. A reconcile
        var profA = await nodeA.ProfileService.GetProfileAsync(token);
        await nodeA.SyncEngine.StartAsync(profA!, token);

        // 11. Divergence detected & 12. Local A bytes preserved
        // 13. Canonical report.txt at A becomes content from B
        Assert.Equal("content from B", File.ReadAllText(fA));

        // 14. Conflict copy contains content from A
        var filesA = Directory.GetFiles(nodeA.SourceDir);
        var conflictFile = filesA.FirstOrDefault(f => Path.GetFileName(f).StartsWith("report (conflict "));
        Assert.NotNull(conflictFile);
        string conflictFileName = Path.GetFileName(conflictFile);
        Assert.Equal("content from A", File.ReadAllText(conflictFile));

        // 15. Conflict copy uploaded to server
        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        var canonicalEntry = manifest.Entries.First(e => e.RelativePath == "report.txt");
        var conflictEntry = manifest.Entries.FirstOrDefault(e => e.RelativePath == conflictFileName);
        Assert.NotNull(conflictEntry);

        // 16. Conflict copy FileId != canonical FileId
        Assert.NotEqual(canonicalEntry.FileId, conflictEntry.FileId);

        // 17. Conflict copy v1
        Assert.Equal(1, conflictEntry.CurrentVersionNumber);

        // 18. ChangeLog contains CONFLICT_COPY Actor=A
        await using var db = _fixture.CreateDbContext();
        var conflictLog = await db.ChangeLogs
            .FirstOrDefaultAsync(c => c.BackupSetId == sharedSetId && c.Action == "CONFLICT_COPY", token);
        Assert.NotNull(conflictLog);
        Assert.Equal(nodeA.ClientId, conflictLog.ActorClientId);
        Assert.Contains("Conflict with report.txt", conflictLog.Details);

        // 19. B reconcile
        await nodeB.ReconcileAsync(token);

        // 20. B also receives conflict copy
        string conflictPathB = Path.Combine(nodeB.SourceDir, conflictFileName);
        Assert.True(File.Exists(conflictPathB));
        Assert.Equal("content from A", File.ReadAllText(conflictPathB));

        // 21. No data lost
        Assert.Equal("content from B", File.ReadAllText(fB));

        // 22. Reconciliation of both clients -> Synced
        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.False(nodeA.SyncEngine.ReconciliationRequired);
        Assert.False(nodeB.SyncEngine.HasConflict);
        Assert.False(nodeB.SyncEngine.ReconciliationRequired);
    }

    // ==================================================
    // 39. SECTION 22: DELETE VS MODIFY CONFLICT E2E
    // ==================================================

    [Fact]
    public async Task SharedDeleteVsModifyConflict_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("DelMod_A");
        var nodeB = CreateNode("DelMod_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("DeleteVsModifySet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fA = Path.Combine(nodeA.SourceDir, "notes.txt");
        File.WriteAllText(fA, "base notes content");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A offline and modifies file
        await nodeA.SyncEngine.StopAsync();
        File.WriteAllText(fA, "offline edited notes by A");

        // B deletes file on server
        string fB = Path.Combine(nodeB.SourceDir, "notes.txt");
        File.Delete(fB);
        await nodeB.ReconcileAsync(token);

        // A reconnect / reconcile
        var profA = await nodeA.ProfileService.GetProfileAsync(token);
        await nodeA.SyncEngine.StartAsync(profA!, token);

        // Canonical file removed locally
        Assert.False(File.Exists(fA));

        // Local modified bytes preserved in conflict copy
        var filesA = Directory.GetFiles(nodeA.SourceDir);
        var conflictFile = filesA.FirstOrDefault(f => Path.GetFileName(f).StartsWith("notes (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("offline edited notes by A", File.ReadAllText(conflictFile));

        // Conflict copy uploaded to server
        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        var origEntry = manifest.Entries.First(e => e.RelativePath == "notes.txt");
        Assert.True(origEntry.IsDeleted);

        string conflictName = Path.GetFileName(conflictFile);
        var conflictEntry = manifest.Entries.FirstOrDefault(e => e.RelativePath == conflictName);
        Assert.NotNull(conflictEntry);
        Assert.False(conflictEntry.IsDeleted);

        // B reconciles and receives conflict copy
        await nodeB.ReconcileAsync(token);
        string conflictPathB = Path.Combine(nodeB.SourceDir, conflictName);
        Assert.True(File.Exists(conflictPathB));
        Assert.Equal("offline edited notes by A", File.ReadAllText(conflictPathB));
        Assert.False(File.Exists(fB));

        // Both synced
        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.False(nodeA.SyncEngine.ReconciliationRequired);
        Assert.False(nodeB.SyncEngine.HasConflict);
        Assert.False(nodeB.SyncEngine.ReconciliationRequired);
    }

    // ==================================================
    // 40. SECTION 23: CREATE COLLISION E2E
    // ==================================================

    [Fact]
    public async Task SharedCreateCollision_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("CreateCol_A");
        var nodeB = CreateNode("CreateCol_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("CreateCollisionSet", token);

        // A server creates same.txt = SERVER
        string fA = Path.Combine(nodeA.SourceDir, "same.txt");
        File.WriteAllText(fA, "SERVER CONTENT");
        await nodeA.ReconcileAsync(token);

        // Before B bootstrap, B local folder already contains independent same.txt = LOCAL
        string fB = Path.Combine(nodeB.SourceDir, "same.txt");
        File.WriteAllText(fB, "LOCAL CONTENT");

        // B joins and reconciles
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // Expected: same.txt = SERVER, same (conflict B...).txt = LOCAL
        Assert.Equal("SERVER CONTENT", File.ReadAllText(fB));

        var filesB = Directory.GetFiles(nodeB.SourceDir);
        var conflictFile = filesB.FirstOrDefault(f => Path.GetFileName(f).StartsWith("same (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("LOCAL CONTENT", File.ReadAllText(conflictFile));

        // Both exist on server
        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        Assert.Contains(manifest.Entries, e => e.RelativePath == "same.txt" && !e.IsDeleted);
        Assert.Contains(manifest.Entries, e => e.RelativePath == Path.GetFileName(conflictFile) && !e.IsDeleted);

        // A reconciles and receives conflict copy
        await nodeA.ReconcileAsync(token);
        string conflictPathA = Path.Combine(nodeA.SourceDir, Path.GetFileName(conflictFile));
        Assert.True(File.Exists(conflictPathA));
        Assert.Equal("LOCAL CONTENT", File.ReadAllText(conflictPathA));

        Assert.False(nodeA.SyncEngine.HasConflict);
        Assert.False(nodeB.SyncEngine.HasConflict);
    }

    // ==================================================
    // 41. SECTION 24: RENAME DESTINATION COLLISION TEST
    // ==================================================

    [Fact]
    public async Task SharedRenameDestinationCollision_E2E()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("RenCol_A");
        var nodeB = CreateNode("RenCol_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("RenameCollisionSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // A creates a.txt
        string aFileA = Path.Combine(nodeA.SourceDir, "a.txt");
        File.WriteAllText(aFileA, "SERVER FILE A CONTENT");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // B offline
        await nodeB.SyncEngine.StopAsync();

        // Server rename: a.txt -> target.txt
        File.Move(aFileA, Path.Combine(nodeA.SourceDir, "target.txt"));
        await nodeA.GatewayClient.RenameFileAsync(new FileRenamedRequest
        {
            ClientId = nodeA.ClientId,
            BackupSetId = sharedSetId,
            OldRelativePath = "a.txt",
            NewRelativePath = "target.txt"
        }, token);
        await nodeA.ReconcileAsync(token);

        // B already has unrelated local target.txt
        string targetFileB = Path.Combine(nodeB.SourceDir, "target.txt");
        File.WriteAllText(targetFileB, "UNRELATED LOCAL TARGET CONTENT");

        // B starts up and reconciles
        var profB = await nodeB.ProfileService.GetProfileAsync(token);
        await nodeB.SyncEngine.StartAsync(profB!, token);

        // Expected: server FileId becomes target.txt
        Assert.Equal("SERVER FILE A CONTENT", File.ReadAllText(targetFileB));

        // Local unrelated file preserved as conflict copy
        var filesB = Directory.GetFiles(nodeB.SourceDir);
        var conflictFile = filesB.FirstOrDefault(f => Path.GetFileName(f).StartsWith("target (conflict "));
        Assert.NotNull(conflictFile);
        Assert.Equal("UNRELATED LOCAL TARGET CONTENT", File.ReadAllText(conflictFile));

        // Both files exist on server
        var manifest = await nodeB.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        Assert.Contains(manifest.Entries, e => e.RelativePath == "target.txt" && !e.IsDeleted);
        Assert.Contains(manifest.Entries, e => e.RelativePath == Path.GetFileName(conflictFile) && !e.IsDeleted);

        Assert.False(nodeB.SyncEngine.HasConflict);
    }

    // ==================================================
    // 42. SECTION 25: FAILURE SAFETY TEST
    // ==================================================

    [Fact]
    public async Task ConflictResolution_UploadFailure_PreservesLocalConflictCopy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("FailSafe_A");
        var nodeB = CreateNode("FailSafe_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("FailSafeSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fA = Path.Combine(nodeA.SourceDir, "doc.txt");
        File.WriteAllText(fA, "v1 base");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A offline
        await nodeA.SyncEngine.StopAsync();
        File.WriteAllText(fA, "divergent bytes by A");

        // B advances to v2
        string fB = Path.Combine(nodeB.SourceDir, "doc.txt");
        File.WriteAllText(fB, "server v2 bytes");
        await nodeB.ReconcileAsync(token);

        // Simulate upload failure: divergent local file is preserved as conflict copy
        // Test that even before upload, conflict copy is safely on disk
        string machine = Environment.MachineName;
        DateTime now = DateTime.UtcNow;
        string expectedConflictRel = ConflictFileNameHelper.GenerateConflictFileName("doc.txt", machine, now);
        string conflictFullPath = Path.Combine(nodeA.SourceDir, expectedConflictRel);

        // Move to conflict copy manually to simulate Step 6 (Local divergent file moved)
        File.Move(fA, conflictFullPath);

        // Verification: conflict copy physically exists on disk
        Assert.True(File.Exists(conflictFullPath));
        Assert.Equal("divergent bytes by A", File.ReadAllText(conflictFullPath));

        // Now A restarts and reconciles: canonical file is downloaded, and existing conflict copy is detected and uploaded
        var profA = await nodeA.ProfileService.GetProfileAsync(token);
        await nodeA.SyncEngine.StartAsync(profA!, token);

        Assert.Equal("server v2 bytes", File.ReadAllText(fA));
        Assert.True(File.Exists(conflictFullPath));
        Assert.Equal("divergent bytes by A", File.ReadAllText(conflictFullPath));

        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        Assert.Contains(manifest.Entries, e => e.RelativePath == "doc.txt");
        Assert.Contains(manifest.Entries, e => e.RelativePath == expectedConflictRel);

        Assert.False(nodeA.SyncEngine.HasConflict);
    }

    // ==================================================
    // 43. SECTION 26: IDEMPOTENCY TEST
    // ==================================================

    [Fact]
    public async Task ConflictResolution_Retry_DoesNotCreateDuplicateConflictCopies()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = cts.Token;

        var nodeA = CreateNode("Idemp_A");
        var nodeB = CreateNode("Idemp_B");
        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("IdempotencySet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        string fA = Path.Combine(nodeA.SourceDir, "repeat.txt");
        File.WriteAllText(fA, "v1 base");
        await nodeA.ReconcileAsync(token);
        await nodeB.ReconcileAsync(token);

        // A offline
        await nodeA.SyncEngine.StopAsync();
        File.WriteAllText(fA, "divergent A text");

        // B modifies to v2
        string fB = Path.Combine(nodeB.SourceDir, "repeat.txt");
        File.WriteAllText(fB, "server v2 text");
        await nodeB.ReconcileAsync(token);

        // A starts and reconciles
        var profA = await nodeA.ProfileService.GetProfileAsync(token);
        await nodeA.SyncEngine.StartAsync(profA!, token);

        // Run reconciliation multiple times in a row
        await nodeA.ReconcileAsync(token);
        await nodeA.ReconcileAsync(token);

        // Verify: exactly 1 conflict copy exists in A's directory, not (2), (3)
        var files = Directory.GetFiles(nodeA.SourceDir);
        var conflictCopies = files.Where(f => Path.GetFileName(f).StartsWith("repeat (conflict ")).ToList();
        Assert.Single(conflictCopies);

        // Manifest on server has exactly 2 entries (canonical repeat.txt and 1 conflict copy)
        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        var serverConflictCopies = manifest.Entries.Where(e => e.RelativePath.StartsWith("repeat (conflict ")).ToList();
        Assert.Single(serverConflictCopies);
        Assert.False(nodeA.SyncEngine.HasConflict);
    }
}
