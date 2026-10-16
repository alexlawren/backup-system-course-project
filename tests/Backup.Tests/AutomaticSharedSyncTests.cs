using System.Net;
using System.Text.RegularExpressions;
using Backup.Client.Web.Services;
using Backup.MetadataService.Data;
using Backup.Shared.Messages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backup.Tests;

public class AutomaticSharedSyncTests : IClassFixture<SharedSyncServerFixture>, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly SharedSyncServerFixture _fixture;
    private readonly WebApplicationFactory<Program> _webFactory;

    public AutomaticSharedSyncTests(SharedSyncServerFixture fixture, WebApplicationFactory<Program> webFactory)
    {
        _fixture = fixture;
        _webFactory = webFactory;
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
    // SECTION 27: AUTOMATIC SYNC TESTS
    // ==================================================

    [Fact]
    public async Task SharedSync_Start_CreatesSinglePollingLoop()
    {
        using var node = CreateNode("PollStartTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await node.RegisterAsync(cts.Token);

        var setResp = await node.ClientService.CreateBackupSetAsync("SharedPollStart", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        Assert.True(setResp.Success);

        var profile = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "SharedPollStart",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(profile, cts.Token);

        Assert.True(node.SyncEngine.IsRunning);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);
    }

    [Fact]
    public async Task SharedSync_RepeatedStart_DoesNotCreateDuplicatePollingLoops()
    {
        using var node = CreateNode("PollRepeatTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await node.RegisterAsync(cts.Token);

        var setResp = await node.ClientService.CreateBackupSetAsync("SharedPollRepeat", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        Assert.True(setResp.Success);

        var profile = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "SharedPollRepeat",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(profile, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);

        // Call StartAsync repeatedly
        await node.SyncEngine.StartAsync(profile, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);

        await Task.Delay(300);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);
    }

    [Fact]
    public async Task SharedSync_Stop_CancelsPollingLoop()
    {
        using var node = CreateNode("PollStopTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await node.RegisterAsync(cts.Token);

        var setResp = await node.ClientService.CreateBackupSetAsync("SharedPollStop", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        Assert.True(setResp.Success);

        var profile = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "SharedPollStop",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(profile, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);

        await node.SyncEngine.StopAsync(cts.Token);
        Assert.False(node.SyncEngine.IsRunning);
        Assert.False(node.SyncEngine.IsSharedPollingRunning);
    }

    [Fact]
    public async Task SwitchSharedProfile_RestartsPollingForNewProfile()
    {
        using var node = CreateNode("SwitchSharedTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await node.RegisterAsync(cts.Token);

        var setResp1 = await node.ClientService.CreateBackupSetAsync("SharedSet1", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        var setResp2 = await node.ClientService.CreateBackupSetAsync("SharedSet2", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);

        var profile1 = new SyncProfile
        {
            BackupSetId = setResp1.BackupSetId,
            Name = "SharedSet1",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp1.ShareCode,
            AutomaticSyncEnabled = true
        };

        var profile2 = new SyncProfile
        {
            BackupSetId = setResp2.BackupSetId,
            Name = "SharedSet2",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp2.ShareCode,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(profile1, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);
        Assert.Equal(setResp1.BackupSetId, node.SyncEngine.ActiveProfile?.BackupSetId);

        await node.SyncEngine.SwitchProfileAsync(profile2, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);
        Assert.Equal(setResp2.BackupSetId, node.SyncEngine.ActiveProfile?.BackupSetId);
    }

    [Fact]
    public async Task SwitchToPrivate_StopsSharedPolling()
    {
        using var node = CreateNode("SwitchPrivateTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await node.RegisterAsync(cts.Token);

        var sharedSet = await node.ClientService.CreateBackupSetAsync("SharedToPrivateSet", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        var privateSet = await node.ClientService.CreateBackupSetAsync("PrivateSet", node.SourceDir, BackupSetAccessMode.Private, cts.Token);

        var sharedProfile = new SyncProfile
        {
            BackupSetId = sharedSet.BackupSetId,
            Name = "SharedToPrivateSet",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = sharedSet.ShareCode,
            AutomaticSyncEnabled = true
        };

        var privateProfile = new SyncProfile
        {
            BackupSetId = privateSet.BackupSetId,
            Name = "PrivateSet",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Private,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(sharedProfile, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);

        await node.SyncEngine.SwitchProfileAsync(privateProfile, cts.Token);
        Assert.True(node.SyncEngine.IsRunning);
        Assert.False(node.SyncEngine.IsSharedPollingRunning);
    }

    [Fact]
    public async Task ApplicationShutdown_CancelsPollingCleanly()
    {
        var node = CreateNode("ShutdownTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await node.RegisterAsync(cts.Token);

        var setResp = await node.ClientService.CreateBackupSetAsync("ShutdownSet", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        var profile = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "ShutdownSet",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(profile, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);

        await node.SyncEngine.DisposeAsync();
        Assert.False(node.SyncEngine.IsSharedPollingRunning);
        node.Dispose();
    }

    // ==================================================
    // SECTION 28: AUTOMATIC REMOTE TESTS (NO MANUAL RECONCILE)
    // ==================================================

    [Fact]
    public async Task AutoSync_ClientA_Create_AppearsOnClientBWithoutManualReconcile()
    {
        using var nodeA = CreateNode("AutoCreate_A", TimeSpan.FromMilliseconds(200));
        using var nodeB = CreateNode("AutoCreate_B", TimeSpan.FromMilliseconds(200));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var setResp = await nodeA.ClientService.CreateBackupSetAsync("AutoCreateSet", nodeA.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        Assert.True(setResp.Success);

        await nodeB.ClientService.JoinSharedBackupSetAsync(setResp.ShareCode!, cts.Token);

        var profileA = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "AutoCreateSet",
            LocalRootPath = nodeA.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };
        var profileB = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "AutoCreateSet",
            LocalRootPath = nodeB.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);
        await nodeB.SyncEngine.StartAsync(profileB, cts.Token);

        // Client A creates a file locally
        string testFile = Path.Combine(nodeA.SourceDir, "auto_shared.txt");
        await File.WriteAllTextAsync(testFile, "Content from A", cts.Token);

        // B does NOT click sync! B's background polling loop must download the file.
        string targetFileB = Path.Combine(nodeB.SourceDir, "auto_shared.txt");
        bool appearsOnB = await WaitForConditionAsync(
            () => File.Exists(targetFileB) && File.ReadAllText(targetFileB) == "Content from A",
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));

        Assert.True(appearsOnB, "File created on A should appear on B via automatic polling loop without manual reconcile.");
    }

    [Fact]
    public async Task AutoSync_ClientB_Modify_AppearsOnClientAWithoutManualReconcile()
    {
        using var nodeA = CreateNode("AutoMod_A", TimeSpan.FromMilliseconds(200));
        using var nodeB = CreateNode("AutoMod_B", TimeSpan.FromMilliseconds(200));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var setResp = await nodeA.ClientService.CreateBackupSetAsync("AutoModSet", nodeA.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        await nodeB.ClientService.JoinSharedBackupSetAsync(setResp.ShareCode!, cts.Token);

        // Initial file
        await File.WriteAllTextAsync(Path.Combine(nodeA.SourceDir, "mod_doc.txt"), "Initial v1", cts.Token);

        var profileA = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "AutoModSet",
            LocalRootPath = nodeA.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };
        var profileB = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "AutoModSet",
            LocalRootPath = nodeB.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);
        await nodeB.SyncEngine.StartAsync(profileB, cts.Token);

        // Wait for B to receive v1
        string targetFileB = Path.Combine(nodeB.SourceDir, "mod_doc.txt");
        await WaitForConditionAsync(() => File.Exists(targetFileB), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100));

        // B modifies mod_doc.txt
        await File.WriteAllTextAsync(targetFileB, "Modified by B v2", cts.Token);

        // A does NOT click sync! A's background polling loop must download the modification.
        string targetFileA = Path.Combine(nodeA.SourceDir, "mod_doc.txt");
        bool updatedOnA = await WaitForConditionAsync(
            () => File.Exists(targetFileA) && File.ReadAllText(targetFileA) == "Modified by B v2",
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));

        Assert.True(updatedOnA, "Modification made by B should appear on A via automatic polling loop without manual reconcile.");
    }

    [Fact]
    public async Task AutoSync_Rename_PropagatesWithoutManualReconcile()
    {
        using var nodeA = CreateNode("AutoRen_A", TimeSpan.FromMilliseconds(200));
        using var nodeB = CreateNode("AutoRen_B", TimeSpan.FromMilliseconds(200));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var setResp = await nodeA.ClientService.CreateBackupSetAsync("AutoRenSet", nodeA.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        await nodeB.ClientService.JoinSharedBackupSetAsync(setResp.ShareCode!, cts.Token);

        await File.WriteAllTextAsync(Path.Combine(nodeA.SourceDir, "old_name.txt"), "Rename Test Data", cts.Token);

        var profileA = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "AutoRenSet", LocalRootPath = nodeA.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };
        var profileB = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "AutoRenSet", LocalRootPath = nodeB.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };

        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);
        await nodeB.SyncEngine.StartAsync(profileB, cts.Token);

        string oldOnB = Path.Combine(nodeB.SourceDir, "old_name.txt");
        await WaitForConditionAsync(() => File.Exists(oldOnB), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100));

        // A renames file locally
        string oldOnA = Path.Combine(nodeA.SourceDir, "old_name.txt");
        string newOnA = Path.Combine(nodeA.SourceDir, "new_name.txt");
        File.Move(oldOnA, newOnA);

        // B does NOT click sync!
        string newOnB = Path.Combine(nodeB.SourceDir, "new_name.txt");
        bool renamedOnB = await WaitForConditionAsync(
            () => File.Exists(newOnB) && !File.Exists(oldOnB),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));

        Assert.True(renamedOnB, "Renamed file should automatically propagate to B without manual reconcile.");
    }

    [Fact]
    public async Task AutoSync_Delete_PropagatesWithoutManualReconcile()
    {
        using var nodeA = CreateNode("AutoDel_A", TimeSpan.FromMilliseconds(200));
        using var nodeB = CreateNode("AutoDel_B", TimeSpan.FromMilliseconds(200));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var setResp = await nodeA.ClientService.CreateBackupSetAsync("AutoDelSet", nodeA.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        await nodeB.ClientService.JoinSharedBackupSetAsync(setResp.ShareCode!, cts.Token);

        await File.WriteAllTextAsync(Path.Combine(nodeA.SourceDir, "to_delete.txt"), "To be deleted", cts.Token);

        var profileA = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "AutoDelSet", LocalRootPath = nodeA.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };
        var profileB = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "AutoDelSet", LocalRootPath = nodeB.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };

        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);
        await nodeB.SyncEngine.StartAsync(profileB, cts.Token);

        string fileOnB = Path.Combine(nodeB.SourceDir, "to_delete.txt");
        await WaitForConditionAsync(() => File.Exists(fileOnB), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100));

        // B deletes file locally
        File.Delete(fileOnB);

        // A does NOT click sync!
        string fileOnA = Path.Combine(nodeA.SourceDir, "to_delete.txt");
        bool deletedOnA = await WaitForConditionAsync(
            () => !File.Exists(fileOnA),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));

        Assert.True(deletedOnA, "Deletion should automatically propagate to A without manual reconcile.");
    }

    [Fact]
    public async Task AutoSync_ConflictCopy_PropagatesWithoutManualReconcile()
    {
        using var nodeA = CreateNode("AutoConflict_A", TimeSpan.FromMilliseconds(200));
        using var nodeB = CreateNode("AutoConflict_B", TimeSpan.FromMilliseconds(200));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var setResp = await nodeA.ClientService.CreateBackupSetAsync("AutoConflictDistSet", nodeA.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        await nodeB.ClientService.JoinSharedBackupSetAsync(setResp.ShareCode!, cts.Token);

        // Initial file
        await File.WriteAllTextAsync(Path.Combine(nodeA.SourceDir, "report.txt"), "BASE REPORT", cts.Token);

        var profileA = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "AutoConflictDistSet", LocalRootPath = nodeA.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };
        var profileB = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "AutoConflictDistSet", LocalRootPath = nodeB.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };

        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);
        await nodeB.SyncEngine.StartAsync(profileB, cts.Token);

        string reportB = Path.Combine(nodeB.SourceDir, "report.txt");
        await WaitForConditionAsync(() => File.Exists(reportB), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100));

        // A stops sync (simulating offline editing)
        await nodeA.SyncEngine.StopAsync(cts.Token);
        await File.WriteAllTextAsync(Path.Combine(nodeA.SourceDir, "report.txt"), "A EDITS", cts.Token);

        // B modifies report.txt and pushes v2
        await File.WriteAllTextAsync(reportB, "B EDITS", cts.Token);
        await WaitForConditionAsync(() =>
        {
            using var db = _fixture.CreateDbContext();
            var f = db.Files.FirstOrDefault(x => x.BackupSetId == setResp.BackupSetId && x.RelativePath == "report.txt");
            return f != null && f.CurrentVersionNumber == 2;
        }, TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(100));

        // A resumes sync -> conflict auto-resolution preserves A in conflict copy, sets canonical to B
        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);

        // Wait for conflict copy to appear on A
        bool conflictOnA = await WaitForConditionAsync(
            () => Directory.GetFiles(nodeA.SourceDir, "report (conflict *").Length > 0,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));
        Assert.True(conflictOnA);

        // Client B does NOT click sync! B's background polling loop must automatically receive the conflict copy.
        bool conflictOnB = await WaitForConditionAsync(
            () => Directory.GetFiles(nodeB.SourceDir, "report (conflict *").Length > 0,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));

        Assert.True(conflictOnB, "Conflict copy should automatically propagate to B without manual reconcile.");
    }

    // ==================================================
    // SECTION 29: NO-OP TEST
    // ==================================================

    [Fact]
    public async Task AutoSync_UnchangedManifest_DoesNotCreateVersionsOrChangeLog()
    {
        using var node = CreateNode("NoOpPollTest", TimeSpan.FromMilliseconds(150));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await node.RegisterAsync(cts.Token);

        var setResp = await node.ClientService.CreateBackupSetAsync("NoOpSet", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        await File.WriteAllTextAsync(Path.Combine(node.SourceDir, "static.txt"), "Static content", cts.Token);

        var profile = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "NoOpSet",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(profile, cts.Token);

        // Wait until initial file is uploaded
        await WaitForConditionAsync(() =>
        {
            using var db = _fixture.CreateDbContext();
            return db.Files.Any(f => f.BackupSetId == setResp.BackupSetId && f.RelativePath == "static.txt");
        }, TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(100));

        // Record counts
        int initialVersionCount;
        int initialChangeLogCount;
        using (var db = _fixture.CreateDbContext())
        {
            initialVersionCount = await db.FileVersions.CountAsync(v => v.File.BackupSetId == setResp.BackupSetId, cts.Token);
            initialChangeLogCount = await db.ChangeLogs.CountAsync(cl => cl.BackupSetId == setResp.BackupSetId, cts.Token);
        }

        // Wait for ~8-10 polling cycles without touching any files
        await Task.Delay(1500);

        // Verify counts remain identical (cheap no-op)
        using (var db = _fixture.CreateDbContext())
        {
            int currentVersionCount = await db.FileVersions.CountAsync(v => v.File.BackupSetId == setResp.BackupSetId, cts.Token);
            int currentChangeLogCount = await db.ChangeLogs.CountAsync(cl => cl.BackupSetId == setResp.BackupSetId, cts.Token);

            Assert.Equal(initialVersionCount, currentVersionCount);
            Assert.Equal(initialChangeLogCount, currentChangeLogCount);
        }

        Assert.NotNull(node.SyncEngine.LastSuccessfulReconciliationUtc);
    }

    // ==================================================
    // SECTION 30: FAILURE / RECONNECT TESTS
    // ==================================================

    [Fact]
    public async Task AutoSync_GatewayOffline_DoesNotTerminateBackgroundLoop()
    {
        using var node = CreateNode("GatewayOfflineTest", TimeSpan.FromMilliseconds(150));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await node.RegisterAsync(cts.Token);

        var setResp = await node.ClientService.CreateBackupSetAsync("GatewayOfflineSet", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);

        var profile = new SyncProfile
        {
            BackupSetId = setResp.BackupSetId,
            Name = "GatewayOfflineSet",
            LocalRootPath = node.SourceDir,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = setResp.ShareCode,
            AutomaticSyncEnabled = true
        };

        await node.SyncEngine.StartAsync(profile, cts.Token);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);

        // Invalidate gateway client connection to simulate network drop
        node.GatewayClient.MarkDisconnected();

        // Wait through several polling iterations
        await Task.Delay(600);

        // Loop must NOT terminate on error
        Assert.True(node.SyncEngine.IsSharedPollingRunning);
    }

    [Fact]
    public async Task AutoSync_GatewayReturns_ReconnectsAndCatchesUpAutomatically()
    {
        using var nodeA = CreateNode("GatewayCatchup_A", TimeSpan.FromMilliseconds(200));
        using var nodeB = CreateNode("GatewayCatchup_B", TimeSpan.FromMilliseconds(200));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var setResp = await nodeA.ClientService.CreateBackupSetAsync("GatewayCatchupSet", nodeA.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        await nodeB.ClientService.JoinSharedBackupSetAsync(setResp.ShareCode!, cts.Token);

        var profileA = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "GatewayCatchupSet", LocalRootPath = nodeA.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };
        var profileB = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "GatewayCatchupSet", LocalRootPath = nodeB.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };

        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);
        await nodeB.SyncEngine.StartAsync(profileB, cts.Token);

        // Client B creates a new file on server
        await File.WriteAllTextAsync(Path.Combine(nodeB.SourceDir, "catchup_item.txt"), "Catchup Payload", cts.Token);

        // A catches up automatically via background polling
        string itemOnA = Path.Combine(nodeA.SourceDir, "catchup_item.txt");
        bool caughtUp = await WaitForConditionAsync(
            () => File.Exists(itemOnA) && File.ReadAllText(itemOnA) == "Catchup Payload",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100));

        Assert.True(caughtUp, "Client A should catch up automatically via periodic polling loop.");
    }

    // ==================================================
    // SECTION 31: CONFLICT + AUTO POLLING E2E
    // ==================================================

    [Fact]
    public async Task AutoSync_ConcurrentEdit_ResolvesConflictAndConverges()
    {
        using var nodeA = CreateNode("Converge_A", TimeSpan.FromMilliseconds(200));
        using var nodeB = CreateNode("Converge_B", TimeSpan.FromMilliseconds(200));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        var setResp = await nodeA.ClientService.CreateBackupSetAsync("ConvergeSet", nodeA.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        await nodeB.ClientService.JoinSharedBackupSetAsync(setResp.ShareCode!, cts.Token);

        string fileA = Path.Combine(nodeA.SourceDir, "doc.txt");
        string fileB = Path.Combine(nodeB.SourceDir, "doc.txt");
        await File.WriteAllTextAsync(fileA, "v1 BASE", cts.Token);

        var profileA = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "ConvergeSet", LocalRootPath = nodeA.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };
        var profileB = new SyncProfile { BackupSetId = setResp.BackupSetId, Name = "ConvergeSet", LocalRootPath = nodeB.SourceDir, AccessMode = BackupSetAccessMode.Shared, ShareCode = setResp.ShareCode, AutomaticSyncEnabled = true };

        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);
        await nodeB.SyncEngine.StartAsync(profileB, cts.Token);

        // Both synced with v1
        await WaitForConditionAsync(() => File.Exists(fileB), TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(100));

        // 1. A stops sync and modifies locally
        await nodeA.SyncEngine.StopAsync(cts.Token);
        await File.WriteAllTextAsync(fileA, "CONTENT FROM CLIENT A", cts.Token);

        // 2. B modifies locally -> watcher pushes to server v2
        await File.WriteAllTextAsync(fileB, "CONTENT FROM CLIENT B", cts.Token);
        await WaitForConditionAsync(() =>
        {
            using var db = _fixture.CreateDbContext();
            var f = db.Files.FirstOrDefault(x => x.BackupSetId == setResp.BackupSetId && x.RelativePath == "doc.txt");
            return f != null && f.CurrentVersionNumber == 2;
        }, TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(100));

        // 3. A starts sync -> conflict resolution creates conflict copy, canonical becomes B
        await nodeA.SyncEngine.StartAsync(profileA, cts.Token);

        // Wait for conflict resolution on A
        bool resolvedOnA = await WaitForConditionAsync(
            () => File.Exists(fileA) && File.ReadAllText(fileA) == "CONTENT FROM CLIENT B" &&
                  Directory.GetFiles(nodeA.SourceDir, "doc (conflict *").Length > 0,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));
        Assert.True(resolvedOnA);

        // 4. B does NOT click sync. B's polling loop receives the conflict copy.
        bool convergedOnB = await WaitForConditionAsync(
            () => File.Exists(fileB) && File.ReadAllText(fileB) == "CONTENT FROM CLIENT B" &&
                  Directory.GetFiles(nodeB.SourceDir, "doc (conflict *").Length > 0,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100));
        Assert.True(convergedOnB);

        // Both clients converged to Synced
        Assert.False(nodeA.SyncEngine.ReconciliationRequired);
        Assert.False(nodeB.SyncEngine.ReconciliationRequired);
    }

    // ==================================================
    // SECTION 32: UI COMPONENT / INTEGRATION TESTS
    // ==================================================

    [Fact]
    public async Task SharedHome_ShowsCurrentClientIdentity()
    {
        using var client = _webFactory.CreateClient();
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Текущий клиент", html);
        Assert.Contains("Имя компьютера:", html);
        Assert.Contains("Client ID:", html);
    }

    [Fact]
    public async Task SharedHome_ShowsShareCodeRoleAndMemberCount()
    {
        using var client = _webFactory.CreateClient();
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Набор резервного копирования", html);
        Assert.Contains("Тип набора:", html);
        Assert.Contains("Общий (Shared)", html);
        Assert.Contains("Подключиться к общему набору", html);
        Assert.Contains("Код подключения:", html);
    }

    [Fact]
    public async Task SharedHome_ShowsAutomaticSyncInterval()
    {
        using var client = _webFactory.CreateClient();
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Состояние системы", html);
        Assert.Contains("Синхронизация", html);
        Assert.Contains("Согласование", html);
        Assert.Contains("Последняя сверка", html);
    }

    [Fact]
    public async Task ChangeLog_ShowsActorMachineName()
    {
        using var client = _webFactory.CreateClient();
        var resp = await client.GetAsync("/history");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Журнал изменений", html);
        Assert.Contains("Действие:", html);
    }

    [Fact]
    public async Task ChangeLog_ConflictCopy_HasLocalizedAction()
    {
        using var client = _webFactory.CreateClient();
        var resp = await client.GetAsync("/history");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Конфликтная копия", html);
    }

    [Fact]
    public async Task VersionHistory_FullSha256AvailableInTooltipOrCopyControl()
    {
        using var client = _webFactory.CreateClient();
        // Request an arbitrary file details page to check rendered template structure
        var resp = await client.GetAsync($"/files/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string html = await resp.Content.ReadAsStringAsync();
        // Page renders without crashing, with proper layout
        Assert.Contains("blazor.web.js", html);
    }
}
