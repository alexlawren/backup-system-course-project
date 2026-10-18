using Backup.Client.Web.Services;
using Backup.Shared.Messages;
using Xunit;

namespace Backup.Tests;

public class SharedRenameRegressionTests : IClassFixture<SharedSyncServerFixture>
{
    private readonly SharedSyncServerFixture _fixture;

    public SharedRenameRegressionTests(SharedSyncServerFixture fixture)
    {
        _fixture = fixture;
    }

    private TestClientNode CreateNode(string name, TimeSpan? pollingInterval = null)
    {
        var node = new TestClientNode(name, _fixture.GatewayPort);
        node.SyncEngine.SharedSyncInterval = pollingInterval ?? TimeSpan.FromMilliseconds(400);
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

    private async Task<(TestClientNode NodeA, TestClientNode NodeB, Guid SharedSetId, Guid OriginalFileId, int OriginalVersion, string FileA, string ReportA, string FileB, string ReportB)> SetupSharedRenameScenarioAsync(CancellationToken token, TimeSpan? pollingInterval = null)
    {
        var nodeA = CreateNode("RenameScenario_A_" + Guid.NewGuid().ToString("N")[..6], pollingInterval);
        var nodeB = CreateNode("RenameScenario_B_" + Guid.NewGuid().ToString("N")[..6], pollingInterval);

        await nodeA.RegisterAsync(token);
        await nodeB.RegisterAsync(token);

        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("RenameScenarioSet", token);
        await nodeB.JoinSharedSetAsync(shareCode, token);

        // A creates shared.txt -> v1
        string fileA = Path.Combine(nodeA.SourceDir, "shared.txt");
        await File.WriteAllTextAsync(fileA, "v1", token);

        string fileB = Path.Combine(nodeB.SourceDir, "shared.txt");
        bool bGotV1 = await WaitForConditionAsync(() => File.Exists(fileB), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(150));
        Assert.True(bGotV1, "Client B should receive v1 of shared.txt");

        // B modifies shared.txt -> v2
        await File.WriteAllTextAsync(fileB, "v2", token);
        bool aGotV2 = await WaitForConditionAsync(() => File.Exists(fileA) && File.ReadAllText(fileA) == "v2", TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(150));
        Assert.True(aGotV2, "Client A should receive v2 of shared.txt");

        // Capture initial manifest state
        var manifestBefore = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, token);
        var entryBefore = manifestBefore.Entries.First(e => e.RelativePath == "shared.txt");

        string reportA = Path.Combine(nodeA.SourceDir, "report.txt");
        string reportB = Path.Combine(nodeB.SourceDir, "report.txt");

        return (nodeA, nodeB, sharedSetId, entryBefore.FileId, entryBefore.CurrentVersionNumber, fileA, reportA, fileB, reportB);
    }

    [Fact]
    public async Task LocalRename_PreservesFileId()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var (nodeA, nodeB, sharedSetId, originalFileId, _, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token);

        using (nodeA)
        using (nodeB)
        {
            // Execute rename on Client A
            File.Move(fileA, reportA);

            bool bGotReport = await WaitForConditionAsync(() => File.Exists(reportB) && !File.Exists(fileB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Assert.True(bGotReport, "Client B should have report.txt and no shared.txt");

            var manifestAfter = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, cts.Token);
            var reportEntry = manifestAfter.Entries.FirstOrDefault(e => e.RelativePath == "report.txt");
            Assert.NotNull(reportEntry);
            Assert.Equal(originalFileId, reportEntry.FileId);
        }
    }

    [Fact]
    public async Task LocalRename_PreservesCurrentVersionNumber()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var (nodeA, nodeB, sharedSetId, _, originalVersion, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token);

        using (nodeA)
        using (nodeB)
        {
            Assert.Equal(2, originalVersion);

            File.Move(fileA, reportA);

            bool bGotReport = await WaitForConditionAsync(() => File.Exists(reportB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Assert.True(bGotReport);

            var manifestAfter = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, cts.Token);
            var reportEntry = manifestAfter.Entries.FirstOrDefault(e => e.RelativePath == "report.txt");
            Assert.NotNull(reportEntry);
            Assert.Equal(originalVersion, reportEntry.CurrentVersionNumber);
        }
    }

    [Fact]
    public async Task LocalRename_PreservesVersionHistory()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var (nodeA, nodeB, sharedSetId, originalFileId, _, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token);

        using (nodeA)
        using (nodeB)
        {
            File.Move(fileA, reportA);

            bool bGotReport = await WaitForConditionAsync(() => File.Exists(reportB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Assert.True(bGotReport);

            var verHistory = await nodeA.GatewayClient.GetVersionsAsync(new GetVersionsRequest
            {
                FileId = originalFileId
            }, cts.Token);

            Assert.True(verHistory.Success);
            Assert.Equal(2, verHistory.Versions.Count);
            Assert.Contains(verHistory.Versions, v => v.VersionNumber == 1);
            Assert.Contains(verHistory.Versions, v => v.VersionNumber == 2);
        }
    }

    [Fact]
    public async Task LocalRename_CreatesSingleRenameChangeLogEntry()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var (nodeA, nodeB, sharedSetId, originalFileId, _, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token);

        using (nodeA)
        using (nodeB)
        {
            var initialLogs = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, cts.Token);
            var initialIds = initialLogs.Entries.Select(e => e.Id).ToHashSet();

            File.Move(fileA, reportA);

            bool bGotReport = await WaitForConditionAsync(() => File.Exists(reportB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Assert.True(bGotReport);

            var changeLog = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, cts.Token);
            var newEntries = changeLog.Entries.Where(e => !initialIds.Contains(e.Id)).ToList();

            Assert.Single(newEntries);
            var renameEntry = newEntries[0];
            Assert.Equal("RENAME", renameEntry.Action);
            Assert.Equal(originalFileId, renameEntry.FileId);
            Assert.Equal(nodeA.ClientId, renameEntry.ActorClientId);
            Assert.Contains("shared.txt", renameEntry.Details);
            Assert.Contains("report.txt", renameEntry.Details);
        }
    }

    [Fact]
    public async Task LocalRename_DoesNotCreateDeleteAndCreateEntries()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var (nodeA, nodeB, sharedSetId, _, _, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token);

        using (nodeA)
        using (nodeB)
        {
            var initialLogs = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, cts.Token);
            var initialIds = initialLogs.Entries.Select(e => e.Id).ToHashSet();

            File.Move(fileA, reportA);

            bool bGotReport = await WaitForConditionAsync(() => File.Exists(reportB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Assert.True(bGotReport);

            var changeLog = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, cts.Token);
            var newEntries = changeLog.Entries.Where(e => !initialIds.Contains(e.Id)).ToList();

            Assert.DoesNotContain(newEntries, e => e.Action == "DELETE");
            Assert.DoesNotContain(newEntries, e => e.Action == "CREATE");
        }
    }

    [Fact]
    public async Task SharedRename_PropagatesToOtherClient_PreservingFileId()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var (nodeA, nodeB, sharedSetId, originalFileId, originalVersion, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token);

        using (nodeA)
        using (nodeB)
        {
            File.Move(fileA, reportA);

            bool bSettled = await WaitForConditionAsync(() => File.Exists(reportB) && !File.Exists(fileB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Assert.True(bSettled);

            // Verify Client B's LocalFileIndex has report.txt with the same FileId
            var indexB = await nodeB.IndexService.LoadIndexAsync(sharedSetId, cts.Token);
            Assert.True(indexB.Entries.TryGetValue("report.txt", out var entryB));
            Assert.Equal(originalFileId, entryB.FileId);
            Assert.Equal(originalVersion, entryB.LastSyncedServerVersion);
            Assert.False(indexB.Entries.ContainsKey("shared.txt"));
        }
    }

    [Fact]
    public async Task SharedRename_DoesNotCreateDuplicateServerRename()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var (nodeA, nodeB, sharedSetId, _, _, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token);

        using (nodeA)
        using (nodeB)
        {
            File.Move(fileA, reportA);

            bool bSettled = await WaitForConditionAsync(() => File.Exists(reportB) && !File.Exists(fileB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Assert.True(bSettled);

            // Wait additional 2 seconds to ensure no watcher echo from B reaches server
            await Task.Delay(2000, cts.Token);

            var changeLog = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, cts.Token);
            var renames = changeLog.Entries.Where(e => e.Action == "RENAME").ToList();
            Assert.Single(renames);
            Assert.Equal(nodeA.ClientId, renames[0].ActorClientId);
        }
    }

    [Fact]
    public async Task WatcherRename_ConcurrentWithPolling_RemainsRename()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        // Aggressive polling (every 80ms) on both nodes to stress-test concurrency with watcher
        var (nodeA, nodeB, sharedSetId, originalFileId, originalVersion, fileA, reportA, fileB, reportB) =
            await SetupSharedRenameScenarioAsync(cts.Token, pollingInterval: TimeSpan.FromMilliseconds(80));

        using (nodeA)
        using (nodeB)
        {
            var initialLogs = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, cts.Token);
            var initialIds = initialLogs.Entries.Select(e => e.Id).ToHashSet();

            File.Move(fileA, reportA);

            bool bSettled = await WaitForConditionAsync(() => File.Exists(reportB) && !File.Exists(fileB), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100));
            Assert.True(bSettled);

            // Ensure no race condition turned rename into DELETE + CREATE
            var changeLog = await nodeA.GatewayClient.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = sharedSetId }, cts.Token);
            var newEntries = changeLog.Entries.Where(e => !initialIds.Contains(e.Id)).ToList();

            Assert.DoesNotContain(newEntries, e => e.Action == "DELETE");
            Assert.DoesNotContain(newEntries, e => e.Action == "CREATE");
            var renames = newEntries.Where(e => e.Action == "RENAME").ToList();
            Assert.Single(renames);

            var manifestAfter = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest { BackupSetId = sharedSetId }, cts.Token);
            var reportEntry = manifestAfter.Entries.FirstOrDefault(e => e.RelativePath == "report.txt");
            Assert.NotNull(reportEntry);
            Assert.Equal(originalFileId, reportEntry.FileId);
            Assert.Equal(originalVersion, reportEntry.CurrentVersionNumber);
        }
    }
}
