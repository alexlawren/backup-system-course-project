using System.IO.Pipelines;
using System.Text.Json;
using Backup.MetadataService.Data;
using Backup.MetadataService.Services;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.SyncGateway.Clients;
using Backup.SyncGateway.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class ManifestReconciliationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MetadataDbContext> _options;
    private readonly IDbContextFactory<MetadataDbContext> _contextFactory;
    private readonly MetadataApplicationService _metadataService;

    public ManifestReconciliationTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var initContext = new MetadataDbContext(_options);
        initContext.Database.EnsureCreated();

        _contextFactory = new TestDbContextFactory(_options);
        _metadataService = new MetadataApplicationService(_contextFactory, NullLogger<MetadataApplicationService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task Section16_ReappearingDeletedFile_RequiresUploadAndIncrementsVersion()
    {
        // 1. Setup client and backup set
        var reg = await _metadataService.RegisterClientAsync(new RegisterClientRequest { Name = "PC-1" });
        var bs = await _metadataService.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "ReappearingSet",
            LocalRootPath = "C:\\test"
        });

        string relPath = "docs/file.txt";
        string originalSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // 2. Prepare & commit version 1
        var prep1 = await _metadataService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = bs.BackupSetId,
            RelativePath = relPath,
            Size = 100,
            Sha256 = originalSha
        });
        Assert.True(prep1.UploadRequired);
        Assert.Equal(1, prep1.ExpectedVersionNumber);

        var commit1 = await _metadataService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep1.OperationId!.Value,
            ActualSize = 100,
            ActualSha256 = originalSha,
            StorageKey = "key1"
        });
        Assert.True(commit1.Success);

        // 3. Delete file
        var delResp = await _metadataService.DeleteFileMetadataAsync(new DeleteFileMetadataRequest
        {
            BackupSetId = bs.BackupSetId,
            RelativePath = relPath
        });
        Assert.True(delResp.Success);

        // Verify it is marked as deleted
        var filesAfterDelete = await _metadataService.GetFilesAsync(new GetFilesRequest
        {
            BackupSetId = bs.BackupSetId,
            IncludeDeleted = true
        });
        var deletedFile = Assert.Single(filesAfterDelete.Files);
        Assert.True(deletedFile.IsDeleted);

        // 4. File reappears locally with EXACT SAME SHA-256
        var prep2 = await _metadataService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = bs.BackupSetId,
            RelativePath = relPath,
            Size = 100,
            Sha256 = originalSha
        });

        // MUST return UploadRequired = true and increment version to 2!
        Assert.True(prep2.UploadRequired, "Reappearing deleted file must require upload even if hash matches last version.");
        Assert.Equal(2, prep2.ExpectedVersionNumber);

        // 5. Commit version 2
        var commit2 = await _metadataService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prep2.OperationId!.Value,
            ActualSize = 100,
            ActualSha256 = originalSha,
            StorageKey = "key2"
        });
        Assert.True(commit2.Success);

        // 6. Verify IsDeleted is now false in DB
        var filesAfterRecreate = await _metadataService.GetFilesAsync(new GetFilesRequest
        {
            BackupSetId = bs.BackupSetId,
            IncludeDeleted = false
        });
        var activeFile = Assert.Single(filesAfterRecreate.Files);
        Assert.False(activeFile.IsDeleted);
        Assert.Equal(2, activeFile.CurrentVersionNumber);
    }

    [Fact]
    public async Task Section27_ServerCurrentSnapshot_ReturnsHashAndSizeWithoutNPlusOne()
    {
        var reg = await _metadataService.RegisterClientAsync(new RegisterClientRequest { Name = "PC-2" });
        var bs = await _metadataService.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = reg.ClientId,
            Name = "SnapshotSet",
            LocalRootPath = "C:\\snap"
        });

        // Add 2 files with committed versions
        string sha1 = "1111111111111111111111111111111111111111111111111111111111111111";
        string sha2 = "2222222222222222222222222222222222222222222222222222222222222222";

        var p1 = await _metadataService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = bs.BackupSetId,
            RelativePath = "a.txt",
            Size = 150,
            Sha256 = sha1
        });
        await _metadataService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = p1.OperationId!.Value,
            ActualSize = 150,
            ActualSha256 = sha1,
            StorageKey = "k1"
        });

        var p2 = await _metadataService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = bs.BackupSetId,
            RelativePath = "b.txt",
            Size = 300,
            Sha256 = sha2
        });
        await _metadataService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = p2.OperationId!.Value,
            ActualSize = 300,
            ActualSha256 = sha2,
            StorageKey = "k2"
        });

        var snapshot = await _metadataService.GetFilesAsync(new GetFilesRequest
        {
            BackupSetId = bs.BackupSetId,
            IncludeDeleted = true
        });

        Assert.Equal(2, snapshot.Files.Count);

        var a = snapshot.Files.First(f => f.RelativePath == "a.txt");
        Assert.Equal(150, a.CurrentSize);
        Assert.Equal(sha1, a.CurrentSha256);

        var b = snapshot.Files.First(f => f.RelativePath == "b.txt");
        Assert.Equal(300, b.CurrentSize);
        Assert.Equal(sha2, b.CurrentSha256);
    }

    [Fact]
    public async Task Section27_ManifestReconciliation_GeneratesCorrectDeterministicSyncPlan()
    {
        var backupSetId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient();

        // Server has:
        // 1. unchanged.txt (hash = h1)
        // 2. changed.txt (hash = h_old)
        // 3. deleted_on_server.txt (isDeleted = true, hash = h_del)
        // 4. missing_locally_active.txt (isDeleted = false, hash = h_miss)
        // 5. missing_locally_deleted.txt (isDeleted = true, hash = h_already_del)
        string h1 = "1111111111111111111111111111111111111111111111111111111111111111";
        string h_old = "2222222222222222222222222222222222222222222222222222222222222222";
        string h_new = "3333333333333333333333333333333333333333333333333333333333333333";
        string h_brand_new = "4444444444444444444444444444444444444444444444444444444444444444";

        fakeMetadata.GetFilesFunc = req => Task.FromResult(new GetFilesResponse
        {
            Success = true,
            BackupSetId = backupSetId,
            Files = new()
            {
                new() { FileId = Guid.NewGuid(), RelativePath = "unchanged.txt", CurrentVersionNumber = 1, IsDeleted = false, CurrentSha256 = h1, CurrentSize = 10 },
                new() { FileId = Guid.NewGuid(), RelativePath = "changed.txt", CurrentVersionNumber = 1, IsDeleted = false, CurrentSha256 = h_old, CurrentSize = 20 },
                new() { FileId = Guid.NewGuid(), RelativePath = "deleted_on_server.txt", CurrentVersionNumber = 1, IsDeleted = true, CurrentSha256 = "del_hash", CurrentSize = 30 },
                new() { FileId = Guid.NewGuid(), RelativePath = "missing_locally_active.txt", CurrentVersionNumber = 1, IsDeleted = false, CurrentSha256 = "miss_hash", CurrentSize = 40 },
                new() { FileId = Guid.NewGuid(), RelativePath = "missing_locally_deleted.txt", CurrentVersionNumber = 1, IsDeleted = true, CurrentSha256 = "already_del", CurrentSize = 50 }
            }
        });

        var fakeStorage = new FakeStorageServiceClient();
        var handler = new GatewaySessionHandler(fakeMetadata, fakeStorage, NullLogger<GatewaySessionHandler>.Instance);

        // Setup pipe for in-memory stream testing
        var pipe = new Pipe();
        var clientStream = pipe.Reader.AsStream();
        var serverStream = pipe.Writer.AsStream();

        var reversePipe = new Pipe();
        var serverWriteStream = reversePipe.Writer.AsStream();
        var clientReadStream = reversePipe.Reader.AsStream();

        // Local manifest:
        // 1. brand_new.txt -> LOCAL only -> UPLOAD NEW
        // 2. unchanged.txt -> hash matches -> UNCHANGED
        // 3. changed.txt -> hash differs -> UPLOAD CHANGED
        // 4. deleted_on_server.txt -> isDeleted on server -> UPLOAD SERVER_DELETED
        var manifestRequest = new ManifestRequest
        {
            BackupSetId = backupSetId,
            ClientId = Guid.NewGuid(),
            Files = new List<ManifestFileEntry>
            {
                new() { RelativePath = "brand_new.txt", Size = 50, Sha256 = h_brand_new, LastWriteTimeUtc = DateTime.UtcNow },
                new() { RelativePath = "unchanged.txt", Size = 10, Sha256 = h1, LastWriteTimeUtc = DateTime.UtcNow },
                new() { RelativePath = "changed.txt", Size = 25, Sha256 = h_new, LastWriteTimeUtc = DateTime.UtcNow },
                new() { RelativePath = "deleted_on_server.txt", Size = 30, Sha256 = "any_hash", LastWriteTimeUtc = DateTime.UtcNow }
            }
        };

        var writer = new ProtocolFrameWriter();
        var reader = new ProtocolFrameReader();

        // Write Manifest frame
        await writer.WriteJsonFrameAsync(serverStream, MessageType.Manifest, manifestRequest);

        // Start processing connection
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var twoWayStream = new BidirectionalStream(clientStream, serverWriteStream);

        var processTask = Task.Run(async () =>
        {
            await handler.ProcessConnectionAsync(twoWayStream, cts.Token);
        });

        // Read response
        var responseHeader = await reader.ReadHeaderAsync(clientReadStream, cts.Token);
        Assert.NotNull(responseHeader);
        Assert.Equal(MessageType.SyncPlan, responseHeader.MessageType);

        var syncPlan = await reader.ReadJsonBodyAsync<SyncPlanResponse>(clientReadStream, responseHeader.BodyLength, cts.Token);
        Assert.NotNull(syncPlan);
        Assert.True(syncPlan.Success);

        // Assert Unchanged
        Assert.Equal(1, syncPlan.UnchangedCount);

        // Assert Uploads:
        // Deterministic order: brand_new.txt, changed.txt, deleted_on_server.txt
        Assert.Equal(3, syncPlan.Upload.Count);

        var brandNew = syncPlan.Upload.First(u => u.RelativePath == "brand_new.txt");
        Assert.Equal(SyncPlanReasons.New, brandNew.Reason);

        var changed = syncPlan.Upload.First(u => u.RelativePath == "changed.txt");
        Assert.Equal(SyncPlanReasons.Changed, changed.Reason);

        var serverDeleted = syncPlan.Upload.First(u => u.RelativePath == "deleted_on_server.txt");
        Assert.Equal(SyncPlanReasons.ServerDeleted, serverDeleted.Reason);

        // Assert Deletes:
        // Only missing_locally_active.txt should be in Delete, missing_locally_deleted.txt should NOT be deleted again!
        Assert.Single(syncPlan.Delete);
        Assert.Equal("missing_locally_active.txt", syncPlan.Delete[0]);

        // Cleanup
        pipe.Writer.Complete();
        reversePipe.Writer.Complete();
        await handler.DisposeAsync();
    }

    private async Task<SyncPlanResponse> SendManifestAndGetPlanAsync(
        FakeMetadataServiceClient fakeMetadata,
        ManifestRequest request)
    {
        var fakeStorage = new FakeStorageServiceClient();
        await using var handler = new GatewaySessionHandler(fakeMetadata, fakeStorage, NullLogger<GatewaySessionHandler>.Instance);

        var pipe = new Pipe();
        var clientStream = pipe.Reader.AsStream();
        var serverStream = pipe.Writer.AsStream();

        var reversePipe = new Pipe();
        var serverWriteStream = reversePipe.Writer.AsStream();
        var clientReadStream = reversePipe.Reader.AsStream();

        var writer = new ProtocolFrameWriter();
        var reader = new ProtocolFrameReader();

        await writer.WriteJsonFrameAsync(serverStream, MessageType.Manifest, request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var twoWayStream = new BidirectionalStream(clientStream, serverWriteStream);

        var processTask = Task.Run(async () =>
        {
            await handler.ProcessConnectionAsync(twoWayStream, cts.Token);
        });

        var responseHeader = await reader.ReadHeaderAsync(clientReadStream, cts.Token);
        Assert.NotNull(responseHeader);
        Assert.Equal(MessageType.SyncPlan, responseHeader.MessageType);

        var syncPlan = await reader.ReadJsonBodyAsync<SyncPlanResponse>(clientReadStream, responseHeader.BodyLength, cts.Token);
        Assert.NotNull(syncPlan);

        pipe.Writer.Complete();
        reversePipe.Writer.Complete();
        return syncPlan;
    }

    [Fact]
    public async Task Section27_Manifest_LocalOnly_ReturnsUploadNew()
    {
        var backupSetId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient();
        fakeMetadata.GetFilesFunc = _ => Task.FromResult(new GetFilesResponse { Success = true, BackupSetId = backupSetId, Files = new() });

        var req = new ManifestRequest
        {
            BackupSetId = backupSetId,
            Files = new List<ManifestFileEntry>
            {
                new() { RelativePath = "new.txt", Size = 10, Sha256 = "hash_new", LastWriteTimeUtc = DateTime.UtcNow }
            }
        };

        var plan = await SendManifestAndGetPlanAsync(fakeMetadata, req);
        Assert.Single(plan.Upload);
        Assert.Equal("new.txt", plan.Upload[0].RelativePath);
        Assert.Equal(SyncPlanReasons.New, plan.Upload[0].Reason);
        Assert.Empty(plan.Delete);
        Assert.Equal(0, plan.UnchangedCount);
    }

    [Fact]
    public async Task Section27_Manifest_SameHash_ReturnsUnchanged()
    {
        var backupSetId = Guid.NewGuid();
        string hash = "same_hash_12345";
        var fakeMetadata = new FakeMetadataServiceClient();
        fakeMetadata.GetFilesFunc = _ => Task.FromResult(new GetFilesResponse
        {
            Success = true,
            BackupSetId = backupSetId,
            Files = new()
            {
                new() { RelativePath = "file.txt", CurrentSha256 = hash, CurrentSize = 10, IsDeleted = false, CurrentVersionNumber = 1 }
            }
        });

        var req = new ManifestRequest
        {
            BackupSetId = backupSetId,
            Files = new List<ManifestFileEntry>
            {
                new() { RelativePath = "file.txt", Size = 10, Sha256 = hash, LastWriteTimeUtc = DateTime.UtcNow }
            }
        };

        var plan = await SendManifestAndGetPlanAsync(fakeMetadata, req);
        Assert.Empty(plan.Upload);
        Assert.Empty(plan.Delete);
        Assert.Equal(1, plan.UnchangedCount);
    }

    [Fact]
    public async Task Section27_Manifest_ChangedHash_ReturnsUploadChanged()
    {
        var backupSetId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient();
        fakeMetadata.GetFilesFunc = _ => Task.FromResult(new GetFilesResponse
        {
            Success = true,
            BackupSetId = backupSetId,
            Files = new()
            {
                new() { RelativePath = "file.txt", CurrentSha256 = "hash_v1", CurrentSize = 10, IsDeleted = false, CurrentVersionNumber = 1 }
            }
        });

        var req = new ManifestRequest
        {
            BackupSetId = backupSetId,
            Files = new List<ManifestFileEntry>
            {
                new() { RelativePath = "file.txt", Size = 15, Sha256 = "hash_v2", LastWriteTimeUtc = DateTime.UtcNow }
            }
        };

        var plan = await SendManifestAndGetPlanAsync(fakeMetadata, req);
        Assert.Single(plan.Upload);
        Assert.Equal("file.txt", plan.Upload[0].RelativePath);
        Assert.Equal(SyncPlanReasons.Changed, plan.Upload[0].Reason);
        Assert.Empty(plan.Delete);
        Assert.Equal(0, plan.UnchangedCount);
    }

    [Fact]
    public async Task Section27_Manifest_ServerDeletedPlusLocalExists_ReturnsUploadServerDeleted()
    {
        var backupSetId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient();
        fakeMetadata.GetFilesFunc = _ => Task.FromResult(new GetFilesResponse
        {
            Success = true,
            BackupSetId = backupSetId,
            Files = new()
            {
                new() { RelativePath = "revived.txt", CurrentSha256 = "hash_old", CurrentSize = 10, IsDeleted = true, CurrentVersionNumber = 1 }
            }
        });

        var req = new ManifestRequest
        {
            BackupSetId = backupSetId,
            Files = new List<ManifestFileEntry>
            {
                new() { RelativePath = "revived.txt", Size = 10, Sha256 = "hash_old", LastWriteTimeUtc = DateTime.UtcNow }
            }
        };

        var plan = await SendManifestAndGetPlanAsync(fakeMetadata, req);
        Assert.Single(plan.Upload);
        Assert.Equal("revived.txt", plan.Upload[0].RelativePath);
        Assert.Equal(SyncPlanReasons.ServerDeleted, plan.Upload[0].Reason);
        Assert.Empty(plan.Delete);
        Assert.Equal(0, plan.UnchangedCount);
    }

    [Fact]
    public async Task Section27_Manifest_ServerOnlyActive_ReturnsDelete()
    {
        var backupSetId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient();
        fakeMetadata.GetFilesFunc = _ => Task.FromResult(new GetFilesResponse
        {
            Success = true,
            BackupSetId = backupSetId,
            Files = new()
            {
                new() { RelativePath = "deleted_locally.txt", CurrentSha256 = "hash1", CurrentSize = 10, IsDeleted = false, CurrentVersionNumber = 1 }
            }
        });

        var req = new ManifestRequest
        {
            BackupSetId = backupSetId,
            Files = new List<ManifestFileEntry>() // Empty local
        };

        var plan = await SendManifestAndGetPlanAsync(fakeMetadata, req);
        Assert.Empty(plan.Upload);
        Assert.Single(plan.Delete);
        Assert.Equal("deleted_locally.txt", plan.Delete[0]);
        Assert.Equal(0, plan.UnchangedCount);
    }

    [Fact]
    public async Task Section27_Manifest_ServerOnlyAlreadyDeleted_NoDelete()
    {
        var backupSetId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient();
        fakeMetadata.GetFilesFunc = _ => Task.FromResult(new GetFilesResponse
        {
            Success = true,
            BackupSetId = backupSetId,
            Files = new()
            {
                new() { RelativePath = "already_dead.txt", CurrentSha256 = "hash1", CurrentSize = 10, IsDeleted = true, CurrentVersionNumber = 1 }
            }
        });

        var req = new ManifestRequest
        {
            BackupSetId = backupSetId,
            Files = new List<ManifestFileEntry>() // Empty local
        };

        var plan = await SendManifestAndGetPlanAsync(fakeMetadata, req);
        Assert.Empty(plan.Upload);
        Assert.Empty(plan.Delete); // Must NOT delete already deleted server file
        Assert.Equal(0, plan.UnchangedCount);
    }
}

internal class BidirectionalStream : Stream
{
    private readonly Stream _readable;
    private readonly Stream _writable;

    public BidirectionalStream(Stream readable, Stream writable)
    {
        _readable = readable;
        _writable = writable;
    }

    public override bool CanRead => _readable.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _writable.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _writable.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _writable.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _readable.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _readable.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _readable.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _writable.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _writable.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _writable.WriteAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

