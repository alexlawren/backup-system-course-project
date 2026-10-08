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

public class MultipleClientsTests : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly string _storageRoot;
    private readonly string _clientDataDirA;
    private readonly string _clientDataDirB;
    private readonly string _sourceDirA;
    private readonly string _sourceDirB;

    private readonly CancellationTokenSource _cts = new();

    private MetadataServerWorker? _metadataWorker;
    private StorageServerWorker? _storageWorker;
    private GatewayServerWorker? _gatewayWorker;

    private int _metadataPort;
    private int _storagePort;
    private int _gatewayPort;

    private DbContextOptions<MetadataDbContext>? _dbOptions;
    private TestDbContextFactory? _dbFactory;

    public MultipleClientsTests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_multi_" + uniqueId);
        _clientDataDirA = Path.Combine(Path.GetTempPath(), "e2e_clientA_" + uniqueId);
        _clientDataDirB = Path.Combine(Path.GetTempPath(), "e2e_clientB_" + uniqueId);
        _sourceDirA = Path.Combine(Path.GetTempPath(), "e2e_sourceA_" + uniqueId);
        _sourceDirB = Path.Combine(Path.GetTempPath(), "e2e_sourceB_" + uniqueId);

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_clientDataDirA);
        Directory.CreateDirectory(_clientDataDirB);
        Directory.CreateDirectory(_sourceDirA);
        Directory.CreateDirectory(_sourceDirB);
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
        try { Directory.Delete(_clientDataDirA, recursive: true); } catch { }
        try { Directory.Delete(_clientDataDirB, recursive: true); } catch { }
        try { Directory.Delete(_sourceDirA, recursive: true); } catch { }
        try { Directory.Delete(_sourceDirB, recursive: true); } catch { }

        _cts.Dispose();
    }

    [Fact]
    public async Task MultipleClients_ConnectedToSameGateway_HaveIsolatedMetadataAndStorage()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = testCts.Token;

        // Client A
        var clientA = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityA = new ClientIdentityService(Path.Combine(_clientDataDirA, "client-state.json"));
        var serviceA = new BackupClientService(clientA, identityA);
        await serviceA.EnsureRegisteredAsync(token);
        var idA = (await serviceA.GetIdentityAsync(token))!.ClientId!.Value;
        var setRespA = await serviceA.CreateBackupSetAsync("SetA", _sourceDirA, token);
        Assert.True(setRespA.Success);
        Guid setIdA = setRespA.BackupSetId;

        // Client B
        var clientB = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityB = new ClientIdentityService(Path.Combine(_clientDataDirB, "client-state.json"));
        var serviceB = new BackupClientService(clientB, identityB);
        await serviceB.EnsureRegisteredAsync(token);
        var idB = (await serviceB.GetIdentityAsync(token))!.ClientId!.Value;
        var setRespB = await serviceB.CreateBackupSetAsync("SetB", _sourceDirB, token);
        Assert.True(setRespB.Success);
        Guid setIdB = setRespB.BackupSetId;

        // Clients must have different identities
        Assert.NotEqual(idA, idB);
        Assert.NotEqual(setIdA, setIdB);

        // Upload file A
        string fileA = Path.Combine(_sourceDirA, "doc_a.txt");
        File.WriteAllText(fileA, "Content for client A");
        var resA = await serviceA.BackupSingleFileAsync(setIdA, _sourceDirA, fileA, token);
        Assert.True(resA.Success);
        Assert.Contains(idA.ToString(), resA.StorageKey!);
        Assert.Contains(setIdA.ToString(), resA.StorageKey!);

        // Upload file B
        string fileB = Path.Combine(_sourceDirB, "doc_b.txt");
        File.WriteAllText(fileB, "Content for client B");
        var resB = await serviceB.BackupSingleFileAsync(setIdB, _sourceDirB, fileB, token);
        Assert.True(resB.Success);
        Assert.Contains(idB.ToString(), resB.StorageKey!);
        Assert.Contains(setIdB.ToString(), resB.StorageKey!);

        // Abruptly dispose Client A
        clientA.Dispose();

        // Client B should still work perfectly
        string fileB2 = Path.Combine(_sourceDirB, "doc_b2.txt");
        File.WriteAllText(fileB2, "Another content for B");
        var resB2 = await serviceB.BackupSingleFileAsync(setIdB, _sourceDirB, fileB2, token);
        Assert.True(resB2.Success);

        // Verify file listings are completely isolated
        var filesA = await clientB.GetFilesAsync(new GetFilesRequest { BackupSetId = setIdA }, token);
        Assert.True(filesA.Success);
        Assert.Single(filesA.Files);
        Assert.Equal("doc_a.txt", filesA.Files[0].RelativePath);

        var filesB = await clientB.GetFilesAsync(new GetFilesRequest { BackupSetId = setIdB }, token);
        Assert.True(filesB.Success);
        Assert.Equal(2, filesB.Files.Count);
    }
}
