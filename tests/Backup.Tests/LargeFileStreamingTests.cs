using System.Security.Cryptography;
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

public class LargeFileStreamingTests : IAsyncLifetime
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

    public LargeFileStreamingTests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_large_" + uniqueId);
        _clientDataDir = Path.Combine(Path.GetTempPath(), "e2e_client_large_" + uniqueId);
        _sourceDir = Path.Combine(Path.GetTempPath(), "e2e_source_large_" + uniqueId);

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
    public async Task StreamLargeFile_32MiB_UploadAndRestore_MatchesSha256WithoutMemoryBuffering()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = testCts.Token;

        var client = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityService = new ClientIdentityService(Path.Combine(_clientDataDir, "client-state.json"));
        var backupService = new BackupClientService(client, identityService);

        await backupService.EnsureRegisteredAsync(token);
        var reg = await backupService.GetIdentityAsync(token);
        Assert.NotNull(reg?.ClientId);

        var setResp = await backupService.CreateBackupSetAsync("LargeFileSet", _sourceDir, token);
        Assert.True(setResp.Success);
        Guid backupSetId = setResp.BackupSetId;

        // 1. Generate 32 MiB file in chunks (streaming write, streaming hash)
        const int fileSize = 32 * 1024 * 1024; // 32 MiB
        string filePath = Path.Combine(_sourceDir, "large_32mb.dat");
        string expectedSha;

        byte[] chunk = new byte[64 * 1024]; // 64 KiB chunk
        Random.Shared.NextBytes(chunk);

        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            int remaining = fileSize;
            while (remaining > 0)
            {
                int toWrite = Math.Min(remaining, chunk.Length);
                fs.Write(chunk, 0, toWrite);
                sha.AppendData(chunk, 0, toWrite);
                remaining -= toWrite;
            }
            expectedSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        // 2. Perform single-file streaming backup
        var backupResult = await backupService.BackupSingleFileAsync(backupSetId, _sourceDir, filePath, token);
        Assert.True(backupResult.Success, $"Backup failed: {backupResult.ErrorMessage}");
        Assert.NotNull(backupResult.FileId);
        Assert.Equal(1, backupResult.VersionNumber);
        Guid fileId = backupResult.FileId.Value;

        // 3. Delete the original local file to prepare for restore test
        File.Delete(filePath);
        Assert.False(File.Exists(filePath));

        // 4. Perform streaming restore through client download helper
        var restoreResult = await client.DownloadAndVerifyRestoreFileAsync(new RestoreRequest
        {
            ClientId = reg.ClientId.Value,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = 1
        }, _sourceDir, token);

        Assert.Null(restoreResult.ErrorMessage);
        Assert.True(restoreResult.Success);
        Assert.True(File.Exists(restoreResult.TempFilePath));
        File.Move(restoreResult.TempFilePath, filePath);
        Assert.True(File.Exists(filePath));
        Assert.Equal(fileSize, new FileInfo(filePath).Length);

        // 5. Verify restored SHA256 matches expected without ReadAllBytes
        string restoredSha;
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
        {
            byte[] readBuffer = new byte[64 * 1024];
            int readBytes;
            while ((readBytes = fs.Read(readBuffer, 0, readBuffer.Length)) > 0)
            {
                sha.AppendData(readBuffer, 0, readBytes);
            }
            restoredSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        Assert.Equal(expectedSha, restoredSha);
    }
}
