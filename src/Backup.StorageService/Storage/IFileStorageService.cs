using Backup.Shared.Messages;

namespace Backup.StorageService.Storage;

public interface IFileStorageService
{
    string RootPath { get; }
    string TempPath { get; }

    void Initialize();

    Task<StoreFileResultResponse> WriteUploadedFileAsync(
        StoreFileRequest request,
        Stream sourceStream,
        long streamLength,
        CancellationToken cancellationToken);

    Task<GetStoredFileResultResponse> GetStoredFileInfoAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    FileStream OpenStoredFileReadStream(string physicalPath);

    bool TryResolvePhysicalPath(string storageKey, out string physicalPath, out string? errorMessage);
}
