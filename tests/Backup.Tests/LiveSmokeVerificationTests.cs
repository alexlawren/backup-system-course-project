using System.Security.Cryptography;
using System.Text;
using Backup.Client.Web.Services;
using Backup.MetadataService.Data;
using Backup.Shared.Messages;
using Backup.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class LiveSmokeVerificationTests
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    [Fact]
    public async Task Live_Four_Services_Smoke_Verification_V1_And_V2()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string clientStatePath = Path.Combine(repoRoot, "data", "client", "client-state.json");
        string localRoot = Path.Combine(repoRoot, "data", "smoke_ui_test");
        string testFilePath = Path.Combine(localRoot, "notes.txt");
        string storageRoot = Path.Combine(repoRoot, "data", "storage");

        Directory.CreateDirectory(localRoot);
        var identityService = new ClientIdentityService(clientStatePath);
        var gatewayClient = new BackupGatewayClient("127.0.0.1", 5000);
        var clientService = new BackupClientService(gatewayClient, identityService);

        // 0. Skip if live standalone cluster is not active on port 5000
        if (!await CanConnectAsync("127.0.0.1", 5000))
        {
            return;
        }

        // 1. Hello / Ping Gateway
        bool ping = await clientService.PingGatewayAsync();
        Assert.True(ping, "Live SyncGateway ping failed.");

        // 2. Register Client
        bool reg = await clientService.EnsureRegisteredAsync();
        Assert.True(reg, "Client registration through live Gateway failed.");

        var clientState = await clientService.GetIdentityAsync();
        Assert.NotNull(clientState.ClientId);
        Assert.True(File.Exists(clientStatePath), "client-state.json was not created.");

        // 3. Create BackupSet
        var bs = await clientService.CreateBackupSetAsync("LiveSmokeSet", localRoot);
        Assert.True(bs.Success, $"CreateBackupSet failed: {bs.ErrorMessage}");
        Assert.NotEqual(Guid.Empty, bs.BackupSetId);

        // 4. Create local file content v1
        byte[] v1Bytes = Encoding.UTF8.GetBytes("Live Smoke Test Content - Version 1");
        await File.WriteAllBytesAsync(testFilePath, v1Bytes);
        string v1Sha = Convert.ToHexString(SHA256.HashData(v1Bytes)).ToLowerInvariant();

        // 5. Backup v1
        var v1Result = await clientService.BackupSingleFileAsync(bs.BackupSetId, localRoot, testFilePath);
        Assert.True(v1Result.Success, $"V1 backup failed: {v1Result.ErrorMessage}");
        Assert.Equal(1, v1Result.VersionNumber);
        Assert.NotNull(v1Result.StorageKey);

        // 6. Verify Physical File for v1
        Assert.True(StorageKeyHelper.TryResolvePhysicalPath(storageRoot, v1Result.StorageKey, out var v1PhysicalPath, out _));
        Assert.True(File.Exists(v1PhysicalPath), $"Physical storage file missing for v1 at: {v1PhysicalPath}");
        byte[] v1ActualBytes = await File.ReadAllBytesAsync(v1PhysicalPath);
        Assert.Equal(v1Bytes, v1ActualBytes);

        // 7. Modify file content for v2
        byte[] v2Bytes = Encoding.UTF8.GetBytes("Live Smoke Test Content - Version 2 (UPDATED)");
        await File.WriteAllBytesAsync(testFilePath, v2Bytes);
        string v2Sha = Convert.ToHexString(SHA256.HashData(v2Bytes)).ToLowerInvariant();

        // 8. Backup v2
        var v2Result = await clientService.BackupSingleFileAsync(bs.BackupSetId, localRoot, testFilePath);
        Assert.True(v2Result.Success, $"V2 backup failed: {v2Result.ErrorMessage}");
        Assert.Equal(2, v2Result.VersionNumber);
        Assert.NotNull(v2Result.StorageKey);
        Assert.NotEqual(v1Result.StorageKey, v2Result.StorageKey);

        // 9. Verify Physical File for v2 and immutability of v1
        Assert.True(StorageKeyHelper.TryResolvePhysicalPath(storageRoot, v2Result.StorageKey, out var v2PhysicalPath, out _));
        Assert.True(File.Exists(v1PhysicalPath), "Physical storage file for v1 disappeared!");
        Assert.True(File.Exists(v2PhysicalPath), "Physical storage file for v2 missing!");

        byte[] v1PreservedBytes = await File.ReadAllBytesAsync(v1PhysicalPath);
        Assert.Equal(v1Bytes, v1PreservedBytes);

        byte[] v2ActualBytes = await File.ReadAllBytesAsync(v2PhysicalPath);
        Assert.Equal(v2Bytes, v2ActualBytes);

        // 10. Verify PostgreSQL records
        var dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        await using var db = new MetadataDbContext(dbOptions);

        var file = await db.Files.FindAsync(v1Result.FileId!.Value);
        Assert.NotNull(file);
        Assert.Equal(2, file.CurrentVersionNumber);

        var versions = await db.FileVersions
            .Where(v => v.FileId == v1Result.FileId.Value)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(v1Result.StorageKey, versions[0].StorageKey);
        Assert.Equal(v2Result.StorageKey, versions[1].StorageKey);

        var logs = await db.ChangeLogs
            .Where(l => l.FileId == v1Result.FileId.Value)
            .OrderBy(l => l.CreatedAtUtc)
            .ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal("CREATE", logs[0].Action);
        Assert.Equal("MODIFY", logs[1].Action);
    }

    private static async Task<bool> CanConnectAsync(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(300);
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
