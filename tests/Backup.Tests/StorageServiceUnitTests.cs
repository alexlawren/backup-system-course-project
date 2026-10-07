using System.Security.Cryptography;
using System.Text;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Storage;
using Backup.StorageService.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class StorageServiceUnitTests : IDisposable
{
    private readonly string _testRoot;
    private readonly FileStorageService _storageService;

    public StorageServiceUnitTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "backup_storage_unit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _storageService = new FileStorageService(NullLogger<FileStorageService>.Instance, _testRoot);
        _storageService.Initialize();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void StorageKey_MatchesDeterministicConvention()
    {
        var clientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var backupSetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var fileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        int version = 5;

        var key = StorageKeyHelper.BuildStorageKey(clientId, backupSetId, fileId, version);

        Assert.Equal(
            "clients/11111111-1111-1111-1111-111111111111/backup-sets/22222222-2222-2222-2222-222222222222/files/33333333-3333-3333-3333-333333333333/versions/0000000005.bin",
            key);
        Assert.False(Path.IsPathRooted(key));
        Assert.DoesNotContain("\\", key);
    }

    [Fact]
    public void StorageKey_RejectsEmptyGuid_AndZeroOrNegativeVersion()
    {
        var validGuid = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => StorageKeyHelper.BuildStorageKey(Guid.Empty, validGuid, validGuid, 1));
        Assert.Throws<ArgumentException>(() => StorageKeyHelper.BuildStorageKey(validGuid, Guid.Empty, validGuid, 1));
        Assert.Throws<ArgumentException>(() => StorageKeyHelper.BuildStorageKey(validGuid, validGuid, Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageKeyHelper.BuildStorageKey(validGuid, validGuid, validGuid, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StorageKeyHelper.BuildStorageKey(validGuid, validGuid, validGuid, -1));
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("clients/../../../windows/win.ini")]
    [InlineData("/absolute/path")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("clients/\0invalid")]
    public void StorageKeyHelper_RejectsPathTraversal_AndEscapes(string maliciousKey)
    {
        bool success = StorageKeyHelper.TryResolvePhysicalPath(_testRoot, maliciousKey, out var physicalPath, out var err);

        Assert.False(success);
        Assert.NotNull(err);
        Assert.Empty(physicalPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")] // 63 chars
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8550")] // 65 chars
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8z")] // non-hex 'z'
    public void Sha256Validation_RejectsInvalidHashes(string? invalidHash)
    {
        bool isValid = StorageKeyHelper.TryValidateAndNormalizeSha256(invalidHash, out var normalized, out var err);

        Assert.False(isValid);
        Assert.NotNull(err);
        Assert.Empty(normalized);
    }

    [Fact]
    public async Task ZeroByteFile_SuccessfullyStored()
    {
        var opId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var backupSetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        int version = 1;

        // SHA-256 for empty file: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        string emptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var request = new StoreFileRequest
        {
            OperationId = opId,
            ClientId = clientId,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = version,
            ExpectedSize = 0,
            ExpectedSha256 = emptySha
        };

        using var memoryStream = new MemoryStream(Array.Empty<byte>());
        var result = await _storageService.WriteUploadedFileAsync(request, memoryStream, 0, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ActualSize);
        Assert.Equal(emptySha, result.ActualSha256);
        Assert.NotNull(result.StorageKey);

        // Verify physical file exists and is 0 bytes
        _storageService.TryResolvePhysicalPath(result.StorageKey, out var physicalPath, out _);
        Assert.True(File.Exists(physicalPath));
        Assert.Equal(0, new FileInfo(physicalPath).Length);

        // Verify temporary folder is clean
        var tempFiles = Directory.GetFiles(_storageService.TempPath);
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task HashMismatch_RemovesTempFile_AndDoesNotCreateFinalFile()
    {
        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 12,
            ExpectedSha256 = "0000000000000000000000000000000000000000000000000000000000000000" // wrong hash
        };

        byte[] payload = Encoding.UTF8.GetBytes("Hello World!");
        using var stream = new MemoryStream(payload);

        var result = await _storageService.WriteUploadedFileAsync(request, stream, payload.Length, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.HashMismatch, result.ErrorCode);

        // Verify no temp file left
        Assert.Empty(Directory.GetFiles(_storageService.TempPath));

        // Verify no final file created
        string key = StorageKeyHelper.BuildStorageKey(request.ClientId, request.BackupSetId, request.FileId, request.VersionNumber);
        _storageService.TryResolvePhysicalPath(key, out var finalPath, out _);
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task SizeMismatch_DoesNotCreateFinalFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("Short");
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 100, // expected 100, but stream length is 5
            ExpectedSha256 = hash
        };

        using var stream = new MemoryStream(payload);
        var result = await _storageService.WriteUploadedFileAsync(request, stream, payload.Length, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.SizeMismatch, result.ErrorCode);

        // Verify no temp file left
        Assert.Empty(Directory.GetFiles(_storageService.TempPath));

        // Verify no final file created
        string key = StorageKeyHelper.BuildStorageKey(request.ClientId, request.BackupSetId, request.FileId, request.VersionNumber);
        _storageService.TryResolvePhysicalPath(key, out var finalPath, out _);
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task PrematureStreamTermination_DoesNotCreateFinalFile()
    {
        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 1000,
            ExpectedSha256 = new string('a', 64)
        };

        // Stream only provides 50 bytes instead of 1000
        byte[] truncatedBytes = new byte[50];
        using var stream = new MemoryStream(truncatedBytes);

        var result = await _storageService.WriteUploadedFileAsync(request, stream, 1000, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.TransferInterrupted, result.ErrorCode);

        // Temp file must be cleaned up
        Assert.Empty(Directory.GetFiles(_storageService.TempPath));

        // Final file must not exist
        string key = StorageKeyHelper.BuildStorageKey(request.ClientId, request.BackupSetId, request.FileId, request.VersionNumber);
        _storageService.TryResolvePhysicalPath(key, out var finalPath, out _);
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task ExistingIdenticalVersion_ReturnsIdempotentSuccess()
    {
        byte[] payload = Encoding.UTF8.GetBytes("Idempotent test content");
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = payload.Length,
            ExpectedSha256 = hash
        };

        // 1. First upload
        using (var stream1 = new MemoryStream(payload))
        {
            var res1 = await _storageService.WriteUploadedFileAsync(request, stream1, payload.Length, CancellationToken.None);
            Assert.True(res1.Success);
        }

        // 2. Second upload with a new OperationId for the exact same file version and content
        var retryRequest = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = request.ClientId,
            BackupSetId = request.BackupSetId,
            FileId = request.FileId,
            VersionNumber = 1,
            ExpectedSize = payload.Length,
            ExpectedSha256 = hash
        };

        using (var stream2 = new MemoryStream(payload))
        {
            var res2 = await _storageService.WriteUploadedFileAsync(retryRequest, stream2, payload.Length, CancellationToken.None);
            Assert.True(res2.Success);
            Assert.Equal(res2.StorageKey, StorageKeyHelper.BuildStorageKey(request.ClientId, request.BackupSetId, request.FileId, 1));
            Assert.Equal(payload.Length, res2.ActualSize);
            Assert.Equal(hash, res2.ActualSha256);
        }

        // Temp folder must still be empty
        Assert.Empty(Directory.GetFiles(_storageService.TempPath));
    }

    [Fact]
    public async Task ExistingDifferentFile_ReturnsStorageConflict_AndDoesNotOverwrite()
    {
        byte[] original = Encoding.UTF8.GetBytes("Original content");
        string originalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();

        var request1 = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = original.Length,
            ExpectedSha256 = originalHash
        };

        using (var stream1 = new MemoryStream(original))
        {
            var res1 = await _storageService.WriteUploadedFileAsync(request1, stream1, original.Length, CancellationToken.None);
            Assert.True(res1.Success);
        }

        // Attempt to upload different content under the same version number
        byte[] conflicting = Encoding.UTF8.GetBytes("Conflicting different content");
        string conflictingHash = Convert.ToHexString(SHA256.HashData(conflicting)).ToLowerInvariant();

        var conflictRequest = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = request1.ClientId,
            BackupSetId = request1.BackupSetId,
            FileId = request1.FileId,
            VersionNumber = 1, // Same version!
            ExpectedSize = conflicting.Length,
            ExpectedSha256 = conflictingHash
        };

        using (var stream2 = new MemoryStream(conflicting))
        {
            var res2 = await _storageService.WriteUploadedFileAsync(conflictRequest, stream2, conflicting.Length, CancellationToken.None);
            Assert.False(res2.Success);
            Assert.Equal(StorageErrorCodes.StorageConflict, res2.ErrorCode);
        }

        // Verify original file on disk was NOT overwritten
        string key = StorageKeyHelper.BuildStorageKey(request1.ClientId, request1.BackupSetId, request1.FileId, 1);
        _storageService.TryResolvePhysicalPath(key, out var physicalPath, out _);
        byte[] onDisk = await File.ReadAllBytesAsync(physicalPath);
        Assert.Equal(original, onDisk);

        // Temp folder clean
        Assert.Empty(Directory.GetFiles(_storageService.TempPath));
    }

    [Fact]
    public async Task MissingStorageKey_ReturnsStorageNotFound()
    {
        var info = await _storageService.GetStoredFileInfoAsync("clients/non-existent/key.bin");

        Assert.False(info.Success);
        Assert.Equal(StorageErrorCodes.StorageNotFound, info.ErrorCode);
    }
}
