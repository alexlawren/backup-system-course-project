using Backup.MetadataService.Data;
using Backup.MetadataService.Services;
using Backup.Shared.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class PostgresIntegrationTests
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    [Fact]
    public async Task Postgres_EndToEnd_Lifecycle_PersistsToDatabase()
    {
        var options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;

        var factory = new TestDbContextFactory(options);
        var service = new MetadataApplicationService(factory, NullLogger<MetadataApplicationService>.Instance);

        // 1. Register Client
        string clientName = $"PG-Client-{Guid.NewGuid():N}";
        var reg = await service.RegisterClientAsync(new RegisterClientRequest
        {
            Name = clientName,
            MachineName = "PG-TEST-NODE"
        });
        Assert.True(reg.Success);

        // 2. Create BackupSet
        var bs = await service.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "PostgresSet",
            LocalRootPath = "/pg/data"
        });
        Assert.True(bs.Success);

        // 3. Prepare Version
        string relativePath = $"pg_folder/test_doc_{Guid.NewGuid():N}.txt";
        var prep = await service.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = bs.BackupSetId,
            RelativePath = relativePath,
            Size = 256,
            Sha256 = "pg_hash_v1"
        });
        Assert.True(prep.Success);
        Assert.True(prep.UploadRequired);
        Assert.NotNull(prep.OperationId);

        // 4. Commit Version
        var commit = await service.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep.OperationId!.Value,
            ActualSize = 256,
            ActualSha256 = "pg_hash_v1",
            StorageKey = "pg_storage_key_1"
        });
        Assert.True(commit.Success);
        Assert.Equal(1, commit.VersionNumber);

        // 5. Verify records directly in PostgreSQL
        await using var db = await factory.CreateDbContextAsync();

        var clientInDb = await db.Clients.FindAsync(reg.ClientId);
        Assert.NotNull(clientInDb);
        Assert.Equal(clientName, clientInDb.Name);

        var fileInDb = await db.Files.FirstOrDefaultAsync(f => f.BackupSetId == bs.BackupSetId && f.RelativePath == relativePath);
        Assert.NotNull(fileInDb);
        Assert.Equal(1, fileInDb.CurrentVersionNumber);
        Assert.False(fileInDb.IsDeleted);

        var versionInDb = await db.FileVersions.FirstOrDefaultAsync(v => v.FileId == fileInDb.Id && v.VersionNumber == 1);
        Assert.NotNull(versionInDb);
        Assert.Equal("pg_hash_v1", versionInDb.Sha256);
        Assert.Equal("pg_storage_key_1", versionInDb.StorageKey);

        var changeLogInDb = await db.ChangeLogs.FirstOrDefaultAsync(cl => cl.FileId == fileInDb.Id);
        Assert.NotNull(changeLogInDb);
        Assert.Equal("CREATE", changeLogInDb.Action);
    }
}
