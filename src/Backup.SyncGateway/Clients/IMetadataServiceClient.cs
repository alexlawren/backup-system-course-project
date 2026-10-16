using Backup.Shared.Messages;

namespace Backup.SyncGateway.Clients;

public interface IMetadataServiceClient : IAsyncDisposable
{
    void Disconnect();
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<HelloResponse?> HelloAsync(string clientName = "Backup.SyncGateway", CancellationToken cancellationToken = default);
    Task<RegisterClientResponse> RegisterClientAsync(RegisterClientRequest request, CancellationToken cancellationToken = default);
    Task<CreateBackupSetResponse> CreateBackupSetAsync(CreateBackupSetRequest request, CancellationToken cancellationToken = default);
    Task<PrepareVersionResponse> PrepareVersionAsync(PrepareVersionRequest request, CancellationToken cancellationToken = default);
    Task<CommitVersionResponse> CommitVersionAsync(CommitVersionRequest request, CancellationToken cancellationToken = default);
    Task<DeleteFileMetadataResponse> DeleteFileMetadataAsync(DeleteFileMetadataRequest request, CancellationToken cancellationToken = default);
    Task<RenameFileMetadataResponse> RenameFileMetadataAsync(RenameFileMetadataRequest request, CancellationToken cancellationToken = default);
    Task<GetFilesResponse> GetFilesAsync(GetFilesRequest request, CancellationToken cancellationToken = default);
    Task<GetVersionsResponse> GetVersionsAsync(GetVersionsRequest request, CancellationToken cancellationToken = default);
    Task<ResolveVersionResponse> ResolveVersionAsync(ResolveVersionRequest request, CancellationToken cancellationToken = default);
    Task<GetChangeLogResponse> GetChangeLogsAsync(GetChangeLogRequest request, CancellationToken cancellationToken = default);
    Task<JoinSharedBackupSetResponse> JoinSharedBackupSetAsync(JoinSharedBackupSetRequest request, CancellationToken cancellationToken = default);
    Task<GetBackupSetMembersResponse> GetBackupSetMembersAsync(GetBackupSetMembersRequest request, CancellationToken cancellationToken = default);
    Task<GetBackupSetManifestResponse> GetBackupSetManifestAsync(GetBackupSetManifestRequest request, CancellationToken cancellationToken = default);
}
