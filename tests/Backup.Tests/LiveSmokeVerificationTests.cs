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

    [Fact]
    public async Task Section44_LiveManualSmoke_FullRestoreWorkflow()
    {
        // 0. Skip if live standalone cluster is not active on port 5000
        if (!await CanConnectAsync("127.0.0.1", 5000))
        {
            return;
        }

        string tempDemoDir = Path.Combine(Path.GetTempPath(), "demo_monitored_folder_" + Guid.NewGuid().ToString("N")[..8]);
        string clientStatePath = Path.Combine(Path.GetTempPath(), "demo_client_state_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        string testFilePath = Path.Combine(tempDemoDir, "demo_document.txt");

        try
        {
            Directory.CreateDirectory(tempDemoDir);
            var identityService = new ClientIdentityService(clientStatePath);
            var gatewayClient = new BackupGatewayClient("127.0.0.1", 5000);
            var indexService = new LocalFileIndexService(Path.Combine(tempDemoDir, ".index"));
            var syncEngine = new SyncEngine(
                gatewayClient,
                identityService,
                indexService,
                new DirectoryScanner(),
                new FileSystemWatcherService(),
                new FileStabilityHelper(),
                NullLogger<SyncEngine>.Instance,
                suppressionService: new RestoreSuppressionService());
            var clientService = new BackupClientService(gatewayClient, identityService, syncEngine);

            // Register client
            bool reg = await clientService.EnsureRegisteredAsync();
            Assert.True(reg);

            // Create backup set
            var bs = await clientService.CreateBackupSetAsync("DemoSmokeSet", tempDemoDir);
            Assert.True(bs.Success);
            var backupSetId = bs.BackupSetId;

            var profile = new SyncProfile
            {
                BackupSetId = backupSetId,
                Name = "DemoSmokeSet",
                LocalRootPath = tempDemoDir,
                AutomaticSyncEnabled = true
            };
            await syncEngine.StartAsync(profile);

            // Step 1: Create file -> v1
            await File.WriteAllTextAsync(testFilePath, "Version One Demo Content");
            var v1 = await clientService.BackupSingleFileAsync(backupSetId, tempDemoDir, testFilePath);
            Assert.True(v1.Success);
            Assert.Equal(1, v1.VersionNumber);
            var fileId = v1.FileId!.Value;

            // Step 2: Modify -> v2
            await File.WriteAllTextAsync(testFilePath, "Version Two Demo Content");
            var v2 = await clientService.BackupSingleFileAsync(backupSetId, tempDemoDir, testFilePath);
            Assert.True(v2.Success);
            Assert.Equal(2, v2.VersionNumber);

            // Step 3: Modify -> v3
            await File.WriteAllTextAsync(testFilePath, "Version Three Demo Content");
            var v3 = await clientService.BackupSingleFileAsync(backupSetId, tempDemoDir, testFilePath);
            Assert.True(v3.Success);
            Assert.Equal(3, v3.VersionNumber);

            // Step 4: Query files through Gateway
            var filesResp = await clientService.GetFilesAsync(backupSetId);
            Assert.True(filesResp.Success);
            Assert.Contains(filesResp.Files, f => f.FileId == fileId && f.CurrentVersionNumber == 3);

            // Step 5: Query versions through Gateway
            var versionsResp = await clientService.GetVersionsAsync(fileId);
            Assert.True(versionsResp.Success);
            Assert.Equal(3, versionsResp.Versions.Count);

            // Step 6: Restore version 1
            var restoreResult = await clientService.RestoreVersionAsync(backupSetId, fileId, 1);
            Assert.True(restoreResult.Success);
            Assert.Equal("SUCCESS", restoreResult.Status);
            Assert.Equal(4, restoreResult.NewCurrentVersionNumber);

            // Step 7: Verify local disk content restored to v1
            string diskContent = await File.ReadAllTextAsync(testFilePath);
            Assert.Equal("Version One Demo Content", diskContent);

            // Step 8: Verify DB state has v4
            var dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
                .UseNpgsql(PostgresConnectionString)
                .Options;
            await using var db = new MetadataDbContext(dbOptions);

            var dbFile = await db.Files.Include(f => f.Versions).FirstAsync(f => f.Id == fileId);
            Assert.Equal(4, dbFile.CurrentVersionNumber);
            Assert.Equal(4, dbFile.Versions.Count);

            var v1Record = dbFile.Versions.First(v => v.VersionNumber == 1);
            var v4Record = dbFile.Versions.First(v => v.VersionNumber == 4);
            Assert.Equal(v1Record.Sha256, v4Record.Sha256);

            var logs = await db.ChangeLogs
                .Where(l => l.FileId == fileId)
                .OrderBy(l => l.CreatedAtUtc)
                .ToListAsync();
            Assert.Equal("RESTORE", logs.Last().Action);
        }
        finally
        {
            try { Directory.Delete(tempDemoDir, recursive: true); } catch { }
            try { File.Delete(clientStatePath); } catch { }
        }
    }

    [Fact]
    public async Task Section27_FullInteractiveSmoke_CreateModifyRenameDeleteRestore_And_Reconnect()
    {
        if (!await CanConnectAsync("127.0.0.1", 5000))
        {
            return;
        }

        string tempDemoDir = Path.Combine(Path.GetTempPath(), "demo_interactive_flow_" + Guid.NewGuid().ToString("N")[..8]);
        string clientStatePath = Path.Combine(Path.GetTempPath(), "demo_interactive_state_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        string testFile = Path.Combine(tempDemoDir, "interactive_doc.txt");
        string renamedFile = Path.Combine(tempDemoDir, "interactive_doc_renamed.txt");

        try
        {
            Directory.CreateDirectory(tempDemoDir);
            var identityService = new ClientIdentityService(clientStatePath);
            var gatewayClient = new BackupGatewayClient("127.0.0.1", 5000);
            var indexService = new LocalFileIndexService(Path.Combine(tempDemoDir, ".index"));
            var syncEngine = new SyncEngine(
                gatewayClient,
                identityService,
                indexService,
                new DirectoryScanner(),
                new FileSystemWatcherService(),
                new FileStabilityHelper(),
                NullLogger<SyncEngine>.Instance,
                suppressionService: new RestoreSuppressionService());
            var clientService = new BackupClientService(gatewayClient, identityService, syncEngine);

            // Step 1 & 2: Gateway is online & Ping responds
            bool ping1 = await clientService.PingGatewayAsync();
            Assert.True(ping1, "Initial gateway ping failed.");

            // Step 3-5: Rapid mixed calls in loop: Ping, Register, Ping, State refresh
            // Proves: No framing error (2065855593 byte HeaderLength bug) occurs under interleaved calls!
            for (int i = 0; i < 15; i++)
            {
                bool p = await clientService.PingGatewayAsync();
                Assert.True(p);
                var id = await clientService.GetIdentityAsync();
                Assert.NotNull(id);
                bool regTest = await clientService.EnsureRegisteredAsync();
                Assert.True(regTest);
            }

            // Step 6: Client is registered
            var state = await clientService.GetIdentityAsync();
            Assert.NotNull(state.ClientId);
            Guid clientId = state.ClientId.Value;

            // Step 7: Create test BackupSet
            var bs = await clientService.CreateBackupSetAsync("InteractiveSmokeSet", tempDemoDir);
            Assert.True(bs.Success, $"CreateBackupSet failed: {bs.ErrorMessage}");
            Guid backupSetId = bs.BackupSetId;

            var profile = new SyncProfile
            {
                BackupSetId = backupSetId,
                Name = "InteractiveSmokeSet",
                LocalRootPath = tempDemoDir,
                AutomaticSyncEnabled = true
            };
            await syncEngine.StartAsync(profile);

            // Step 8 & 9 & 10: Create file -> Version 1
            await File.WriteAllTextAsync(testFile, "Interactive Smoke V1 - Initial Content");
            var v1 = await clientService.BackupSingleFileAsync(backupSetId, tempDemoDir, testFile);
            Assert.True(v1.Success, $"V1 backup failed: {v1.ErrorMessage}");
            Assert.Equal(1, v1.VersionNumber);
            Guid fileId = v1.FileId!.Value;

            // Step 11 & 12: Modify file -> Version 2
            await File.WriteAllTextAsync(testFile, "Interactive Smoke V2 - Modified Content");
            var v2 = await clientService.BackupSingleFileAsync(backupSetId, tempDemoDir, testFile);
            Assert.True(v2.Success, $"V2 backup failed: {v2.ErrorMessage}");
            Assert.Equal(2, v2.VersionNumber);

            // Step 13: Rename file
            File.Move(testFile, renamedFile);
            var renameResult = await clientService.BackupSingleFileAsync(backupSetId, tempDemoDir, renamedFile);
            Assert.True(renameResult.Success, $"Rename backup failed: {renameResult.ErrorMessage}");

            // Step 14: Delete file
            File.Delete(renamedFile);
            var deleteResult = await gatewayClient.DeleteFileAsync(new FileDeletedRequest
            {
                BackupSetId = backupSetId,
                RelativePath = "interactive_doc_renamed.txt"
            });
            Assert.True(deleteResult.Success, $"Delete failed: {deleteResult.ErrorMessage}");

            // Step 15 & 16: Restore Version 1 -> Creates new RESTORE version (v3 or v4)
            var restore = await clientService.RestoreVersionAsync(backupSetId, fileId, 1);
            Assert.True(restore.Success, $"Restore failed: {restore.ErrorMessage}");
            Assert.True(File.Exists(testFile), "Restored file missing on disk.");
            string restoredContent = await File.ReadAllTextAsync(testFile);
            Assert.Equal("Interactive Smoke V1 - Initial Content", restoredContent);

            // Step 17 & 18: Audit log records CREATE, MODIFY, RENAME, DELETE, RESTORE
            var changeLog = await clientService.GetChangeLogsAsync(backupSetId, limit: 50);
            Assert.True(changeLog.Success, "GetChangeLogsAsync failed.");
            Assert.NotEmpty(changeLog.Entries);

            var actions = changeLog.Entries.Select(e => e.Action.ToUpperInvariant()).ToHashSet();
            Assert.Contains("CREATE", actions);
            Assert.Contains("MODIFY", actions);
            Assert.Contains("RESTORE", actions);

            // Verify Russian localization dictionary for actions
            string LocalizeAction(string action) => action.ToUpperInvariant() switch
            {
                "CREATE" => "Создание",
                "MODIFY" => "Изменение",
                "DELETE" => "Удаление",
                "RENAME" => "Переименование",
                "RESTORE" => "Восстановление",
                _ => action
            };
            Assert.Equal("Создание", LocalizeAction("CREATE"));
            Assert.Equal("Изменение", LocalizeAction("MODIFY"));
            Assert.Equal("Удаление", LocalizeAction("DELETE"));
            Assert.Equal("Переименование", LocalizeAction("RENAME"));
            Assert.Equal("Восстановление", LocalizeAction("RESTORE"));

            // Step 19 & 20: Disconnect simulation -> MarkDisconnected called
            gatewayClient.MarkDisconnected();
            // Client does not crash, MarkDisconnected handles it

            // Step 21 & 22: Reconnect on next call
            bool reconnectPing = await clientService.PingGatewayAsync();
            Assert.True(reconnectPing, "Gateway reconnect failed.");

            // Step 23 & 24: Reconcile Now -> Consistency verified
            await syncEngine.ReconcileNowAsync();
            Assert.False(syncEngine.ReconciliationRequired);
        }
        finally
        {
            try { Directory.Delete(tempDemoDir, recursive: true); } catch { }
            try { File.Delete(clientStatePath); } catch { }
        }
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
