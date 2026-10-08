using System.Security.Cryptography;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Storage;
using Backup.StorageService.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.StorageService.Storage;

public class FileStorageService : IFileStorageService
{
    private const int BufferSize = 65536; // 64 KiB bounded buffer

    private readonly ILogger<FileStorageService> _logger;
    private readonly string _rootPath;
    private readonly string _tempPath;

    public string RootPath => _rootPath;
    public string TempPath => _tempPath;

    public FileStorageService(
        ILogger<FileStorageService> logger,
        IOptions<StorageOptions> options)
    {
        _logger = logger;
        _rootPath = ResolveStorageRoot(options.Value.RootPath);
        _tempPath = Path.Combine(_rootPath, ".tmp");
    }

    /// <summary>
    /// Constructor accepting an explicit root path (primarily for unit and integration testing).
    /// </summary>
    public FileStorageService(
        ILogger<FileStorageService> logger,
        string rootPath)
    {
        _logger = logger;
        _rootPath = ResolveStorageRoot(rootPath);
        _tempPath = Path.Combine(_rootPath, ".tmp");
    }

    public void Initialize()
    {
        if (!Directory.Exists(_rootPath))
        {
            _logger.LogInformation("[FileStorageService] Creating root storage directory: {RootPath}", _rootPath);
            Directory.CreateDirectory(_rootPath);
        }

        if (!Directory.Exists(_tempPath))
        {
            _logger.LogInformation("[FileStorageService] Creating temporary storage directory: {TempPath}", _tempPath);
            Directory.CreateDirectory(_tempPath);
        }
    }

    public async Task<StoreFileResultResponse> WriteUploadedFileAsync(
        StoreFileRequest request,
        Stream sourceStream,
        long streamLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceStream);

        Initialize();

        if (streamLength != request.ExpectedSize)
        {
            _logger.LogWarning("[FileStorageService] Stream length {StreamLength} does not match expected size {ExpectedSize}",
                streamLength, request.ExpectedSize);

            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ErrorCode = StorageErrorCodes.SizeMismatch,
                ErrorMessage = $"Uploaded body length ({streamLength}) does not match expected size ({request.ExpectedSize})."
            };
        }

        string tempFilePath = Path.Combine(_tempPath, $"{request.OperationId:N}.part");
        string actualSha256;
        long actualSize = 0;

        try
        {
            await using (var fileStream = new FileStream(
                tempFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[BufferSize];
                long remaining = streamLength;

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = await sourceStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read == 0)
                    {
                        throw new ProtocolException($"Unexpected EOF while receiving file upload. Expected {remaining} more bytes.");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer.AsSpan(0, read));
                    remaining -= read;
                    actualSize += read;
                }

                await fileStream.FlushAsync(cancellationToken);

                byte[] hashBytes = hasher.GetHashAndReset();
                actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileStorageService] Upload failed for OperationId {OperationId}. Cleaning up temporary file.", request.OperationId);
            SafeDeleteFile(tempFilePath);

            if (ex is ProtocolException pe)
            {
                return new StoreFileResultResponse
                {
                    OperationId = request.OperationId,
                    Success = false,
                    ErrorCode = StorageErrorCodes.TransferInterrupted,
                    ErrorMessage = pe.Message
                };
            }

            throw;
        }

        // Verify size
        if (actualSize != request.ExpectedSize)
        {
            _logger.LogWarning("[FileStorageService] Size mismatch for OperationId {OperationId}: actual {Actual}, expected {Expected}",
                request.OperationId, actualSize, request.ExpectedSize);
            SafeDeleteFile(tempFilePath);

            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ActualSize = actualSize,
                ErrorCode = StorageErrorCodes.SizeMismatch,
                ErrorMessage = $"Actual file size ({actualSize}) does not match expected size ({request.ExpectedSize})."
            };
        }

        // Verify SHA-256
        StorageKeyHelper.TryValidateAndNormalizeSha256(request.ExpectedSha256, out var expectedSha, out _);
        if (!string.Equals(actualSha256, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[FileStorageService] SHA-256 mismatch for OperationId {OperationId}: actual {Actual}, expected {Expected}",
                request.OperationId, actualSha256, expectedSha);
            SafeDeleteFile(tempFilePath);

            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ActualSize = actualSize,
                ActualSha256 = actualSha256,
                ErrorCode = StorageErrorCodes.HashMismatch,
                ErrorMessage = $"Actual SHA-256 ({actualSha256}) does not match expected SHA-256 ({expectedSha})."
            };
        }

        // Compute deterministic StorageKey
        string storageKey = StorageKeyHelper.BuildStorageKey(
            request.ClientId,
            request.BackupSetId,
            request.FileId,
            request.VersionNumber);

        if (!StorageKeyHelper.TryResolvePhysicalPath(_rootPath, storageKey, out var finalPhysicalPath, out var resolveError))
        {
            SafeDeleteFile(tempFilePath);
            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ErrorCode = StorageErrorCodes.InvalidStorageRequest,
                ErrorMessage = $"Could not resolve physical path for storage key: {resolveError}"
            };
        }

        // Check for existing version file (Idempotency / Immutability)
        if (File.Exists(finalPhysicalPath))
        {
            var existingInfo = new FileInfo(finalPhysicalPath);
            string existingSha256;
            await using (var existingFs = new FileStream(
                finalPhysicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous))
            {
                byte[] existingHashBytes = await SHA256.HashDataAsync(existingFs, cancellationToken);
                existingSha256 = Convert.ToHexString(existingHashBytes).ToLowerInvariant();
            }

            SafeDeleteFile(tempFilePath);

            if (existingInfo.Length == request.ExpectedSize &&
                string.Equals(existingSha256, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[FileStorageService] Existing identical version found for {StorageKey}. Idempotent success.", storageKey);
                return new StoreFileResultResponse
                {
                    OperationId = request.OperationId,
                    Success = true,
                    StorageKey = storageKey,
                    ActualSize = existingInfo.Length,
                    ActualSha256 = existingSha256
                };
            }

            _logger.LogWarning("[FileStorageService] Conflict for {StorageKey}. Final version file already exists with different contents.", storageKey);
            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ErrorCode = StorageErrorCodes.StorageConflict,
                ErrorMessage = $"Version file already exists with different content (Size: {existingInfo.Length}, Hash: {existingSha256}). Overwriting is not permitted."
            };
        }

        // Atomic commit: move temporary file to final path
        string? targetDir = Path.GetDirectoryName(finalPhysicalPath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        try
        {
            File.Move(tempFilePath, finalPhysicalPath, overwrite: false);
            _logger.LogInformation("[FileStorageService] Successfully committed {StorageKey} ({Size} bytes)", storageKey, actualSize);

            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = true,
                StorageKey = storageKey,
                ActualSize = actualSize,
                ActualSha256 = actualSha256
            };
        }
        catch (IOException ex)
        {
            SafeDeleteFile(tempFilePath);
            _logger.LogError(ex, "[FileStorageService] IO error during atomic move to {FinalPath}", finalPhysicalPath);

            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ErrorCode = StorageErrorCodes.StorageIoError,
                ErrorMessage = $"Failed to atomically commit file: {ex.Message}"
            };
        }
    }

    public async Task<GetStoredFileResultResponse> GetStoredFileInfoAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        if (!StorageKeyHelper.TryResolvePhysicalPath(_rootPath, storageKey, out var physicalPath, out var err))
        {
            return new GetStoredFileResultResponse
            {
                StorageKey = storageKey,
                Success = false,
                ErrorCode = StorageErrorCodes.StorageNotFound,
                ErrorMessage = err ?? "Invalid storage key."
            };
        }

        if (!File.Exists(physicalPath))
        {
            return new GetStoredFileResultResponse
            {
                StorageKey = storageKey,
                Success = false,
                ErrorCode = StorageErrorCodes.StorageNotFound,
                ErrorMessage = "File not found in storage."
            };
        }

        var info = new FileInfo(physicalPath);
        string sha256;
        await using (var fs = new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous))
        {
            byte[] hashBytes = await SHA256.HashDataAsync(fs, cancellationToken);
            sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        return new GetStoredFileResultResponse
        {
            StorageKey = storageKey,
            Success = true,
            Size = info.Length,
            Sha256 = sha256
        };
    }

    public FileStream OpenStoredFileReadStream(string physicalPath)
    {
        return new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);
    }

    public bool TryResolvePhysicalPath(string storageKey, out string physicalPath, out string? errorMessage)
    {
        return StorageKeyHelper.TryResolvePhysicalPath(_rootPath, storageKey, out physicalPath, out errorMessage);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    /// <summary>
    /// Resolves the storage root path predictably. If relative, locates the repository root
    /// containing BackupSystem.sln so files are never written inside bin/Debug/net9.0.
    /// </summary>
    public static string ResolveStorageRoot(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        // Search upwards for repository root containing BackupSystem.sln
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(current, configuredPath));
            }
            current = Directory.GetParent(current)?.FullName;
        }

        string? baseDir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            if (File.Exists(Path.Combine(baseDir, "BackupSystem.sln")))
            {
                return Path.GetFullPath(Path.Combine(baseDir, configuredPath));
            }
            baseDir = Directory.GetParent(baseDir)?.FullName;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
    }

    /// <summary>
    /// Scans ONLY the temporary upload directory (_tempPath / .tmp) for *.part files older than maxAge.
    /// Never touches or scans committed immutable files in clients/.../versions/.
    /// </summary>
    public int CleanupExpiredTempFiles(TimeSpan maxAge)
    {
        if (!Directory.Exists(_tempPath))
        {
            return 0;
        }

        int deletedCount = 0;
        var threshold = DateTime.UtcNow - maxAge;

        try
        {
            var tempFiles = Directory.EnumerateFiles(_tempPath, "*.part", SearchOption.TopDirectoryOnly);
            foreach (var filePath in tempFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.LastWriteTimeUtc < threshold)
                    {
                        File.Delete(filePath);
                        deletedCount++;
                        _logger.LogInformation("[FileStorageService] Deleted stale temporary upload file: '{File}' (Last modified: {Time})",
                            filePath, fileInfo.LastWriteTimeUtc);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FileStorageService] Failed to delete stale temporary file: '{File}'", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileStorageService] Failed to enumerate temporary files in '{TempPath}'", _tempPath);
        }

        return deletedCount;
    }
}
