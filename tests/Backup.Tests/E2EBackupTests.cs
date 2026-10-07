using System.Security.Cryptography;
using System.Text;
using Backup.Client.Web.Configuration;
using Backup.Client.Web.Services;
using Backup.MetadataService;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
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

public class E2EBackupTests : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly string _storageRoot;
    private readonly string _clientDataDir;
    private readonly string _clientWorkingDir;

    private readonly CancellationTokenSource _cts = new();

    private MetadataServerWorker? _metadataWorker;
    private StorageServerWorker? _storageWorker;
    private GatewayServerWorker? _gatewayWorker;

    private int _metadataPort;
    private int _storagePort;
    private int _gatewayPort;

    private DbContextOptions<MetadataDbContext>? _dbOptions;
    private TestDbContextFactory? _dbFactory;

    public E2EBackupTests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_" + uniqueId);
        _clientDataDir = Path.Combine(Path.GetTempPath(), "e2e_client_data_" + uniqueId);
        _clientWorkingDir = Path.Combine(Path.GetTempPath(), "e2e_client_work_" + uniqueId);

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_clientDataDir);
        Directory.CreateDirectory(_clientWorkingDir);
    }

    public async Task InitializeAsync()
    {
        // 1. Setup MetadataService
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

        // Wait for metadata port
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_metadataWorker.BoundPort == null || _metadataWorker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException("MetadataServerWorker failed to bind.");
            await Task.Delay(20);
        }
        _metadataPort = _metadataWorker.BoundPort.Value;

        // 2. Setup StorageService
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
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException("StorageServerWorker failed to bind.");
            await Task.Delay(20);
        }
        _storagePort = _storageWorker.BoundPort.Value;

        // 3. Setup SyncGateway
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
            if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException("GatewayServerWorker failed to bind.");
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

        _cts.Dispose();

        // Safe cleanup of only test directories
        try
        {
            if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true);
            if (Directory.Exists(_clientDataDir)) Directory.Delete(_clientDataDir, true);
            if (Directory.Exists(_clientWorkingDir)) Directory.Delete(_clientWorkingDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task Full_E2E_SingleFile_Backup_Lifecycle_V1_To_V2_Succeeds()
    {
        // Setup client infrastructure
        string stateFilePath = Path.Combine(_clientDataDir, "client-state.json");
        var identityService = new ClientIdentityService(stateFilePath);

        var gatewayClient = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var clientService = new BackupClientService(gatewayClient, identityService);

        // 1. Register Client via Gateway -> Metadata
        var regSuccess = await clientService.EnsureRegisteredAsync();
        Assert.True(regSuccess);

        var clientIdentity = await clientService.GetIdentityAsync();
        Assert.NotNull(clientIdentity.ClientId);
        Assert.NotEqual(Guid.Empty, clientIdentity.ClientId.Value);

        // 2. Create BackupSet via Gateway -> Metadata
        string setName = $"Set-{Guid.NewGuid():N}";
        var bsResponse = await clientService.CreateBackupSetAsync(setName, _clientWorkingDir);
        Assert.True(bsResponse.Success);
        Assert.NotEqual(Guid.Empty, bsResponse.BackupSetId);

        // 3. Create temporary local test file
        string localSubdir = Path.Combine(_clientWorkingDir, "documents");
        Directory.CreateDirectory(localSubdir);
        string localFilePath = Path.Combine(localSubdir, "report.txt");

        byte[] v1Content = Encoding.UTF8.GetBytes("Coursework Report Content - Version 1");
        await File.WriteAllBytesAsync(localFilePath, v1Content);
        string v1Sha = Convert.ToHexString(SHA256.HashData(v1Content)).ToLowerInvariant();

        // 4. Perform Version 1 Backup
        var v1Result = await clientService.BackupSingleFileAsync(bsResponse.BackupSetId, _clientWorkingDir, localFilePath);

        // 5. Assert Version 1 Result
        Assert.True(v1Result.Success);
        Assert.NotNull(v1Result.FileId);
        Assert.Equal(1, v1Result.VersionNumber);
        Assert.NotNull(v1Result.StorageKey);

        // 6. Verify PostgreSQL Version 1 metadata
        await using (var db = await _dbFactory!.CreateDbContextAsync())
        {
            var file = await db.Files.FindAsync(v1Result.FileId.Value);
            Assert.NotNull(file);
            Assert.Equal("documents/report.txt", file.RelativePath);
            Assert.Equal(1, file.CurrentVersionNumber);

            var v1Record = await db.FileVersions
                .FirstOrDefaultAsync(v => v.FileId == v1Result.FileId.Value && v.VersionNumber == 1);
            Assert.NotNull(v1Record);
            Assert.Equal(v1Result.StorageKey, v1Record.StorageKey);
            Assert.Equal(v1Content.Length, v1Record.Size);
            Assert.Equal(v1Sha, v1Record.Sha256);

            var logs = await db.ChangeLogs
                .Where(cl => cl.FileId == v1Result.FileId.Value)
                .OrderBy(cl => cl.CreatedAtUtc)
                .ToListAsync();
            Assert.Single(logs);
            Assert.Equal("CREATE", logs[0].Action);
            Assert.Equal("COMMITTED", logs[0].Status);
        }

        // 7. Verify physical storage file exists and matches v1 bytes & sha
        Assert.True(StorageKeyHelper.TryResolvePhysicalPath(_storageRoot, v1Result.StorageKey, out var v1PhysicalPath, out _));
        Assert.True(File.Exists(v1PhysicalPath));
        byte[] v1StoredBytes = await File.ReadAllBytesAsync(v1PhysicalPath);
        Assert.Equal(v1Content, v1StoredBytes);
        Assert.Equal(v1Sha, Convert.ToHexString(SHA256.HashData(v1StoredBytes)).ToLowerInvariant());

        // 8. Modify file content for Version 2
        byte[] v2Content = Encoding.UTF8.GetBytes("Coursework Report Content - Version 2 (UPDATED)");
        await File.WriteAllBytesAsync(localFilePath, v2Content);
        string v2Sha = Convert.ToHexString(SHA256.HashData(v2Content)).ToLowerInvariant();

        // 9. Perform Version 2 Backup
        var v2Result = await clientService.BackupSingleFileAsync(bsResponse.BackupSetId, _clientWorkingDir, localFilePath);

        // 10. Assert Version 2 Result
        Assert.True(v2Result.Success);
        Assert.Equal(v1Result.FileId, v2Result.FileId);
        Assert.Equal(2, v2Result.VersionNumber);
        Assert.NotNull(v2Result.StorageKey);
        Assert.NotEqual(v1Result.StorageKey, v2Result.StorageKey);

        // 11. Verify PostgreSQL Version 2 metadata
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var file = await db.Files.FindAsync(v1Result.FileId.Value);
            Assert.NotNull(file);
            Assert.Equal(2, file.CurrentVersionNumber);

            var v2Record = await db.FileVersions
                .FirstOrDefaultAsync(v => v.FileId == v1Result.FileId.Value && v.VersionNumber == 2);
            Assert.NotNull(v2Record);
            Assert.Equal(v2Result.StorageKey, v2Record.StorageKey);
            Assert.Equal(v2Content.Length, v2Record.Size);
            Assert.Equal(v2Sha, v2Record.Sha256);

            var logs = await db.ChangeLogs
                .Where(cl => cl.FileId == v1Result.FileId.Value)
                .OrderBy(cl => cl.CreatedAtUtc)
                .ToListAsync();
            Assert.Equal(2, logs.Count);
            Assert.Equal("CREATE", logs[0].Action);
            Assert.Equal("MODIFY", logs[1].Action);
        }

        // 12. Verify both physical storage versions exist and V1 is unmodified
        Assert.True(StorageKeyHelper.TryResolvePhysicalPath(_storageRoot, v2Result.StorageKey, out var v2PhysicalPath, out _));
        Assert.True(File.Exists(v1PhysicalPath));
        Assert.True(File.Exists(v2PhysicalPath));

        byte[] v1CheckBytes = await File.ReadAllBytesAsync(v1PhysicalPath);
        Assert.Equal(v1Content, v1CheckBytes); // Version 1 is immutable and preserved

        byte[] v2StoredBytes = await File.ReadAllBytesAsync(v2PhysicalPath);
        Assert.Equal(v2Content, v2StoredBytes);
        Assert.Equal(v2Sha, Convert.ToHexString(SHA256.HashData(v2StoredBytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task Failure_E2E_StorageFailure_DoesNotCreateFileVersionOrIncrementNumber()
    {
        // Setup client infrastructure
        string stateFilePath = Path.Combine(_clientDataDir, "client-state-failure.json");
        var identityService = new ClientIdentityService(stateFilePath);
        var gatewayClient = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var clientService = new BackupClientService(gatewayClient, identityService);

        // 1. Register and create BackupSet
        await clientService.EnsureRegisteredAsync();
        var clientIdentity = await clientService.GetIdentityAsync();
        var bsResponse = await clientService.CreateBackupSetAsync("FailTestSet", _clientWorkingDir);
        Assert.True(bsResponse.Success);

        // 2. Connect raw socket to Gateway to simulate failure during streaming
        using var tcpClient = new System.Net.Sockets.TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", _gatewayPort);
        var stream = tcpClient.GetStream();

        var reader = new ProtocolFrameReader();
        var writer = new ProtocolFrameWriter();

        // 3. Send Hello
        await writer.WriteJsonFrameAsync(stream, MessageType.Hello, new HelloRequest
        {
            ClientName = "RawTestClient",
            Role = "Client",
            ClientId = clientIdentity.ClientId
        });
        var helloRespHeader = await reader.ReadHeaderAsync(stream);
        await reader.ReadJsonBodyAsync<HelloResponse>(stream, helloRespHeader!.BodyLength);

        // 4. Send FileChanged with Size = 100
        byte[] expectedContent = new byte[100];
        string expectedSha = Convert.ToHexString(SHA256.HashData(expectedContent)).ToLowerInvariant();
        string relativePath = "failed_upload.txt";

        await writer.WriteJsonFrameAsync(stream, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = bsResponse.BackupSetId,
            RelativePath = relativePath,
            Size = 100,
            Sha256 = expectedSha
        });

        // 5. Receive UploadRequired
        var uploadReqHeader = await reader.ReadHeaderAsync(stream);
        Assert.Equal(MessageType.UploadRequired, uploadReqHeader!.MessageType);
        var uploadReq = await reader.ReadJsonBodyAsync<UploadRequiredResponse>(stream, uploadReqHeader.BodyLength);
        Assert.NotNull(uploadReq);
        Assert.True(uploadReq.UploadRequired);
        var fileId = uploadReq.FileId!.Value;

        // 6. Send corrupt binary payload with SIZE MISMATCH (send 50 bytes instead of 100)
        var binHeader = new ProtocolHeader
        {
            MessageType = MessageType.UploadFile,
            ContentType = ProtocolContentTypes.ApplicationOctetStream,
            BodyLength = 50 // Size mismatch!
        };
        await writer.WriteFrameAsync(stream, binHeader, new byte[50]);

        // 7. Receive UploadCompleted with failure
        var resultHeader = await reader.ReadHeaderAsync(stream);
        Assert.Equal(MessageType.UploadCompleted, resultHeader!.MessageType);
        var completed = await reader.ReadJsonBodyAsync<UploadCompletedResponse>(stream, resultHeader.BodyLength);

        Assert.NotNull(completed);
        Assert.False(completed.Success);
        Assert.Equal(BackupErrorCodes.SizeMismatch, completed.ErrorCode);

        // 8. Assert PostgreSQL: FileVersion was NOT created and CurrentVersionNumber was NOT incremented
        await using (var db = await _dbFactory!.CreateDbContextAsync())
        {
            var file = await db.Files.FindAsync(fileId);
            if (file != null)
            {
                Assert.Equal(0, file.CurrentVersionNumber); // Not incremented
            }

            var versions = await db.FileVersions.Where(v => v.FileId == fileId).ToListAsync();
            Assert.Empty(versions); // FileVersion was NOT created
        }
    }
}
