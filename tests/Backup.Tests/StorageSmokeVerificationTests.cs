using System.Security.Cryptography;
using System.Text;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Storage;
using Backup.StorageService;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Backup.SyncGateway;
using Backup.SyncGateway.Clients;
using Backup.SyncGateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backup.Tests;

public class StorageSmokeVerificationTests
{
    [Fact]
    public async Task StorageService_And_SyncGateway_FullSmokeVerification()
    {
        // 1. Setup StorageService on an isolated port with repo-relative data/storage
        int storagePort = 5022;
        int gatewayPort = 5020;

        string repoRoot = FileStorageService.ResolveStorageRoot("data/storage");
        var storageOptions = Options.Create(new StorageOptions { RootPath = repoRoot });
        var storageService = new FileStorageService(NullLogger<FileStorageService>.Instance, storageOptions);

        var storageWorkerOptions = Options.Create(new StorageServiceOptions { Host = "127.0.0.1", Port = storagePort });
        var storageWorker = new StorageServerWorker(
            NullLogger<StorageServerWorker>.Instance,
            NullLoggerFactory.Instance,
            storageWorkerOptions,
            storageService);

        using var storageCts = new CancellationTokenSource();
        var storageTask = storageWorker.StartAsync(storageCts.Token);
        await Task.Delay(200); // Allow listener to bind

        // 2. Setup SyncGateway pointing downstream to StorageService
        var gatewayOptions = Options.Create(new GatewayOptions { Host = "127.0.0.1", Port = gatewayPort });
        var downstreamOptions = Options.Create(new DownstreamServicesOptions
        {
            MetadataService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = 5001 },
            StorageService = new DownstreamServiceEndpoint { Host = "127.0.0.1", Port = storagePort }
        });

        var gatewayWorker = new GatewayServerWorker(
            NullLogger<GatewayServerWorker>.Instance,
            NullLoggerFactory.Instance,
            gatewayOptions,
            downstreamOptions);

        using var gatewayCts = new CancellationTokenSource();
        var gatewayTask = gatewayWorker.StartAsync(gatewayCts.Token);
        await Task.Delay(300); // Allow gateway to complete downstream Hello check and bind

        // 3. Gateway -> StorageService Hello Handshake check
        await using var gatewayToStorageClient = new StorageServiceClient("127.0.0.1", storagePort);
        var helloAck = await gatewayToStorageClient.HelloAsync("SyncGatewaySmokeCheck");
        Assert.NotNull(helloAck);
        Assert.True(helloAck.Success);
        Assert.Equal("Backup.StorageService", helloAck.ServerName);

        // 4. Send test file through StorageServiceClient
        var opId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var backupSetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        int version = 1;

        byte[] payload = Encoding.UTF8.GetBytes("Smoke verification content: Backup.StorageService + Backup.SyncGateway.");
        string expectedSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var storeRequest = new StoreFileRequest
        {
            OperationId = opId,
            ClientId = clientId,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = version,
            ExpectedSize = payload.Length,
            ExpectedSha256 = expectedSha
        };

        using var uploadStream = new MemoryStream(payload);
        var storeResult = await gatewayToStorageClient.StoreFileAsync(storeRequest, uploadStream);

        Assert.True(storeResult.Success);
        Assert.NotNull(storeResult.StorageKey);

        // 5. Verify physical file exists on disk at expected location
        string expectedKey = StorageKeyHelper.BuildStorageKey(clientId, backupSetId, fileId, version);
        Assert.Equal(expectedKey, storeResult.StorageKey);

        bool resolved = storageService.TryResolvePhysicalPath(storeResult.StorageKey, out var physicalPath, out var err);
        Assert.True(resolved);
        Assert.True(File.Exists(physicalPath), $"Expected file {physicalPath} to exist on disk.");

        byte[] diskBytes = await File.ReadAllBytesAsync(physicalPath);
        Assert.Equal(payload, diskBytes);
        Assert.Equal(payload.Length, diskBytes.Length);

        // 6. Download file back via GetStoredFile
        using var downloadStream = new MemoryStream();
        var getResult = await gatewayToStorageClient.GetStoredFileAsync(
            new GetStoredFileRequest { StorageKey = storeResult.StorageKey },
            downloadStream);

        Assert.True(getResult.Success);
        Assert.Equal(payload.Length, getResult.Size);
        Assert.Equal(expectedSha, getResult.Sha256);

        // 7. Compare SHA-256 and bytes
        byte[] downloadedBytes = downloadStream.ToArray();
        string downloadedSha = Convert.ToHexString(SHA256.HashData(downloadedBytes)).ToLowerInvariant();
        Assert.Equal(payload, downloadedBytes);
        Assert.Equal(expectedSha, downloadedSha);

        // 8. Clean up created smoke test data
        try
        {
            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }
            // Clean client directory inside data/storage/clients/{clientId}
            string clientDir = Path.Combine(repoRoot, "clients", clientId.ToString());
            if (Directory.Exists(clientDir))
            {
                Directory.Delete(clientDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }

        // 9. Stop processes cleanly
        gatewayCts.Cancel();
        storageCts.Cancel();

        try { await gatewayWorker.StopAsync(CancellationToken.None); } catch { }
        try { await storageWorker.StopAsync(CancellationToken.None); } catch { }
    }
}
