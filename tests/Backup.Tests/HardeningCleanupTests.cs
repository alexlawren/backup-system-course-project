using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
using Backup.MetadataService.Services;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backup.Tests;

public class HardeningCleanupTests : IAsyncLifetime
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    private readonly string _storageRoot;
    private readonly DbContextOptions<MetadataDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;

    public HardeningCleanupTests()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), "hardening_storage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);

        _dbOptions = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        _dbFactory = new TestDbContextFactory(_dbOptions);
    }

    public async Task InitializeAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task PendingOperationsCleanupWorker_RemovesExpired_PreservesActiveAndCommitted()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var client = new ClientEntity { Id = Guid.NewGuid(), MachineName = "CleanupTestHost" };
        var set = new BackupSetEntity { Id = Guid.NewGuid(), ClientId = client.Id, Name = "CleanupSet", LocalRootPath = @"C:\Test" };
        var file = new FileEntity { Id = Guid.NewGuid(), BackupSetId = set.Id, RelativePath = "cleanup_test.txt", CurrentVersionNumber = 1, IsDeleted = false, CreatedAtUtc = DateTime.UtcNow };
        var version = new FileVersionEntity
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            FileId = file.Id,
            VersionNumber = 1,
            Size = 100,
            Sha256 = "dummy_sha",
            StorageKey = "clients/dummy/test.bin",
            CreatedAtUtc = DateTime.UtcNow
        };

        var expiredOp = new PendingFileOperationEntity
        {
            OperationId = Guid.NewGuid(),
            FileId = file.Id,
            ExpectedVersionNumber = 2,
            ExpectedSize = 200,
            ExpectedSha256 = "expired_sha",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-30)
        };

        var activeOp = new PendingFileOperationEntity
        {
            OperationId = Guid.NewGuid(),
            FileId = file.Id,
            ExpectedVersionNumber = 3,
            ExpectedSize = 300,
            ExpectedSha256 = "active_sha",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        };

        db.Clients.Add(client);
        db.BackupSets.Add(set);
        db.Files.Add(file);
        db.FileVersions.Add(version);
        db.PendingFileOperations.AddRange(expiredOp, activeOp);
        await db.SaveChangesAsync();

        var options = Options.Create(new PendingOperationsCleanupOptions { CleanupIntervalSeconds = 60 });
        var worker = new PendingOperationsCleanupWorker(_dbFactory, NullLogger<PendingOperationsCleanupWorker>.Instance, options);

        // Act
        int cleanedCount = await worker.CleanupStaleOperationsAsync();

        // Assert
        Assert.True(cleanedCount >= 1);

        await using var verifyDb = await _dbFactory.CreateDbContextAsync();
        var remainingExpired = await verifyDb.PendingFileOperations.FindAsync(expiredOp.OperationId);
        Assert.Null(remainingExpired);

        var remainingActive = await verifyDb.PendingFileOperations.FindAsync(activeOp.OperationId);
        Assert.NotNull(remainingActive);

        var committedVersion = await verifyDb.FileVersions.FindAsync(version.Id);
        Assert.NotNull(committedVersion);

        var monitoredFile = await verifyDb.Files.FindAsync(file.Id);
        Assert.NotNull(monitoredFile);
        Assert.Equal(1, monitoredFile.CurrentVersionNumber); // untouched!
    }

    [Fact]
    public void StorageTempCleanup_RemovesStaleParts_PreservesRecentPartsAndCommittedBlobs()
    {
        var storageService = new FileStorageService(NullLogger<FileStorageService>.Instance, _storageRoot);

        string tmpDir = Path.Combine(_storageRoot, ".tmp");
        Directory.CreateDirectory(tmpDir);

        // 1. Create a stale .part file (2 hours old)
        string stalePart = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllText(stalePart, "stale temp content");
        File.SetLastWriteTimeUtc(stalePart, DateTime.UtcNow.AddHours(-2));

        // 2. Create a fresh .part file (10 minutes old)
        string freshPart = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllText(freshPart, "fresh temp content");
        File.SetLastWriteTimeUtc(freshPart, DateTime.UtcNow.AddMinutes(-10));

        // 3. Create committed version blob
        string committedDir = Path.Combine(_storageRoot, "clients", "test", "versions");
        Directory.CreateDirectory(committedDir);
        string committedBlob = Path.Combine(committedDir, "version1.bin");
        File.WriteAllText(committedBlob, "committed version content");
        File.SetLastWriteTimeUtc(committedBlob, DateTime.UtcNow.AddDays(-10)); // very old, but committed!

        var cleanupOptions = Options.Create(new StorageTempCleanupOptions { MaxAgeHours = 1, CleanupIntervalMinutes = 60 });
        var cleanupWorker = new StorageTempCleanupWorker(storageService, NullLogger<StorageTempCleanupWorker>.Instance, cleanupOptions);

        // Act
        int cleaned = cleanupWorker.CleanupStaleTempFiles();

        // Assert
        Assert.Equal(1, cleaned);
        Assert.False(File.Exists(stalePart));
        Assert.True(File.Exists(freshPart));
        Assert.True(File.Exists(committedBlob)); // never touched!
    }
}
