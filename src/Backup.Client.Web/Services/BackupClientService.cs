using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Validation;

namespace Backup.Client.Web.Services;

public interface IBackupClientService
{
    Task<ClientState> GetIdentityAsync(CancellationToken cancellationToken = default);
    Task<bool> EnsureRegisteredAsync(CancellationToken cancellationToken = default);
    Task<CreateBackupSetResponse> CreateBackupSetAsync(string name, string localRootPath, CancellationToken cancellationToken = default);
    Task<CreateBackupSetResponse> CreateBackupSetAsync(string name, string localRootPath, BackupSetAccessMode accessMode, CancellationToken cancellationToken = default);
    Task<JoinSharedBackupSetResponse> JoinSharedBackupSetAsync(string shareCode, CancellationToken cancellationToken = default);
    Task<GetBackupSetMembersResponse> GetBackupSetMembersAsync(Guid backupSetId, CancellationToken cancellationToken = default);
    Task<UploadCompletedResponse> BackupSingleFileAsync(Guid backupSetId, string localRootPath, string absoluteFilePath, CancellationToken cancellationToken = default);
    Task<bool> PingGatewayAsync(CancellationToken cancellationToken = default);
    Task<GetFilesResponse> GetFilesAsync(Guid backupSetId, bool includeDeleted = true, CancellationToken cancellationToken = default);
    Task<GetVersionsResponse> GetVersionsAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task<GetChangeLogResponse> GetChangeLogsAsync(Guid backupSetId, int limit = 100, CancellationToken cancellationToken = default);
    Task<RestoreExecutionResult> RestoreVersionAsync(Guid backupSetId, Guid fileId, int versionNumber, CancellationToken cancellationToken = default);
    Task SwitchProfileAsync(SyncProfile profile, CancellationToken cancellationToken = default);
}

public class BackupClientService : IBackupClientService
{
    private readonly BackupGatewayClient _gatewayClient;
    private readonly IClientIdentityService _identityService;
    private readonly ISyncEngine? _syncEngine;

    public BackupClientService(
        BackupGatewayClient gatewayClient,
        IClientIdentityService identityService,
        ISyncEngine? syncEngine = null)
    {
        _gatewayClient = gatewayClient;
        _identityService = identityService;
        _syncEngine = syncEngine;
    }

    public async Task<ClientState> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        return await _identityService.GetStateAsync(cancellationToken);
    }

    public async Task<bool> PingGatewayAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await _identityService.GetStateAsync(cancellationToken);
            if (state?.ClientId.HasValue == true)
            {
                _gatewayClient.SetActiveClientId(state.ClientId.Value);
            }
            return await _gatewayClient.PingAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        var state = await _identityService.GetStateAsync(cancellationToken);

        var regRequest = new RegisterClientRequest
        {
            ClientId = state.ClientId,
            Name = state.ClientName,
            MachineName = state.MachineName
        };

        var response = await _gatewayClient.RegisterClientAsync(regRequest, cancellationToken);
        if (response.Success && response.ClientId != Guid.Empty)
        {
            await _identityService.SaveStateAsync(response.ClientId, state.MachineName, state.ClientName, cancellationToken);
            return true;
        }

        return false;
    }

    public Task<CreateBackupSetResponse> CreateBackupSetAsync(
        string name,
        string localRootPath,
        CancellationToken cancellationToken = default)
    {
        return CreateBackupSetAsync(name, localRootPath, BackupSetAccessMode.Private, cancellationToken);
    }

    public async Task<CreateBackupSetResponse> CreateBackupSetAsync(
        string name,
        string localRootPath,
        BackupSetAccessMode accessMode,
        CancellationToken cancellationToken = default)
    {
        var state = await _identityService.GetStateAsync(cancellationToken);
        if (state.ClientId == null)
        {
            bool registered = await EnsureRegisteredAsync(cancellationToken);
            if (!registered)
            {
                return new CreateBackupSetResponse
                {
                    Success = false,
                    ErrorMessage = "Client must be registered before creating a backup set."
                };
            }
            state = await _identityService.GetStateAsync(cancellationToken);
        }

        var request = new CreateBackupSetRequest
        {
            ClientId = state.ClientId!.Value,
            Name = name,
            LocalRootPath = localRootPath,
            AccessMode = accessMode
        };

        return await _gatewayClient.CreateBackupSetAsync(request, cancellationToken);
    }

    public async Task<JoinSharedBackupSetResponse> JoinSharedBackupSetAsync(string shareCode, CancellationToken cancellationToken = default)
    {
        var state = await _identityService.GetStateAsync(cancellationToken);
        if (state.ClientId == null)
        {
            await EnsureRegisteredAsync(cancellationToken);
            state = await _identityService.GetStateAsync(cancellationToken);
        }

        var req = new JoinSharedBackupSetRequest
        {
            ShareCode = shareCode,
            ClientId = state.ClientId
        };
        return await _gatewayClient.JoinSharedBackupSetAsync(req, cancellationToken);
    }

    public async Task<GetBackupSetMembersResponse> GetBackupSetMembersAsync(Guid backupSetId, CancellationToken cancellationToken = default)
    {
        var state = await _identityService.GetStateAsync(cancellationToken);
        var req = new GetBackupSetMembersRequest
        {
            BackupSetId = backupSetId,
            ClientId = state.ClientId
        };
        return await _gatewayClient.GetBackupSetMembersAsync(req, cancellationToken);
    }

    public async Task<UploadCompletedResponse> BackupSingleFileAsync(
        Guid backupSetId,
        string localRootPath,
        string absoluteFilePath,
        CancellationToken cancellationToken = default)
    {
        if (backupSetId == Guid.Empty)
        {
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.BackupSetNotFound,
                ErrorMessage = "BackupSetId cannot be empty."
            };
        }

        if (string.IsNullOrWhiteSpace(localRootPath) || !Directory.Exists(localRootPath))
        {
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.InvalidLocalFile,
                ErrorMessage = $"LocalRootPath '{localRootPath}' does not exist on this machine."
            };
        }

        if (string.IsNullOrWhiteSpace(absoluteFilePath) || !File.Exists(absoluteFilePath))
        {
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.InvalidLocalFile,
                ErrorMessage = $"File '{absoluteFilePath}' not found on local disk."
            };
        }

        // Verify containment inside LocalRootPath
        string fullRoot = Path.GetFullPath(localRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullFile = Path.GetFullPath(absoluteFilePath);

        if (!fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.InvalidLocalFile,
                ErrorMessage = $"File '{absoluteFilePath}' is outside the BackupSet root directory '{localRootPath}'."
            };
        }

        string relative = Path.GetRelativePath(localRootPath, absoluteFilePath);
        if (!ProtocolPathValidator.TryNormalizeAndValidate(relative, out var normalizedRelative, out var pathError))
        {
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.InvalidRelativePath,
                ErrorMessage = $"Path validation failed: {pathError}"
            };
        }

        var state = await _identityService.GetStateAsync(cancellationToken);
        if (state.ClientId == null)
        {
            await EnsureRegisteredAsync(cancellationToken);
            state = await _identityService.GetStateAsync(cancellationToken);
        }

        return await _gatewayClient.BackupFileAsync(
            backupSetId,
            normalizedRelative,
            absoluteFilePath,
            state.ClientId,
            cancellationToken);
    }

    public async Task<GetFilesResponse> GetFilesAsync(Guid backupSetId, bool includeDeleted = true, CancellationToken cancellationToken = default)
    {
        return await _gatewayClient.GetFilesAsync(new GetFilesRequest
        {
            BackupSetId = backupSetId,
            IncludeDeleted = includeDeleted
        }, cancellationToken);
    }

    public async Task<GetVersionsResponse> GetVersionsAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        return await _gatewayClient.GetVersionsAsync(new GetVersionsRequest
        {
            FileId = fileId
        }, cancellationToken);
    }

    public async Task<GetChangeLogResponse> GetChangeLogsAsync(Guid backupSetId, int limit = 100, CancellationToken cancellationToken = default)
    {
        return await _gatewayClient.GetChangeLogAsync(new GetChangeLogRequest
        {
            BackupSetId = backupSetId,
            Limit = limit
        }, cancellationToken);
    }

    public async Task<RestoreExecutionResult> RestoreVersionAsync(Guid backupSetId, Guid fileId, int versionNumber, CancellationToken cancellationToken = default)
    {
        if (_syncEngine == null)
        {
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                ErrorCode = "ENGINE_NOT_AVAILABLE",
                ErrorMessage = "SyncEngine is not available in current client context."
            };
        }

        return await _syncEngine.RestoreVersionAsync(backupSetId, fileId, versionNumber, cancellationToken);
    }

    public async Task SwitchProfileAsync(SyncProfile profile, CancellationToken cancellationToken = default)
    {
        if (_syncEngine != null)
        {
            await _syncEngine.SwitchProfileAsync(profile, cancellationToken);
        }
    }
}
