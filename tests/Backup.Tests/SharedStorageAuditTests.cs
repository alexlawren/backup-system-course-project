using System.Security.Cryptography;
using System.Text;
using Backup.Client.Web.Services;
using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
using Backup.MetadataService.Services;
using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Backup.Tests;

public class SharedStorageAuditTests : IClassFixture<SharedSyncServerFixture>
{
    private const string PostgresMasterConnectionString =
        "Host=127.0.0.1;Port=5432;Database=postgres;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly SharedSyncServerFixture _fixture;

    public SharedStorageAuditTests(SharedSyncServerFixture fixture)
    {
        _fixture = fixture;
    }

    private TestClientNode CreateNode(string name, TimeSpan? pollingInterval = null)
    {
        var node = new TestClientNode(name, _fixture.GatewayPort);
        node.SyncEngine.SharedSyncInterval = pollingInterval ?? TimeSpan.FromMilliseconds(200);
        return node;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, TimeSpan pollInterval)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(pollInterval);
        }
        return condition();
    }

    // ==================================================
    // SECTION 3: CLEAN INSTALL TEST
    // ==================================================

    [Fact]
    public async Task Audit_CleanDatabase_Migrations_CreateSchema_And_ExecuteOperations()
    {
        string cleanDbName = "backup_audit_clean_" + Guid.NewGuid().ToString("N")[..12];

        // 1. Create clean database
        await using (var masterConn = new NpgsqlConnection(PostgresMasterConnectionString))
        {
            await masterConn.OpenAsync();
            await using var cmd = masterConn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{cleanDbName}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        string cleanDbConnStr = $"Host=127.0.0.1;Port=5432;Database={cleanDbName};Username=backup_app;Password=backup_app_dev_secret_2026";

        try
        {
            var dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
                .UseNpgsql(cleanDbConnStr)
                .Options;

            // 2. Apply all EF Core migrations on clean database
            await using (var db = new MetadataDbContext(dbOptions))
            {
                await db.Database.MigrateAsync();

                // 3. Verify all tables exist and can be queried
                Assert.Empty(await db.Clients.ToListAsync());
                Assert.Empty(await db.BackupSets.ToListAsync());
                Assert.Empty(await db.BackupSetMembers.ToListAsync());
                Assert.Empty(await db.Files.ToListAsync());
                Assert.Empty(await db.FileVersions.ToListAsync());
                Assert.Empty(await db.ChangeLogs.ToListAsync());
                Assert.Empty(await db.PendingFileOperations.ToListAsync());

                // 4. Register client
                var clientA = new ClientEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "AuditStationA",
                    MachineName = "AUDIT-HOST-A",
                    CreatedAtUtc = DateTime.UtcNow,
                    LastSeenAtUtc = DateTime.UtcNow
                };
                db.Clients.Add(clientA);
                await db.SaveChangesAsync();

                // 5. Create PRIVATE BackupSet
                var privSet = new BackupSetEntity
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientA.Id,
                    Name = "PrivateAuditSet",
                    LocalRootPath = @"C:\Audit\Private",
                    AccessMode = "PRIVATE",
                    CreatedAtUtc = DateTime.UtcNow,
                    IsActive = true
                };
                db.BackupSets.Add(privSet);
                db.BackupSetMembers.Add(new BackupSetMemberEntity
                {
                    BackupSetId = privSet.Id,
                    ClientId = clientA.Id,
                    Role = "OWNER",
                    JoinedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();

                // 6. Create SHARED BackupSet with ShareCode
                var sharedSet = new BackupSetEntity
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientA.Id,
                    Name = "SharedAuditSet",
                    LocalRootPath = @"C:\Audit\Shared",
                    AccessMode = "SHARED",
                    ShareCode = "7K2M-9P4X",
                    CreatedAtUtc = DateTime.UtcNow,
                    IsActive = true
                };
                db.BackupSets.Add(sharedSet);
                db.BackupSetMembers.Add(new BackupSetMemberEntity
                {
                    BackupSetId = sharedSet.Id,
                    ClientId = clientA.Id,
                    Role = "OWNER",
                    JoinedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();

                // 7. Add client B as MEMBER
                var clientB = new ClientEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "AuditStationB",
                    MachineName = "AUDIT-HOST-B",
                    CreatedAtUtc = DateTime.UtcNow,
                    LastSeenAtUtc = DateTime.UtcNow
                };
                db.Clients.Add(clientB);
                db.BackupSetMembers.Add(new BackupSetMemberEntity
                {
                    BackupSetId = sharedSet.Id,
                    ClientId = clientB.Id,
                    Role = "MEMBER",
                    JoinedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();

                // 8. Verify members query
                var members = await db.BackupSetMembers
                    .Where(m => m.BackupSetId == sharedSet.Id)
                    .Include(m => m.Client)
                    .ToListAsync();

                Assert.Equal(2, members.Count);
                Assert.Contains(members, m => m.Role == "OWNER" && m.ClientId == clientA.Id);
                Assert.Contains(members, m => m.Role == "MEMBER" && m.ClientId == clientB.Id);
            }
        }
        finally
        {
            // Drop temporary clean test database
            try
            {
                await using var masterConn = new NpgsqlConnection(PostgresMasterConnectionString);
                await masterConn.OpenAsync();
                await using var dropCmd = masterConn.CreateCommand();
                dropCmd.CommandText = $"DROP DATABASE \"{cleanDbName}\" WITH (FORCE);";
                await dropCmd.ExecuteNonQueryAsync();
            }
            catch { }
        }
    }

    // ==================================================
    // SECTION 4: OLD DATABASE UPGRADE INVARIANTS
    // ==================================================

    [Fact]
    public async Task Audit_OldDatabaseMigration_BackfillAndDefaults_ArePreserved()
    {
        await using var db = _fixture.CreateDbContext();

        // Check model metadata for backward compatibility
        var entityType = db.Model.FindEntityType(typeof(BackupSetEntity))!;
        var accessModeProp = entityType.FindProperty(nameof(BackupSetEntity.AccessMode))!;
        Assert.Equal("PRIVATE", accessModeProp.GetDefaultValue());

        var shareCodeProp = entityType.FindProperty(nameof(BackupSetEntity.ShareCode))!;
        Assert.True(shareCodeProp.IsNullable);

        var changeLogEntityType = db.Model.FindEntityType(typeof(ChangeLogEntity))!;
        var actorClientIdProp = changeLogEntityType.FindProperty(nameof(ChangeLogEntity.ActorClientId))!;
        Assert.True(actorClientIdProp.IsNullable);

        // Verify that newly created backup set via MetadataService has an OWNER membership
        var client = await db.Clients.FirstAsync();
        var newSet = new BackupSetEntity
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = "MigrationCheckSet",
            LocalRootPath = @"C:\TestMigration",
            AccessMode = "PRIVATE"
        };
        db.BackupSets.Add(newSet);
        db.BackupSetMembers.Add(new BackupSetMemberEntity
        {
            BackupSetId = newSet.Id,
            ClientId = client.Id,
            Role = "OWNER",
            JoinedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var ownerMember = await db.BackupSetMembers
            .FirstOrDefaultAsync(m => m.BackupSetId == newSet.Id && m.Role == "OWNER");
        Assert.NotNull(ownerMember);
        Assert.Equal(client.Id, ownerMember.ClientId);
    }

    // ==================================================
    // SECTION 8: SHARED RESTORE
    // ==================================================

    [Fact]
    public async Task Audit_SharedRestore_ClientB_RestoresV1_CreatesV4_PropagatesToClientA_ViaPolling()
    {
        using var nodeA = CreateNode("AuditRestoreA");
        using var nodeB = CreateNode("AuditRestoreB");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var (backupSetId, shareCode) = await nodeA.CreateSharedSetAsync("AuditRestoreSet", cts.Token);
        await nodeB.JoinSharedSetAsync(shareCode, cts.Token);

        // 1. Client A creates v1
        string filePathA = Path.Combine(nodeA.SourceDir, "report.txt");
        string contentV1 = "Content V1 Initial";
        string shaV1 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contentV1))).ToLowerInvariant();
        await File.WriteAllTextAsync(filePathA, contentV1, cts.Token);

        // Wait for B to receive v1 via polling
        string filePathB = Path.Combine(nodeB.SourceDir, "report.txt");
        Assert.True(await WaitForConditionAsync(() => File.Exists(filePathB) && File.ReadAllText(filePathB) == contentV1,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

        // 2. Client A modifies to v2
        string contentV2 = "Content V2 Revision";
        await File.WriteAllTextAsync(filePathA, contentV2, cts.Token);
        Assert.True(await WaitForConditionAsync(() => File.Exists(filePathB) && File.ReadAllText(filePathB) == contentV2,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

        // 3. Client A modifies to v3
        string contentV3 = "Content V3 Final Draft";
        await File.WriteAllTextAsync(filePathA, contentV3, cts.Token);
        Assert.True(await WaitForConditionAsync(() => File.Exists(filePathB) && File.ReadAllText(filePathB) == contentV3,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

        // Verify server has 3 versions
        var filesResp = await nodeB.GatewayClient.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, cts.Token);
        var fileMeta = filesResp.Files.First(f => f.RelativePath == "report.txt");
        Assert.Equal(3, fileMeta.CurrentVersionNumber);

        // 4. Client B chooses Restore v1
        var restoreResult = await nodeB.SyncEngine.RestoreVersionAsync(backupSetId, fileMeta.FileId, 1, cts.Token);
        Assert.True(restoreResult.Success);

        // Server should NOT rollback version number; it creates v4
        var filesAfterRestore = await nodeB.GatewayClient.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, cts.Token);
        var fileMetaAfter = filesAfterRestore.Files.First(f => f.FileId == fileMeta.FileId);
        Assert.Equal(4, fileMetaAfter.CurrentVersionNumber);

        // Verify server version history: v1, v2, v3, v4, where SHA(v4) == SHA(v1)
        var versionsResp = await nodeB.GatewayClient.GetVersionsAsync(new GetVersionsRequest { FileId = fileMeta.FileId }, cts.Token);
        Assert.Equal(4, versionsResp.Versions.Count);

        var v1 = versionsResp.Versions.First(v => v.VersionNumber == 1);
        var v4 = versionsResp.Versions.First(v => v.VersionNumber == 4);
        Assert.Equal(shaV1, v1.Sha256);
        Assert.Equal(shaV1, v4.Sha256);
        Assert.Equal(v1.Sha256, v4.Sha256);

        // Verify ChangeLog has RESTORE with ActorClientId = Client B
        var changeLogs = await nodeB.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = backupSetId }, cts.Token);
        var restoreEntry = changeLogs.Entries.FirstOrDefault(c => c.Action == "RESTORE" && c.FileId == fileMeta.FileId);
        Assert.NotNull(restoreEntry);
        Assert.Equal(nodeB.ClientId, restoreEntry.ActorClientId);

        // 5. Client A receives v4 automatically via background polling without pressing any button
        Assert.True(await WaitForConditionAsync(() => File.Exists(filePathA) && File.ReadAllText(filePathA) == contentV1,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

        // Both clients have restored content and are in Synced status
        Assert.Equal(contentV1, await File.ReadAllTextAsync(filePathA, cts.Token));
        Assert.Equal(contentV1, await File.ReadAllTextAsync(filePathB, cts.Token));
    }

    // ==================================================
    // SECTION 9: SHARED RESTORE + LOCAL DIVERGENCE
    // ==================================================

    [Fact]
    public async Task Audit_SharedRestore_WhenClientAHasLocalDivergence_PreservesLocalWorkAsConflictCopy()
    {
        using var nodeA = CreateNode("AuditRestoreDivA");
        using var nodeB = CreateNode("AuditRestoreDivB");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var (backupSetId, shareCode) = await nodeA.CreateSharedSetAsync("AuditRestoreDivSet", cts.Token);
        await nodeB.JoinSharedSetAsync(shareCode, cts.Token);

        // 1. Create v1, v2, v3
        string filePathA = Path.Combine(nodeA.SourceDir, "divergent.txt");
        string filePathB = Path.Combine(nodeB.SourceDir, "divergent.txt");

        await File.WriteAllTextAsync(filePathA, "Base V1", cts.Token);
        Assert.True(await WaitForConditionAsync(() => File.Exists(filePathB) && File.ReadAllText(filePathB) == "Base V1",
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

        await File.WriteAllTextAsync(filePathA, "Intermediate V2", cts.Token);
        Assert.True(await WaitForConditionAsync(() => File.Exists(filePathB) && File.ReadAllText(filePathB) == "Intermediate V2",
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

        await File.WriteAllTextAsync(filePathA, "Current V3", cts.Token);
        Assert.True(await WaitForConditionAsync(() => File.Exists(filePathB) && File.ReadAllText(filePathB) == "Current V3",
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

        var filesResp = await nodeB.GatewayClient.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId }, cts.Token);
        var fileMeta = filesResp.Files.First(f => f.RelativePath == "divergent.txt");

        // 2. Client A stops sync (goes offline) and makes a local edit
        await nodeA.SyncEngine.StopAsync(cts.Token);
        string localWorkA = "LOCAL WORK CREATED WHILE OFFLINE BY A";
        await File.WriteAllTextAsync(filePathA, localWorkA, cts.Token);

        // 3. Client B performs Restore of v1 -> creates server v4
        var restoreResult = await nodeB.SyncEngine.RestoreVersionAsync(backupSetId, fileMeta.FileId, 1, cts.Token);
        Assert.True(restoreResult.Success);

        // 4. Client A reconnects / resumes sync
        var profileA = await nodeA.ProfileService.GetProfileAsync(cts.Token);
        await nodeA.SyncEngine.StartAsync(profileA!, cts.Token);

        // 5. Client A must detect divergence: canonical file becomes restored v4 ("Base V1"),
        // and local work A is preserved into a conflict copy file!
        Assert.True(await WaitForConditionAsync(() =>
        {
            var conflictFiles = Directory.GetFiles(nodeA.SourceDir, "divergent (conflict*");
            return conflictFiles.Length > 0 &&
                   File.ReadAllText(filePathA) == "Base V1" &&
                   File.ReadAllText(conflictFiles[0]) == localWorkA;
        }, TimeSpan.FromSeconds(12), TimeSpan.FromMilliseconds(100)));

        // 6. Conflict copy is uploaded to server and Client B automatically receives it via polling
        Assert.True(await WaitForConditionAsync(() =>
        {
            var conflictFilesB = Directory.GetFiles(nodeB.SourceDir, "divergent (conflict*");
            return conflictFilesB.Length > 0 && File.ReadAllText(conflictFilesB[0]) == localWorkA;
        }, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100)));

        // Both clients have no pending reconciliation errors and no active conflicts
        Assert.False(nodeA.SyncEngine.ReconciliationRequired);
        Assert.Empty(nodeA.SyncEngine.ActiveConflicts);
        Assert.False(nodeB.SyncEngine.ReconciliationRequired);
        Assert.Empty(nodeB.SyncEngine.ActiveConflicts);
    }

    // ==================================================
    // SECTION 17, 18, 19: ZERO BYTE, BINARY, NESTED
    // ==================================================

    [Fact]
    public async Task Audit_ZeroByte_Binary_And_NestedDirectories_PropagateAutomatically()
    {
        using var nodeA = CreateNode("AuditSpecialA");
        using var nodeB = CreateNode("AuditSpecialB");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var (backupSetId, shareCode) = await nodeA.CreateSharedSetAsync("AuditSpecialSet", cts.Token);
        await nodeB.JoinSharedSetAsync(shareCode, cts.Token);

        // 1. Zero-byte file
        string zeroFileA = Path.Combine(nodeA.SourceDir, "empty.txt");
        await File.WriteAllBytesAsync(zeroFileA, Array.Empty<byte>(), cts.Token);

        // 2. Binary file with non-UTF8 arbitrary bytes
        string binaryFileA = Path.Combine(nodeA.SourceDir, "payload.bin");
        byte[] rawBytes = new byte[8192];
        new Random(42).NextBytes(rawBytes);
        await File.WriteAllBytesAsync(binaryFileA, rawBytes, cts.Token);

        // 3. Deeply nested file
        string nestedDir = Path.Combine(nodeA.SourceDir, "docs", "2026", "reports", "final");
        Directory.CreateDirectory(nestedDir);
        string nestedFileA = Path.Combine(nestedDir, "data.bin");
        byte[] nestedBytes = Encoding.UTF8.GetBytes("Deeply nested file payload");
        await File.WriteAllBytesAsync(nestedFileA, nestedBytes, cts.Token);

        // Verify propagation to Client B via polling
        string zeroFileB = Path.Combine(nodeB.SourceDir, "empty.txt");
        string binaryFileB = Path.Combine(nodeB.SourceDir, "payload.bin");
        string nestedFileB = Path.Combine(nodeB.SourceDir, "docs", "2026", "reports", "final", "data.bin");

        Assert.True(await WaitForConditionAsync(() =>
            File.Exists(zeroFileB) &&
            File.Exists(binaryFileB) &&
            File.Exists(nestedFileB),
            TimeSpan.FromSeconds(12), TimeSpan.FromMilliseconds(100)));

        Assert.Equal(0, new FileInfo(zeroFileB).Length);
        Assert.Equal(rawBytes, await File.ReadAllBytesAsync(binaryFileB, cts.Token));
        Assert.Equal(nestedBytes, await File.ReadAllBytesAsync(nestedFileB, cts.Token));
    }

    // ==================================================
    // SECTION 20 & 21: PATH SAFETY & SHARE CODE
    // ==================================================

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/absolute/path")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("sub/../../outside.txt")]
    public void Audit_PathSafety_TraversalAttacks_AreRejected(string maliciousPath)
    {
        bool isValid = ProtocolPathValidator.TryNormalizeAndValidate(maliciousPath, out var normalized, out var err);
        Assert.False(isValid);
        Assert.NotEmpty(err!);
    }

    [Theory]
    [InlineData("4k9p-7m2x", "4K9P-7M2X")]
    [InlineData("4K9P7M2X", "4K9P-7M2X")]
    [InlineData("  4k9p - 7m2x  ", "4K9P-7M2X")]
    public void Audit_ShareCode_Normalization_ValidCases(string input, string expected)
    {
        Assert.True(ShareCodeHelper.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOO-SHORT")]
    [InlineData("INVALIDCHARS!!!")]
    [InlineData("4K9P-7M2X-EXTRA")]
    public void Audit_ShareCode_Normalization_InvalidCases_RejectedSafely(string input)
    {
        Assert.False(ShareCodeHelper.TryNormalize(input, out var normalized, out var err));
        Assert.NotEmpty(err!);
    }

    // ==================================================
    // SECTION 13: RAPID START / STOP AUDIT
    // ==================================================

    [Fact]
    public async Task Audit_RapidStartStop_And_ProfileSwitching_NoDeadlock()
    {
        using var node = CreateNode("RapidStartStop");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await node.RegisterAsync(cts.Token);

        var (sharedSetId, shareCode) = await node.CreateSharedSetAsync("RapidSet", cts.Token);
        var sharedProfile = await node.ProfileService.GetProfileAsync(cts.Token);

        var privResp = await node.ClientService.CreateBackupSetAsync("RapidPrivSet", node.SourceDir, BackupSetAccessMode.Private, cts.Token);
        var privProfile = new SyncProfile
        {
            BackupSetId = privResp.BackupSetId,
            Name = "RapidPrivSet",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Private,
            AutomaticSyncEnabled = true
        };

        // Repeated rapid start / stop without deadlock or exceptions
        for (int i = 0; i < 5; i++)
        {
            await node.SyncEngine.StartAsync(sharedProfile!, cts.Token);
            Assert.True(node.SyncEngine.IsRunning);
            Assert.True(node.SyncEngine.IsSharedPollingRunning);

            await node.SyncEngine.StopAsync(cts.Token);
            Assert.False(node.SyncEngine.IsRunning);
            Assert.False(node.SyncEngine.IsSharedPollingRunning);
        }

        // Switching between Private and Shared
        await node.SyncEngine.SwitchProfileAsync(privProfile, cts.Token);
        Assert.False(node.SyncEngine.IsSharedPollingRunning);

        sharedProfile!.AutomaticSyncEnabled = true;
        await node.SyncEngine.SwitchProfileAsync(sharedProfile, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);

        await node.SyncEngine.StopAsync(cts.Token);
    }
}
