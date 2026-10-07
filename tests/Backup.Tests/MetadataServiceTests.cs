using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
using Backup.MetadataService.Services;
using Backup.Shared.Messages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class MetadataServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MetadataDbContext> _options;
    private readonly IDbContextFactory<MetadataDbContext> _contextFactory;
    private readonly MetadataApplicationService _service;

    public MetadataServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Initialize SQLite schema
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
    public async Task Test_01_RegisterNewClient_Success()
    {
        var request = new RegisterClientRequest
        {
            Name = "Workstation-A",
            MachineName = "DESKTOP-123"
        };

        var response = await _service.RegisterClientAsync(request);

        Assert.True(response.Success);
        Assert.True(response.Created);
        Assert.NotEqual(Guid.Empty, response.ClientId);
        Assert.True(response.LastSeenAtUtc <= DateTime.UtcNow);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var clientInDb = await db.Clients.FindAsync(response.ClientId);
        Assert.NotNull(clientInDb);
        Assert.Equal("Workstation-A", clientInDb.Name);
    }

    [Fact]
    public async Task Test_02_ReregisterExistingClient_UpdatesLastSeen()
    {
        var reg1 = await _service.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "Initial-Name",
            MachineName = "MACHINE-1"
        });
        Assert.True(reg1.Created);

        var firstSeen = reg1.LastSeenAtUtc;
        await Task.Delay(20);

        var reg2 = await _service.RegisterClientAsync(new RegisterClientRequest
        {
            ClientId = reg1.ClientId,
            Name = "Updated-Name",
            MachineName = "MACHINE-1"
        });

        Assert.True(reg2.Success);
        Assert.False(reg2.Created);
        Assert.Equal(reg1.ClientId, reg2.ClientId);
        Assert.True(reg2.LastSeenAtUtc >= firstSeen);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var updated = await db.Clients.FindAsync(reg1.ClientId);
        Assert.NotNull(updated);
        Assert.Equal("Updated-Name", updated.Name);
    }

    [Fact]
    public async Task Test_03_CreateBackupSet_Success()
    {
        var client = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "Client-1", MachineName = "M1" });

        var request = new CreateBackupSetRequest
        {
            ClientId = client.ClientId,
            Name = "DocumentsBackup",
            LocalRootPath = "D:/UserData/Docs"
        };

        var response = await _service.CreateBackupSetAsync(request);

        Assert.True(response.Success);
        Assert.NotEqual(Guid.Empty, response.BackupSetId);
        Assert.Equal("DocumentsBackup", response.Name);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var backupSetInDb = await db.BackupSets.FindAsync(response.BackupSetId);
        Assert.NotNull(backupSetInDb);
        Assert.True(backupSetInDb.IsActive);
    }

    [Fact]
    public async Task Test_04_PrepareNewFile_UploadRequiredTrue_ExpectedVersion1()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "documents/contract.pdf",
            Size = 1024,
            Sha256 = "hash_v1"
        });

        Assert.True(prep.Success);
        Assert.True(prep.UploadRequired);
        Assert.NotNull(prep.OperationId);
        Assert.Equal(1, prep.ExpectedVersionNumber);
        Assert.NotEqual(Guid.Empty, prep.FileId);
    }

    [Fact]
    public async Task Test_05_PrepareSameCommittedHash_UploadRequiredFalse()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        // 1. Prepare and commit version 1
        var prep1 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "docs/file.txt",
            Size = 500,
            Sha256 = "identical_hash"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 500,
            ActualSha256 = "identical_hash",
            StorageKey = "storage/key/1"
        });

        // 2. Prepare same hash again
        var prep2 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "docs/file.txt",
            Size = 500,
            Sha256 = "identical_hash"
        });

        Assert.True(prep2.Success);
        Assert.False(prep2.UploadRequired);
        Assert.Null(prep2.OperationId);
        Assert.Equal(1, prep2.ExpectedVersionNumber);
    }

    [Fact]
    public async Task Test_06_PrepareChangedHash_NextVersion()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        // Prepare & Commit v1
        var prep1 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "data.csv",
            Size = 100,
            Sha256 = "hash_v1"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 100,
            ActualSha256 = "hash_v1",
            StorageKey = "storage/1"
        });

        // Prepare v2 with modified hash
        var prep2 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "data.csv",
            Size = 150,
            Sha256 = "hash_v2"
        });

        Assert.True(prep2.Success);
        Assert.True(prep2.UploadRequired);
        Assert.NotNull(prep2.OperationId);
        Assert.Equal(2, prep2.ExpectedVersionNumber);
    }

    [Fact]
    public async Task Test_07_PrepareVersion_DoesNotCreateFileVersionBeforeCommit()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "test.txt",
            Size = 200,
            Sha256 = "hash_x"
        });

        await using var db = await _contextFactory.CreateDbContextAsync();
        var versionsCount = await db.FileVersions.CountAsync(fv => fv.FileId == prep.FileId);
        Assert.Equal(0, versionsCount);

        var file = await db.Files.FindAsync(prep.FileId);
        Assert.NotNull(file);
        Assert.Equal(0, file.CurrentVersionNumber);
    }

    [Fact]
    public async Task Test_08_CommitVersion_CreatesVersion()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "report.docx",
            Size = 300,
            Sha256 = "sha_abc"
        });

        var commit = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            StorageKey = "keys/report_v1",
            ActualSize = 300,
            ActualSha256 = "sha_abc"
        });

        Assert.True(commit.Success);
        Assert.Equal(1, commit.VersionNumber);
        Assert.Equal("keys/report_v1", commit.StorageKey);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var fv = await db.FileVersions.FirstOrDefaultAsync(v => v.FileId == commit.FileId && v.VersionNumber == 1);
        Assert.NotNull(fv);
        Assert.Equal(300, fv.Size);
        Assert.Equal("sha_abc", fv.Sha256);
    }

    [Fact]
    public async Task Test_09_Commit_UpdatesCurrentVersionNumber()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "image.png",
            Size = 4000,
            Sha256 = "sha_img_1"
        });

        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            ActualSize = 4000,
            ActualSha256 = "sha_img_1",
            StorageKey = "s/img"
        });

        await using var db = await _contextFactory.CreateDbContextAsync();
        var file = await db.Files.FindAsync(prep.FileId);
        Assert.NotNull(file);
        Assert.Equal(1, file.CurrentVersionNumber);
        Assert.False(file.IsDeleted);
    }

    [Fact]
    public async Task Test_10_Commit_RemovesPendingFileOperation()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "file.log",
            Size = 10,
            Sha256 = "sha_log"
        });

        var opId = prep.OperationId!.Value;

        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = opId,
            ActualSize = 10,
            ActualSha256 = "sha_log"
        });

        await using var db = await _contextFactory.CreateDbContextAsync();
        var pending = await db.PendingFileOperations.FindAsync(opId);
        Assert.Null(pending);
    }

    [Fact]
    public async Task Test_11_IncorrectHashOnCommit_RollbackAndError()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "archive.zip",
            Size = 5000,
            Sha256 = "expected_hash_val"
        });

        var commit = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            ActualSize = 5000,
            ActualSha256 = "tampered_hash_val"
        });

        Assert.False(commit.Success);
        Assert.Contains("mismatch", commit.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _contextFactory.CreateDbContextAsync();
        // File version was not created
        var versionExists = await db.FileVersions.AnyAsync(v => v.FileId == prep.FileId);
        Assert.False(versionExists);

        // File version number remained 0
        var file = await db.Files.FindAsync(prep.FileId);
        Assert.Equal(0, file!.CurrentVersionNumber);
    }

    [Fact]
    public async Task Test_12_Delete_MarksFileIsDeleted_VersionsRemain()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        // Create & Commit file v1
        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "to_delete.txt",
            Size = 120,
            Sha256 = "sha_del"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            ActualSize = 120,
            ActualSha256 = "sha_del"
        });

        // Delete metadata
        var delResp = await _service.DeleteFileMetadataAsync(new DeleteFileMetadataRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "to_delete.txt"
        });

        Assert.True(delResp.Success);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var file = await db.Files.FindAsync(prep.FileId);
        Assert.NotNull(file);
        Assert.True(file.IsDeleted);

        // Historical versions still intact
        var versionsCount = await db.FileVersions.CountAsync(v => v.FileId == prep.FileId);
        Assert.Equal(1, versionsCount);
    }

    [Fact]
    public async Task Test_13_Rename_ChangesLogicalPath_VersionsRemain()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "old_name.txt",
            Size = 100,
            Sha256 = "sha_ren"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            ActualSize = 100,
            ActualSha256 = "sha_ren"
        });

        var renameResp = await _service.RenameFileMetadataAsync(new RenameFileMetadataRequest
        {
            BackupSetId = backupSetId,
            OldRelativePath = "old_name.txt",
            NewRelativePath = "renamed/new_name.txt"
        });

        Assert.True(renameResp.Success);

        await using var db = await _contextFactory.CreateDbContextAsync();
        var file = await db.Files.FindAsync(prep.FileId);
        Assert.NotNull(file);
        Assert.Equal("renamed/new_name.txt", file.RelativePath);

        // Versions remain linked to the file
        var versions = await db.FileVersions.Where(v => v.FileId == prep.FileId).ToListAsync();
        Assert.Single(versions);
    }

    [Fact]
    public async Task Test_14_Rename_DestinationConflict_Rejected()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        // Create File 1
        var prep1 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "fileA.txt",
            Size = 10,
            Sha256 = "h1"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 10,
            ActualSha256 = "h1"
        });

        // Create File 2
        var prep2 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "fileB.txt",
            Size = 20,
            Sha256 = "h2"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep2.OperationId!.Value,
            ActualSize = 20,
            ActualSha256 = "h2"
        });

        // Try renaming fileA to fileB (conflict)
        var renameResp = await _service.RenameFileMetadataAsync(new RenameFileMetadataRequest
        {
            BackupSetId = backupSetId,
            OldRelativePath = "fileA.txt",
            NewRelativePath = "fileB.txt"
        });

        Assert.False(renameResp.Success);
        Assert.Contains("already exists", renameResp.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_15_GetVersions_OrderedDescending()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        // Create v1
        var prep1 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "versioned.bin",
            Size = 10,
            Sha256 = "v1"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 10,
            ActualSha256 = "v1"
        });

        // Create v2
        var prep2 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "versioned.bin",
            Size = 20,
            Sha256 = "v2"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep2.OperationId!.Value,
            ActualSize = 20,
            ActualSha256 = "v2"
        });

        // Create v3
        var prep3 = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "versioned.bin",
            Size = 30,
            Sha256 = "v3"
        });
        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep3.OperationId!.Value,
            ActualSize = 30,
            ActualSha256 = "v3"
        });

        var versionsResp = await _service.GetVersionsAsync(new GetVersionsRequest { FileId = prep1.FileId });

        Assert.True(versionsResp.Success);
        Assert.Equal(3, versionsResp.Versions.Count);
        Assert.Equal(3, versionsResp.Versions[0].VersionNumber);
        Assert.Equal(2, versionsResp.Versions[1].VersionNumber);
        Assert.Equal(1, versionsResp.Versions[2].VersionNumber);
    }

    [Fact]
    public async Task Test_18_DuplicateRelativeFilePath_ThrowsDbUpdateException()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        await using var db1 = await _contextFactory.CreateDbContextAsync();
        db1.Files.Add(new FileEntity
        {
            Id = Guid.NewGuid(),
            BackupSetId = backupSetId,
            RelativePath = "duplicate.txt",
            CurrentVersionNumber = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db1.SaveChangesAsync();

        await using var db2 = await _contextFactory.CreateDbContextAsync();
        db2.Files.Add(new FileEntity
        {
            Id = Guid.NewGuid(),
            BackupSetId = backupSetId,
            RelativePath = "duplicate.txt", // Duplicate in same BackupSet!
            CurrentVersionNumber = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Test_17_CommitVersion_FirstCallCommitsSuccessfully()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();
        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "idempotency_1.txt",
            Size = 100,
            Sha256 = "hash_100"
        });

        Assert.True(prep.Success);
        Assert.NotNull(prep.OperationId);

        var commit = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            StorageKey = "blobs/key_100",
            ActualSize = 100,
            ActualSha256 = "hash_100"
        });

        Assert.True(commit.Success);
        Assert.False(commit.AlreadyCommitted);
        Assert.NotEqual(Guid.Empty, commit.FileId);
        Assert.Equal(1, commit.VersionNumber);
    }

    [Fact]
    public async Task Test_18_CommitVersion_SameOperationIdSecondCommitReturnsSuccess()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();
        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "idempotency_2.txt",
            Size = 200,
            Sha256 = "hash_200"
        });

        var commit1 = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            StorageKey = "blobs/key_200",
            ActualSize = 200,
            ActualSha256 = "hash_200"
        });
        Assert.True(commit1.Success);

        var commit2 = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId.Value,
            StorageKey = "blobs/key_200",
            ActualSize = 200,
            ActualSha256 = "hash_200"
        });

        Assert.True(commit2.Success);
        Assert.Equal(commit1.FileId, commit2.FileId);
        Assert.Equal(commit1.VersionNumber, commit2.VersionNumber);
    }

    [Fact]
    public async Task Test_19_CommitVersion_SecondCommitDoesNotCreateSecondFileVersion()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();
        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "idempotency_3.txt",
            Size = 300,
            Sha256 = "hash_300"
        });

        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            StorageKey = "blobs/key_300",
            ActualSize = 300,
            ActualSha256 = "hash_300"
        });

        await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId.Value,
            StorageKey = "blobs/key_300",
            ActualSize = 300,
            ActualSha256 = "hash_300"
        });

        await using var db = await _contextFactory.CreateDbContextAsync();
        var versions = await db.FileVersions.Where(v => v.FileId == prep.FileId).ToListAsync();
        Assert.Single(versions);
    }

    [Fact]
    public async Task Test_20_CommitVersion_AlreadyCommittedResponseIndicatesAlreadyCommitted()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();
        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "idempotency_4.txt",
            Size = 400,
            Sha256 = "hash_400"
        });

        var first = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            StorageKey = "blobs/key_400",
            ActualSize = 400,
            ActualSha256 = "hash_400"
        });
        Assert.False(first.AlreadyCommitted);

        var second = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId.Value,
            StorageKey = "blobs/key_400",
            ActualSize = 400,
            ActualSha256 = "hash_400"
        });
        Assert.True(second.AlreadyCommitted);
    }

    [Fact]
    public async Task Test_21_CommitVersion_SameOperationIdWithConflictingDataRejected()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();
        var prep = await _service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = "idempotency_5.txt",
            Size = 500,
            Sha256 = "hash_500"
        });

        var first = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            StorageKey = "blobs/key_500",
            ActualSize = 500,
            ActualSha256 = "hash_500"
        });
        Assert.True(first.Success);

        // Attempt second commit with same OperationId but conflicting hash/size
        var conflict = await _service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId.Value,
            StorageKey = "blobs/key_500_DIFFERENT",
            ActualSize = 500,
            ActualSha256 = "hash_CONFICTING"
        });

        Assert.False(conflict.Success);
        Assert.Equal("COMMIT_CONFLICT", conflict.ErrorCode);
    }

    [Fact]
    public async Task Test_22_CommitVersion_UniqueOperationIdDbConstraintWorks()
    {
        var (_, backupSetId) = await SetupClientAndBackupSetAsync();

        await using var db1 = await _contextFactory.CreateDbContextAsync();
        var file = new FileEntity
        {
            Id = Guid.NewGuid(),
            BackupSetId = backupSetId,
            RelativePath = "unique_op_test.txt",
            CurrentVersionNumber = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        db1.Files.Add(file);

        var sharedOpId = Guid.NewGuid();
        db1.FileVersions.Add(new FileVersionEntity
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            VersionNumber = 1,
            OperationId = sharedOpId,
            StorageKey = "key1",
            Size = 10,
            Sha256 = "hash1",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db1.SaveChangesAsync();

        await using var db2 = await _contextFactory.CreateDbContextAsync();
        db2.FileVersions.Add(new FileVersionEntity
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            VersionNumber = 2,
            OperationId = sharedOpId, // Violates UNIQUE(OperationId)
            StorageKey = "key2",
            Size = 20,
            Sha256 = "hash2",
            CreatedAtUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    private async Task<(Guid ClientId, Guid BackupSetId)> SetupClientAndBackupSetAsync()
    {
        var client = await _service.RegisterClientAsync(new RegisterClientRequest { Name = "TestClient", MachineName = "TestMachine" });
        var bs = await _service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = client.ClientId,
            Name = "DefaultSet",
            LocalRootPath = "C:/Test"
        });
        return (client.ClientId, bs.BackupSetId);
    }
}

internal class TestDbContextFactory : IDbContextFactory<MetadataDbContext>
{
    private readonly DbContextOptions<MetadataDbContext> _options;

    public TestDbContextFactory(DbContextOptions<MetadataDbContext> options)
    {
        _options = options;
    }

    public MetadataDbContext CreateDbContext()
    {
        return new MetadataDbContext(_options);
    }

    public Task<MetadataDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateDbContext());
    }
}
