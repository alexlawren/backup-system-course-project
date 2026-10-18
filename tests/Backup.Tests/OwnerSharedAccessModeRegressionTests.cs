using Backup.Client.Web.Services;
using Backup.Shared.Messages;
using Xunit;

namespace Backup.Tests;

public class OwnerSharedAccessModeRegressionTests : IClassFixture<SharedSyncServerFixture>
{
    private readonly SharedSyncServerFixture _fixture;

    public OwnerSharedAccessModeRegressionTests(SharedSyncServerFixture fixture)
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

    // 1. OwnerCreatesSharedSet_LocalProfileRemainsShared
    [Fact]
    public async Task OwnerCreatesSharedSet_LocalProfileRemainsShared()
    {
        using var node = CreateNode("OwnerLocalProfileTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await node.RegisterAsync(cts.Token);

        var resp = await node.ClientService.CreateBackupSetAsync("OwnerSharedSet", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        Assert.True(resp.Success);
        Assert.Equal(BackupSetAccessMode.Shared, resp.AccessMode);
        Assert.False(string.IsNullOrWhiteSpace(resp.ShareCode));

        var profile = new SyncProfile
        {
            BackupSetId = resp.BackupSetId,
            Name = resp.Name,
            LocalRootPath = node.SourceDir,
            AutomaticSyncEnabled = true,
            AccessMode = resp.AccessMode,
            ShareCode = resp.ShareCode
        };

        await node.ProfileService.SaveProfileAsync(profile, cts.Token);
        await node.SyncEngine.SwitchProfileAsync(profile, cts.Token);

        Assert.Equal(BackupSetAccessMode.Shared, node.SyncEngine.ActiveProfile!.AccessMode);
        Assert.Equal(resp.ShareCode, node.SyncEngine.ActiveProfile!.ShareCode);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);
    }

    // 2. OwnerCreatesSharedSet_ProfileSerializationPreservesAccessMode
    [Fact]
    public async Task OwnerCreatesSharedSet_ProfileSerializationPreservesAccessMode()
    {
        using var node = CreateNode("ProfileSerializationTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await node.RegisterAsync(cts.Token);

        var setId = Guid.NewGuid();
        var shareCode = "ABCD-EFGH-JKMN";

        var profileToSave = new SyncProfile
        {
            BackupSetId = setId,
            Name = "SerializationSharedDemo",
            LocalRootPath = node.SourceDir,
            AutomaticSyncEnabled = true,
            AccessMode = BackupSetAccessMode.Shared,
            ShareCode = shareCode
        };

        await node.ProfileService.SaveProfileAsync(profileToSave, cts.Token);

        // Read directly from file to verify serialized contents
        string profileJsonPath = Path.Combine(node.ClientDataDir, "sync-profile.json");
        Assert.True(File.Exists(profileJsonPath));
        string rawJson = await File.ReadAllTextAsync(profileJsonPath, cts.Token);
        Assert.Contains("\"Shared\"", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(shareCode, rawJson);

        // Load profile back through service
        var loadedProfile = await node.ProfileService.GetProfileAsync(cts.Token);
        Assert.NotNull(loadedProfile);
        Assert.Equal(setId, loadedProfile.BackupSetId);
        Assert.Equal("SerializationSharedDemo", loadedProfile.Name);
        Assert.Equal(BackupSetAccessMode.Shared, loadedProfile.AccessMode);
        Assert.Equal(shareCode, loadedProfile.ShareCode);
        Assert.True(loadedProfile.AutomaticSyncEnabled);
    }

    // 3. OwnerSharedSet_RefreshState_DoesNotDowngradeToPrivate
    [Fact]
    public async Task OwnerSharedSet_RefreshState_DoesNotDowngradeToPrivate()
    {
        using var node = CreateNode("RefreshStateNoDowngrade");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await node.RegisterAsync(cts.Token);

        var resp = await node.ClientService.CreateBackupSetAsync("RefreshSharedSet", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        Assert.True(resp.Success);

        var profile = new SyncProfile
        {
            BackupSetId = resp.BackupSetId,
            Name = resp.Name,
            LocalRootPath = node.SourceDir,
            AutomaticSyncEnabled = true,
            AccessMode = resp.AccessMode,
            ShareCode = resp.ShareCode
        };
        await node.ProfileService.SaveProfileAsync(profile, cts.Token);
        await node.SyncEngine.StartAsync(profile, cts.Token);

        // Simulate multiple state refreshes (e.g., UI RefreshStateAsync)
        for (int i = 0; i < 3; i++)
        {
            var loaded = await node.ProfileService.GetProfileAsync(cts.Token);
            Assert.NotNull(loaded);
            Assert.Equal(BackupSetAccessMode.Shared, loaded.AccessMode);
            Assert.Equal(BackupSetAccessMode.Shared, node.SyncEngine.ActiveProfile!.AccessMode);
            Assert.True(node.SyncEngine.IsSharedPollingRunning);
        }
    }

    // 4. OwnerSharedSet_StartSync_StartsPollingLoop
    [Fact]
    public async Task OwnerSharedSet_StartSync_StartsPollingLoop()
    {
        using var node = CreateNode("StartSyncPollingTest");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await node.RegisterAsync(cts.Token);

        var resp = await node.ClientService.CreateBackupSetAsync("StartSyncSharedSet", node.SourceDir, BackupSetAccessMode.Shared, cts.Token);
        Assert.True(resp.Success);

        // When starting sync, even if a profile without explicit AccessMode is passed but ShareCode is present,
        // it must ensure AccessMode is Shared and start the polling loop
        var profile = new SyncProfile
        {
            BackupSetId = resp.BackupSetId,
            Name = resp.Name,
            LocalRootPath = node.SourceDir,
            AutomaticSyncEnabled = true,
            ShareCode = resp.ShareCode
        };

        await node.SyncEngine.StartAsync(profile, cts.Token);

        Assert.True(node.SyncEngine.IsRunning);
        Assert.Equal(BackupSetAccessMode.Shared, node.SyncEngine.ActiveProfile!.AccessMode);
        Assert.True(node.SyncEngine.IsSharedPollingRunning);
    }

    // 5. OwnerSharedSet_ReceivesMemberModificationAutomatically
    [Fact]
    public async Task OwnerSharedSet_ReceivesMemberModificationAutomatically()
    {
        using var nodeA = CreateNode("OwnerA", TimeSpan.FromMilliseconds(250));
        using var nodeB = CreateNode("MemberB", TimeSpan.FromMilliseconds(250));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await nodeA.RegisterAsync(cts.Token);
        await nodeB.RegisterAsync(cts.Token);

        // 1. Owner A creates Shared set
        var (sharedSetId, shareCode) = await nodeA.CreateSharedSetAsync("AutoPollingDemo", cts.Token);
        Assert.Equal(BackupSetAccessMode.Shared, nodeA.SyncEngine.ActiveProfile!.AccessMode);
        Assert.True(nodeA.SyncEngine.IsSharedPollingRunning);

        // 2. Member B joins
        await nodeB.JoinSharedSetAsync(shareCode, cts.Token);
        Assert.Equal(BackupSetAccessMode.Shared, nodeB.SyncEngine.ActiveProfile!.AccessMode);
        Assert.True(nodeB.SyncEngine.IsSharedPollingRunning);

        // 3. Owner A creates shared.txt = "Версия от клиента A"
        string fileA = Path.Combine(nodeA.SourceDir, "shared.txt");
        await File.WriteAllTextAsync(fileA, "Версия от клиента A", cts.Token);

        // B automatically receives file (v1)
        string fileB = Path.Combine(nodeB.SourceDir, "shared.txt");
        bool bReceivedV1 = await WaitForConditionAsync(
            () => File.Exists(fileB) && File.ReadAllText(fileB) == "Версия от клиента A",
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(200));
        Assert.True(bReceivedV1, "Node B should automatically receive v1 created by Node A");

        // 4. Member B modifies shared.txt = "Версия от клиента B"
        await File.WriteAllTextAsync(fileB, "Версия от клиента B", cts.Token);

        // 5. Owner A must automatically receive v2 via its shared polling loop without manual reconciliation!
        bool aReceivedV2 = await WaitForConditionAsync(
            () => File.ReadAllText(fileA) == "Версия от клиента B",
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(200));
        Assert.True(aReceivedV2, "Node A should automatically receive v2 modified by Node B via shared polling loop");

        // Verify version count on server remains v2, no duplicate v3 created
        var manifest = await nodeA.GatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest
        {
            BackupSetId = sharedSetId
        }, cts.Token);
        Assert.True(manifest.Success);
        var entry = manifest.Entries.FirstOrDefault(e => e.RelativePath == "shared.txt");
        Assert.NotNull(entry);
        Assert.Equal(2, entry.CurrentVersionNumber);
    }

    // 6. SharedOwnerAndJoinedMember_ProfilesBothHaveSharedAccessMode
    [Fact]
    public async Task SharedOwnerAndJoinedMember_ProfilesBothHaveSharedAccessMode()
    {
        using var owner = CreateNode("SharedOwnerRole");
        using var member = CreateNode("SharedMemberRole");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await owner.RegisterAsync(cts.Token);
        await member.RegisterAsync(cts.Token);

        var (setId, shareCode) = await owner.CreateSharedSetAsync("RoleConsistencySet", cts.Token);
        await member.JoinSharedSetAsync(shareCode, cts.Token);

        var ownerProfile = await owner.ProfileService.GetProfileAsync(cts.Token);
        var memberProfile = await member.ProfileService.GetProfileAsync(cts.Token);

        Assert.NotNull(ownerProfile);
        Assert.NotNull(memberProfile);

        Assert.Equal(BackupSetAccessMode.Shared, ownerProfile.AccessMode);
        Assert.Equal(BackupSetAccessMode.Shared, memberProfile.AccessMode);

        Assert.Equal(setId, ownerProfile.BackupSetId);
        Assert.Equal(setId, memberProfile.BackupSetId);

        Assert.Equal(shareCode, ownerProfile.ShareCode);
        Assert.Equal(shareCode, memberProfile.ShareCode);
    }

    // 7. PrivateSet_RemainsPrivate
    [Fact]
    public async Task PrivateSet_RemainsPrivate()
    {
        using var node = CreateNode("PrivateSetIsolation");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await node.RegisterAsync(cts.Token);

        var resp = await node.ClientService.CreateBackupSetAsync("PrivateIsolationSet", node.SourceDir, BackupSetAccessMode.Private, cts.Token);
        Assert.True(resp.Success);
        Assert.Equal(BackupSetAccessMode.Private, resp.AccessMode);
        Assert.Null(resp.ShareCode);

        var profile = new SyncProfile
        {
            BackupSetId = resp.BackupSetId,
            Name = resp.Name,
            LocalRootPath = node.SourceDir,
            AutomaticSyncEnabled = true,
            AccessMode = BackupSetAccessMode.Private,
            ShareCode = null
        };

        await node.ProfileService.SaveProfileAsync(profile, cts.Token);
        await node.SyncEngine.StartAsync(profile, cts.Token);

        Assert.Equal(BackupSetAccessMode.Private, node.SyncEngine.ActiveProfile!.AccessMode);
        Assert.False(node.SyncEngine.IsSharedPollingRunning);

        var loaded = await node.ProfileService.GetProfileAsync(cts.Token);
        Assert.NotNull(loaded);
        Assert.Equal(BackupSetAccessMode.Private, loaded.AccessMode);
        Assert.Null(loaded.ShareCode);
    }
}
