using System.Security.Cryptography;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
using Backup.MetadataService.Services;
using Backup.Shared.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class RestoreVersionSemanticsTests
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private async Task<(MetadataApplicationService appService, Guid backupSetId, Guid clientId)> SetupServiceAsync()
    {
        var options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var factory = new TestDbContextFactory(options);
        var appService = new MetadataApplicationService(factory, NullLogger<MetadataApplicationService>.Instance);

        var clientResp = await appService.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "SemanticsClient_" + Guid.NewGuid().ToString("N")[..8],
            MachineName = Environment.MachineName
        });

        var bsResp = await appService.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = clientResp.ClientId,
            Name = "SemanticsBS_" + Guid.NewGuid().ToString("N")[..8],
            LocalRootPath = "C:/monitored"
        });

        return (appService, bsResp.BackupSetId, clientResp.ClientId);
    }

    [Fact]
    public async Task RestoreV1_WhenCurrentIsV3_CreatesV4_WithV1Hash_AndRestoreChangeLog()
    {
        var (appService, backupSetId, clientId) = await SetupServiceAsync();
        string relativePath = "docs/sample_" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        // Version 1
        string hash1 = "1111111111111111111111111111111111111111111111111111111111111111";
        var prep1 = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 100,
            Sha256 = hash1
        });
        await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 100,
            ActualSha256 = hash1,
            StorageKey = "blobs/v1"
        });

        // Version 2
        string hash2 = "2222222222222222222222222222222222222222222222222222222222222222";
        var prep2 = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 120,
            Sha256 = hash2
        });
        await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep2.OperationId!.Value,
            ActualSize = 120,
            ActualSha256 = hash2,
            StorageKey = "blobs/v2"
        });

        // Version 3
        string hash3 = "3333333333333333333333333333333333333333333333333333333333333333";
        var prep3 = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 150,
            Sha256 = hash3
        });
        await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep3.OperationId!.Value,
            ActualSize = 150,
            ActualSha256 = hash3,
            StorageKey = "blobs/v3"
        });

        // Verify current is v3
        var verListBefore = await appService.GetVersionsAsync(new GetVersionsRequest { FileId = prep1.FileId });
        Assert.Equal(3, verListBefore.Versions.Count);

        // Act: Restore v1 -> creates new version with hash1 and ChangeOrigin = Restore
        var prepRestore = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 100,
            Sha256 = hash1,
            ChangeOrigin = BackupChangeOrigin.Restore
        });

        Assert.True(prepRestore.UploadRequired);
        Assert.Equal(4, prepRestore.ExpectedVersionNumber);

        var commitRestore = await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prepRestore.OperationId!.Value,
            ActualSize = 100,
            ActualSha256 = hash1,
            StorageKey = "blobs/v4"
        });

        Assert.True(commitRestore.Success);
        Assert.Equal(4, commitRestore.VersionNumber);

        // Assert 1: v4 content/hash equals v1
        var verListAfter = await appService.GetVersionsAsync(new GetVersionsRequest { FileId = prep1.FileId });
        Assert.Equal(4, verListAfter.Versions.Count);

        var v4 = verListAfter.Versions.First(v => v.VersionNumber == 4);
        var v1 = verListAfter.Versions.First(v => v.VersionNumber == 1);
        Assert.Equal(v1.Sha256, v4.Sha256);
        Assert.Equal(v1.Size, v4.Size);

        // Assert 2: v1, v2, v3 remain completely unchanged (immutable)
        var v2 = verListAfter.Versions.First(v => v.VersionNumber == 2);
        var v3 = verListAfter.Versions.First(v => v.VersionNumber == 3);
        Assert.Equal(hash2, v2.Sha256);
        Assert.Equal(hash3, v3.Sha256);

        // Assert 3: CurrentVersionNumber becomes 4
        var filesResp = await appService.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId });
        var file = filesResp.Files.First(f => f.FileId == prep1.FileId);
        Assert.Equal(4, file.CurrentVersionNumber);

        // Assert 4: ChangeLog action is RESTORE
        var changeLogs = await appService.GetChangeLogsAsync(new GetChangeLogRequest { BackupSetId = backupSetId });
        var restoreLog = changeLogs.Entries.FirstOrDefault(cl => cl.Action == "RESTORE");
        Assert.NotNull(restoreLog);
        Assert.Equal(prep1.FileId, restoreLog.FileId);
    }

    [Fact]
    public async Task RestoreDeletedFile_ReactivatesIt_AndRecordsRestore()
    {
        var (appService, backupSetId, clientId) = await SetupServiceAsync();
        string relativePath = "delete_restore_" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        // Version 1
        string hash1 = "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111";
        var prep1 = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 50,
            Sha256 = hash1
        });
        await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 50,
            ActualSha256 = hash1,
            StorageKey = "blobs/del_v1"
        });

        // Delete file
        var delResp = await appService.DeleteFileMetadataAsync(new DeleteFileMetadataRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath
        });
        Assert.True(delResp.Success);

        // Verify file is marked deleted
        var filesList = await appService.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId, IncludeDeleted = true });
        var deletedFile = filesList.Files.First(f => f.FileId == prep1.FileId);
        Assert.True(deletedFile.IsDeleted);

        // Act: Restore v1 of deleted file
        var prepRestore = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 50,
            Sha256 = hash1,
            ChangeOrigin = BackupChangeOrigin.Restore
        });

        Assert.True(prepRestore.UploadRequired);
        Assert.Equal(2, prepRestore.ExpectedVersionNumber);

        await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prepRestore.OperationId!.Value,
            ActualSize = 50,
            ActualSha256 = hash1,
            StorageKey = "blobs/del_v2"
        });

        // Assert: IsDeleted is now false, version is 2, change log is RESTORE
        var filesListAfter = await appService.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId });
        var activeFile = filesListAfter.Files.First(f => f.FileId == prep1.FileId);
        Assert.False(activeFile.IsDeleted, "Restoring deleted file must set IsDeleted = false.");
        Assert.Equal(2, activeFile.CurrentVersionNumber);

        var changeLogs = await appService.GetChangeLogsAsync(new GetChangeLogRequest { BackupSetId = backupSetId });
        var restoreLog = changeLogs.Entries.FirstOrDefault(cl => cl.Action == "RESTORE");
        Assert.NotNull(restoreLog);
    }

    [Fact]
    public async Task RestoreCurrentIdenticalVersion_DoesNotCreateUnnecessaryDuplicate()
    {
        var (appService, backupSetId, clientId) = await SetupServiceAsync();
        string relativePath = "same_version_" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        string hash1 = "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222";
        var prep1 = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 80,
            Sha256 = hash1
        });
        await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 80,
            ActualSha256 = hash1,
            StorageKey = "blobs/same_v1"
        });

        // Attempting to restore the same current active version with same hash
        var prepSame = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = backupSetId,
            RelativePath = relativePath,
            Size = 80,
            Sha256 = hash1,
            ChangeOrigin = BackupChangeOrigin.Restore
        });

        Assert.False(prepSame.UploadRequired, "If restored hash is already the current active server hash, UploadRequired must be false.");
        Assert.Equal(1, prepSame.ExpectedVersionNumber);

        // Verify version count is still 1
        var verList = await appService.GetVersionsAsync(new GetVersionsRequest { FileId = prep1.FileId });
        Assert.Single(verList.Versions);
    }
}
