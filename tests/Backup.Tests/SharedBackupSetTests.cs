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
using Backup.Shared.Validation;
using Backup.StorageService;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Backup.SyncGateway;
using Backup.SyncGateway.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backup.Tests;

public class SharedBackupSetUnitAndIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MetadataDbContext> _options;
    private readonly IDbContextFactory<MetadataDbContext> _contextFactory;
    private readonly MetadataApplicationService _service;

    public SharedBackupSetUnitAndIntegrationTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var initContext = new MetadataDbContext(_options);
        initContext.Database.EnsureCreated();

        _contextFactory = new TestDbContextFactory(_options);
        _service = new MetadataApplicationService(_contextFactory, NullLogger<MetadataApplicationService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void Test_01_ShareCodeHelper_Generate_ReturnsFormatWith31Alphabet()
    {
        for (int i = 0; i < 50; i++)
        {
            string code = ShareCodeHelper.Generate();
            Assert.NotNull(code);
            Assert.Equal(9, code.Length);
            Assert.Equal('-', code[4]);

            string raw = code.Replace("-", "");
            Assert.Equal(8, raw.Length);
            foreach (char c in raw)
            {
                Assert.Contains(c, ShareCodeHelper.AllowedAlphabet);
            }
        }
    }

    [Fact]
    public void Test_02_ShareCodeHelper_TryNormalize_ValidInputs()
    {
        Assert.True(ShareCodeHelper.TryNormalize("7h4k-92qx", out string norm1, out _));
        Assert.Equal("7H4K-92QX", norm1);

        Assert.True(ShareCodeHelper.TryNormalize("  7H4K92QX  ", out string norm2, out _));
        Assert.Equal("7H4K-92QX", norm2);

        Assert.True(ShareCodeHelper.TryNormalize("7h4k 92qx", out string norm3, out _));
        Assert.Equal("7H4K-92QX", norm3);
    }

    [Fact]
    public void Test_03_ShareCodeHelper_TryNormalize_InvalidInputs()
    {
        Assert.False(ShareCodeHelper.TryNormalize("", out _, out var err1));
        Assert.NotNull(err1);

        // 'O', '0', 'I', '1', 'L' are excluded from 31 unambiguous alphabet
        Assert.False(ShareCodeHelper.TryNormalize("7H0K-92QX", out _, out var err2));
        Assert.Contains("invalid character", err2, StringComparison.OrdinalIgnoreCase);

        Assert.False(ShareCodeHelper.TryNormalize("7HIK-92QX", out _, out var err3));
        Assert.Contains("invalid character", err3, StringComparison.OrdinalIgnoreCase);

        // Wrong length
        Assert.False(ShareCodeHelper.TryNormalize("ABC", out _, out var err4));
        Assert.Contains("must contain exactly 8 characters", err4, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_04_CreateBackupSet_Private_HasNoShareCode_CreatesOwnerMember()
    {
        var reg = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "OwnerClient" });
        Guid ownerId = reg.ClientId;

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = ownerId,
            Name = "PrivateSet",
            AccessMode = BackupSetAccessMode.Private
        });

        Assert.True(createResp.Success);
        Assert.Equal(BackupSetAccessMode.Private, createResp.AccessMode);
        Assert.Null(createResp.ShareCode);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var setInDb = await db.BackupSets.Include(bs => bs.Members).FirstOrDefaultAsync(bs => bs.Id == createResp.BackupSetId);
        Assert.NotNull(setInDb);
        Assert.Null(setInDb.ShareCode);
        Assert.Single(setInDb.Members);
        var ownerMember = setInDb.Members.First();
        Assert.Equal(ownerId, ownerMember.ClientId);
        Assert.Equal("OWNER", ownerMember.Role);
    }

    [Fact]
    public async Task Test_05_CreateBackupSet_Shared_GeneratesShareCode_CreatesOwnerMember()
    {
        var reg = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "OwnerClient" });
        Guid ownerId = reg.ClientId;

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = ownerId,
            Name = "SharedSet",
            AccessMode = BackupSetAccessMode.Shared
        });

        Assert.True(createResp.Success);
        Assert.Equal(BackupSetAccessMode.Shared, createResp.AccessMode);
        Assert.NotNull(createResp.ShareCode);
        Assert.True(ShareCodeHelper.IsValid(createResp.ShareCode));

        await using var db = await _contextFactory.CreateDbContextAsync();
        var setInDb = await db.BackupSets.Include(bs => bs.Members).FirstOrDefaultAsync(bs => bs.Id == createResp.BackupSetId);
        Assert.NotNull(setInDb);
        Assert.Equal(createResp.ShareCode, setInDb.ShareCode);
        Assert.Single(setInDb.Members);
        var ownerMember = setInDb.Members.First();
        Assert.Equal(ownerId, ownerMember.ClientId);
        Assert.Equal("OWNER", ownerMember.Role);
    }

    [Fact]
    public async Task Test_06_JoinSharedBackupSet_Success_AddsMemberRole()
    {
        var regOwner = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Owner" });
        var regMember = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "MemberB" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regOwner.ClientId,
            Name = "SharedSet1",
            AccessMode = BackupSetAccessMode.Shared
        });

        var joinResp = await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regMember.ClientId,
            ShareCode = createResp.ShareCode!.ToLowerInvariant() // case insensitive
        });

        Assert.True(joinResp.Success);
        Assert.Equal(createResp.BackupSetId, joinResp.BackupSetId);
        Assert.Equal(regOwner.ClientId, joinResp.OwnerClientId);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var members = await db.BackupSetMembers.Where(m => m.BackupSetId == createResp.BackupSetId).ToListAsync();
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.ClientId == regOwner.ClientId && m.Role == "OWNER");
        Assert.Contains(members, m => m.ClientId == regMember.ClientId && m.Role == "MEMBER");
    }

    [Fact]
    public async Task Test_07_JoinSharedBackupSet_AlreadyMember_IsIdempotent()
    {
        var regOwner = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Owner" });
        var regMember = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "MemberB" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regOwner.ClientId,
            Name = "SharedSet2",
            AccessMode = BackupSetAccessMode.Shared
        });

        var join1 = await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regMember.ClientId,
            ShareCode = createResp.ShareCode!
        });
        Assert.True(join1.Success);

        // Re-join
        var join2 = await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regMember.ClientId,
            ShareCode = createResp.ShareCode!
        });
        Assert.True(join2.Success);
        Assert.Equal(createResp.BackupSetId, join2.BackupSetId);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var members = await db.BackupSetMembers.Where(m => m.BackupSetId == createResp.BackupSetId).ToListAsync();
        Assert.Equal(2, members.Count);
    }

    [Fact]
    public async Task Test_08_JoinSharedBackupSet_InvalidOrNonExistentCode_Fails()
    {
        var reg = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "ClientX" });

        var res1 = await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = reg.ClientId,
            ShareCode = "INVALID_CODE"
        });
        Assert.False(res1.Success);
        Assert.Equal("INVALID_SHARE_CODE", res1.ErrorCode);

        var res2 = await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = reg.ClientId,
            ShareCode = "7H4K-92QX" // valid format but non-existent
        });
        Assert.False(res2.Success);
        Assert.Equal(BackupErrorCodes.BackupSetNotFound, res2.ErrorCode);
    }

    [Fact]
    public async Task Test_09_JoinSharedBackupSet_PrivateBackupSet_FailsNotShared()
    {
        var regOwner = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Owner" });
        var regMember = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Member" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regOwner.ClientId,
            Name = "PrivateSet",
            AccessMode = BackupSetAccessMode.Private
        });

        // Even if someone crafts a code or looks up private set, it has no ShareCode
        var joinResp = await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regMember.ClientId,
            ShareCode = "7H4K-92QX"
        });
        Assert.False(joinResp.Success);
    }

    [Fact]
    public async Task Test_10_GetBackupSetMembers_ReturnsAllMembersWithRoles()
    {
        var regA = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "OwnerA", MachineName = "PC-A" });
        var regB = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "MemberB", MachineName = "PC-B" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regA.ClientId,
            Name = "TeamSet",
            AccessMode = BackupSetAccessMode.Shared
        });

        await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regB.ClientId,
            ShareCode = createResp.ShareCode!
        });

        var membersResp = await _service.GetBackupSetMembersAsync(new GetBackupSetMembersRequest
        {
            BackupSetId = createResp.BackupSetId,
            ClientId = regB.ClientId
        });

        Assert.True(membersResp.Success);
        Assert.Equal(2, membersResp.Members.Count);
        var ownerDto = membersResp.Members.First(m => m.ClientId == regA.ClientId);
        Assert.Equal("OWNER", ownerDto.Role);
        Assert.Equal("OwnerA", ownerDto.ClientName);

        var memberDto = membersResp.Members.First(m => m.ClientId == regB.ClientId);
        Assert.Equal("MEMBER", memberDto.Role);
        Assert.Equal("MemberB", memberDto.ClientName);
    }

    [Fact]
    public async Task Test_11_PrepareVersion_AuthorizedMember_RecordsActorClientId()
    {
        var regOwner = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Owner" });
        var regMember = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Member" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regOwner.ClientId,
            Name = "SharedStorage",
            AccessMode = BackupSetAccessMode.Shared
        });

        await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regMember.ClientId,
            ShareCode = createResp.ShareCode!
        });

        var prepResp = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = createResp.BackupSetId,
            RelativePath = "shared_doc.txt",
            Size = 100,
            Sha256 = new string('a', 64),
            ActorClientId = regMember.ClientId,
            ClientId = regMember.ClientId
        });

        Assert.True(prepResp.Success);
        Assert.True(prepResp.UploadRequired);

        // Verify storage namespace invariant: Owner Client ID is used for StorageKey namespace!
        Assert.Equal(regOwner.ClientId, prepResp.ClientId);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var pendingOp = await db.PendingFileOperations.FindAsync(prepResp.OperationId);
        Assert.NotNull(pendingOp);
        Assert.Equal(regMember.ClientId, pendingOp.ActorClientId);
    }

    [Fact]
    public async Task Test_12_PrepareVersion_UnauthorizedClient_FailsAccessDenied()
    {
        var regOwner = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Owner" });
        var regStranger = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Stranger" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regOwner.ClientId,
            Name = "PrivateVault",
            AccessMode = BackupSetAccessMode.Private
        });

        var prepResp = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = createResp.BackupSetId,
            RelativePath = "secret.txt",
            Size = 50,
            Sha256 = new string('b', 64),
            ActorClientId = regStranger.ClientId,
            ClientId = regStranger.ClientId
        });

        Assert.False(prepResp.Success);
        Assert.Equal(BackupErrorCodes.AccessDenied, prepResp.ErrorCode);
    }

    [Fact]
    public async Task Test_13_CommitVersion_StorageNamespaceBelongsToOwner_ActorIsRecordedInChangeLog()
    {
        var regOwner = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Owner" });
        var regMember = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Member" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regOwner.ClientId,
            Name = "SharedSet",
            AccessMode = BackupSetAccessMode.Shared
        });

        await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regMember.ClientId,
            ShareCode = createResp.ShareCode!
        });

        var prepResp = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = createResp.BackupSetId,
            RelativePath = "collab.txt",
            Size = 256,
            Sha256 = new string('c', 64),
            ActorClientId = regMember.ClientId,
            ClientId = regMember.ClientId
        });

        string storageKey = StorageKeyHelper.BuildStorageKey(regOwner.ClientId, createResp.BackupSetId, prepResp.FileId, 1);

        var commitResp = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prepResp.OperationId!.Value,
            StorageKey = storageKey,
            ActualSize = 256,
            ActualSha256 = new string('c', 64),
            ActorClientId = regMember.ClientId
        });

        Assert.True(commitResp.Success);

        // Verify ChangeLog records ActorClientId
        var changeLogResp = await _service.GetChangeLogsAsync(new GetChangeLogRequest
        {
            BackupSetId = createResp.BackupSetId,
            ClientId = regMember.ClientId
        });

        Assert.True(changeLogResp.Success);
        var entry = changeLogResp.Entries.FirstOrDefault(e => e.Action == "CREATE" && e.RelativePath == "collab.txt");
        Assert.NotNull(entry);
        Assert.Equal(regMember.ClientId, entry.ActorClientId);
        Assert.Equal("Member", entry.ActorClientName);
    }

    [Fact]
    public async Task Test_14_DeleteAndRename_AuthorizedMember_RecordsActorClientIdInChangeLog()
    {
        var regOwner = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Owner" });
        var regMember = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Member" });

        var createResp = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = regOwner.ClientId,
            Name = "SharedSetOps",
            AccessMode = BackupSetAccessMode.Shared
        });

        await _service.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ClientId = regMember.ClientId,
            ShareCode = createResp.ShareCode!
        });

        // 1. Prepare and commit initial file
        var prepResp = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = createResp.BackupSetId,
            RelativePath = "notes.txt",
            Size = 50,
            Sha256 = new string('d', 64),
            ActorClientId = regOwner.ClientId,
            ClientId = regOwner.ClientId
        });

        string storageKey = StorageKeyHelper.BuildStorageKey(regOwner.ClientId, createResp.BackupSetId, prepResp.FileId, 1);
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prepResp.OperationId!.Value,
            StorageKey = storageKey,
            ActualSize = 50,
            ActualSha256 = new string('d', 64),
            ActorClientId = regOwner.ClientId
        });

        // 2. Member renames file
        var renResp = await _service.RenameFileMetadataAsync(new RenameFileMetadataRequest
        {
            BackupSetId = createResp.BackupSetId,
            OldRelativePath = "notes.txt",
            NewRelativePath = "renamed_notes.txt",
            ActorClientId = regMember.ClientId,
            ClientId = regMember.ClientId
        });
        Assert.True(renResp.Success);

        // 3. Member deletes file
        var delResp = await _service.DeleteFileMetadataAsync(new DeleteFileMetadataRequest
        {
            BackupSetId = createResp.BackupSetId,
            RelativePath = "renamed_notes.txt",
            ActorClientId = regMember.ClientId,
            ClientId = regMember.ClientId
        });
        Assert.True(delResp.Success);

        // 4. Verify ChangeLogs record Member as Actor
        var logs = await _service.GetChangeLogsAsync(new GetChangeLogRequest
        {
            BackupSetId = createResp.BackupSetId,
            ClientId = regOwner.ClientId
        });

        Assert.True(logs.Success);
        var renameEntry = logs.Entries.FirstOrDefault(e => e.Action == "RENAME");
        Assert.NotNull(renameEntry);
        Assert.Equal(regMember.ClientId, renameEntry.ActorClientId);

        var deleteEntry = logs.Entries.FirstOrDefault(e => e.Action == "DELETE");
        Assert.NotNull(deleteEntry);
        Assert.Equal(regMember.ClientId, deleteEntry.ActorClientId);
    }
}

public class SharedBackupSetE2ETests : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly string _storageRoot;
    private readonly string _clientDataDirA;
    private readonly string _clientDataDirB;
    private readonly string _clientDataDirC;
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

    public SharedBackupSetE2ETests()
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), "e2e_storage_shared_" + uniqueId);
        _clientDataDirA = Path.Combine(Path.GetTempPath(), "e2e_clientA_shared_" + uniqueId);
        _clientDataDirB = Path.Combine(Path.GetTempPath(), "e2e_clientB_shared_" + uniqueId);
        _clientDataDirC = Path.Combine(Path.GetTempPath(), "e2e_clientC_shared_" + uniqueId);
        _sourceDirA = Path.Combine(Path.GetTempPath(), "e2e_sourceA_shared_" + uniqueId);
        _sourceDirB = Path.Combine(Path.GetTempPath(), "e2e_sourceB_shared_" + uniqueId);

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_clientDataDirA);
        Directory.CreateDirectory(_clientDataDirB);
        Directory.CreateDirectory(_clientDataDirC);
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
        try { Directory.Delete(_clientDataDirC, recursive: true); } catch { }
        try { Directory.Delete(_sourceDirA, recursive: true); } catch { }
        try { Directory.Delete(_sourceDirB, recursive: true); } catch { }

        _cts.Dispose();
    }

    [Fact]
    public async Task SharedSetFoundation_E2E()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = testCts.Token;

        // 1. Client A: Register and create a SHARED backup set
        var clientA = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityA = new ClientIdentityService(Path.Combine(_clientDataDirA, "client-state.json"));
        var serviceA = new BackupClientService(clientA, identityA);
        await serviceA.EnsureRegisteredAsync(token);
        var idA = (await serviceA.GetIdentityAsync(token))!.ClientId!.Value;

        var setRespA = await clientA.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = idA,
            Name = "SharedProjectVault",
            AccessMode = BackupSetAccessMode.Shared
        }, token);

        Assert.True(setRespA.Success);
        Assert.NotNull(setRespA.ShareCode);
        Guid sharedSetId = setRespA.BackupSetId;
        string shareCode = setRespA.ShareCode;

        // 2. Client A uploads fileA.txt
        string fileAPath = Path.Combine(_sourceDirA, "fileA.txt");
        File.WriteAllText(fileAPath, "Owner file content A");
        var resA = await serviceA.BackupSingleFileAsync(sharedSetId, _sourceDirA, fileAPath, token);
        Assert.True(resA.Success);
        Assert.Contains(idA.ToString(), resA.StorageKey!);

        // 3. Client B: Register and join shared backup set using ShareCode
        var clientB = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityB = new ClientIdentityService(Path.Combine(_clientDataDirB, "client-state.json"));
        var serviceB = new BackupClientService(clientB, identityB);
        await serviceB.EnsureRegisteredAsync(token);
        var idB = (await serviceB.GetIdentityAsync(token))!.ClientId!.Value;

        var joinResp = await clientB.JoinSharedBackupSetAsync(new JoinSharedBackupSetRequest
        {
            ShareCode = shareCode
        }, token);

        Assert.True(joinResp.Success);
        Assert.Equal(sharedSetId, joinResp.BackupSetId);
        Assert.Equal(idA, joinResp.OwnerClientId);

        // 4. Client B queries GetBackupSetMembers
        var membersResp = await clientB.GetBackupSetMembersAsync(new GetBackupSetMembersRequest
        {
            BackupSetId = sharedSetId
        }, token);

        Assert.True(membersResp.Success);
        Assert.Equal(2, membersResp.Members.Count);
        Assert.Contains(membersResp.Members, m => m.ClientId == idA && m.Role == "OWNER");
        Assert.Contains(membersResp.Members, m => m.ClientId == idB && m.Role == "MEMBER");

        // 5. Client B uploads fileB.txt into the shared backup set
        string fileBPath = Path.Combine(_sourceDirB, "fileB.txt");
        File.WriteAllText(fileBPath, "Member file content B");
        var resB = await serviceB.BackupSingleFileAsync(sharedSetId, _sourceDirB, fileBPath, token);
        Assert.True(resB.Success);

        // Invariant Check: Storage Key must use Owner Client A's namespace!
        Assert.Contains(idA.ToString(), resB.StorageKey!);
        Assert.Contains(sharedSetId.ToString(), resB.StorageKey!);

        // 6. Verify ChangeLog for fileB.txt records ActorClientId == Client B
        var changeLogResp = await clientB.GetChangeLogAsync(new GetChangeLogRequest
        {
            BackupSetId = sharedSetId
        }, token);

        Assert.True(changeLogResp.Success);
        var fileBLog = changeLogResp.Entries.FirstOrDefault(e => e.RelativePath == "fileB.txt" && e.Action == "CREATE");
        Assert.NotNull(fileBLog);
        Assert.Equal(idB, fileBLog.ActorClientId);

        // 7. Client A lists files and sees both files
        var filesResp = await clientA.GetFilesAsync(new GetFilesRequest { BackupSetId = sharedSetId }, token);
        Assert.True(filesResp.Success);
        Assert.Equal(2, filesResp.Files.Count);
        Assert.Contains(filesResp.Files, f => f.RelativePath == "fileA.txt");
        Assert.Contains(filesResp.Files, f => f.RelativePath == "fileB.txt");

        // 8. Client C (unregistered / non-member) attempts to access the set and is denied
        var clientC = new BackupGatewayClient("127.0.0.1", _gatewayPort);
        var identityC = new ClientIdentityService(Path.Combine(_clientDataDirC, "client-state.json"));
        var serviceC = new BackupClientService(clientC, identityC);
        await serviceC.EnsureRegisteredAsync(token);
        var idC = (await serviceC.GetIdentityAsync(token))!.ClientId!.Value;

        string fileCPath = Path.Combine(_sourceDirB, "fileC.txt");
        File.WriteAllText(fileCPath, "Intruder content C");
        var resC = await serviceC.BackupSingleFileAsync(sharedSetId, _sourceDirB, fileCPath, token);
        Assert.False(resC.Success);
        Assert.Equal(BackupErrorCodes.AccessDenied, resC.ErrorCode);
    }
}
