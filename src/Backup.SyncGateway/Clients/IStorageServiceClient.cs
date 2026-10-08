using Backup.Shared.Messages;

namespace Backup.SyncGateway.Clients;

public interface IStorageServiceClient : IAsyncDisposable, IDisposable
{
    void Disconnect();
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<HelloResponse?> HelloAsync(string clientName = "Backup.SyncGateway", CancellationToken cancellationToken = default);
    Task<StoreFileResultResponse> StoreFileAsync(
        StoreFileRequest request,
        Stream content,
        CancellationToken cancellationToken = default);
    Task<GetStoredFileResultResponse> GetStoredFileAsync(
        GetStoredFileRequest request,
        Stream destinationStream,
        CancellationToken cancellationToken = default);
    Task<GetStoredFileResultResponse> StreamStoredFileAsync(
        GetStoredFileRequest request,
        Func<GetStoredFileResultResponse, Task<Stream>> prepareDestinationAsync,
        CancellationToken cancellationToken = default);
}
