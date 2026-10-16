using System.Net.Sockets;
using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Storage;
using Backup.Shared.Streams;
using Backup.Shared.Validation;
using Backup.SyncGateway.Clients;
using Microsoft.Extensions.Logging;

namespace Backup.SyncGateway.Orchestration;

public class GatewaySessionHandler : IAsyncDisposable
{
    private readonly IMetadataServiceClient _metadataClient;
    private readonly IStorageServiceClient _storageClient;
    private readonly ILogger<GatewaySessionHandler> _logger;
    private readonly ProtocolFrameReader _reader;
    private readonly ProtocolFrameWriter _writer;
    private readonly GatewayClientSession _session;

    public GatewaySessionHandler(
        IMetadataServiceClient metadataClient,
        IStorageServiceClient storageClient,
        ILogger<GatewaySessionHandler> logger)
    {
        _metadataClient = metadataClient;
        _storageClient = storageClient;
        _logger = logger;
        _reader = new ProtocolFrameReader();
        _writer = new ProtocolFrameWriter();
        _session = new GatewayClientSession();
    }

    public async Task ProcessConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolHeader? header;
            try
            {
                header = await _reader.ReadHeaderAsync(stream, cancellationToken);
            }
            catch (ProtocolException pe)
            {
                _logger.LogWarning("[SyncGateway] Protocol error reading header: {Message}", pe.Message);
                var err = new ErrorMessage { Code = "PROTOCOL_ERROR", Message = pe.Message };
                await _writer.WriteJsonFrameAsync(stream, MessageType.Error, err, cancellationToken: cancellationToken);
                break;
            }

            if (header == null)
            {
                break; // Clean disconnect
            }

            await DispatchMessageAsync(stream, header, cancellationToken);
        }
    }

    private async Task DispatchMessageAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        switch (header.MessageType)
        {
            case MessageType.Hello:
                await HandleHelloAsync(stream, header, cancellationToken);
                break;

            case MessageType.Ping:
                await HandlePingAsync(stream, header, cancellationToken);
                break;

            case MessageType.RegisterClient:
                await HandleRegisterClientAsync(stream, header, cancellationToken);
                break;

            case MessageType.CreateBackupSet:
                await HandleCreateBackupSetAsync(stream, header, cancellationToken);
                break;

            case MessageType.FileChanged:
            case MessageType.FileCreated:
                await HandleFileChangedAsync(stream, header, cancellationToken);
                break;

            case MessageType.UploadFile:
                await HandleUploadFileAsync(stream, header, cancellationToken);
                break;

            case MessageType.Manifest:
                await HandleManifestAsync(stream, header, cancellationToken);
                break;

            case MessageType.FileDeleted:
                await HandleFileDeletedAsync(stream, header, cancellationToken);
                break;

            case MessageType.FileRenamed:
                await HandleFileRenamedAsync(stream, header, cancellationToken);
                break;

            case MessageType.GetFiles:
                await HandleGetFilesAsync(stream, header, cancellationToken);
                break;

            case MessageType.GetVersions:
                await HandleGetVersionsAsync(stream, header, cancellationToken);
                break;

            case MessageType.GetChangeLog:
                await HandleGetChangeLogAsync(stream, header, cancellationToken);
                break;

            case MessageType.RestoreRequest:
                await HandleRestoreRequestAsync(stream, header, cancellationToken);
                break;

            case MessageType.RestoreCompleted:
                await HandleRestoreCompletedAsync(stream, header, cancellationToken);
                break;

            case MessageType.JoinSharedBackupSet:
                await HandleJoinSharedBackupSetAsync(stream, header, cancellationToken);
                break;

            case MessageType.GetBackupSetMembers:
                await HandleGetBackupSetMembersAsync(stream, header, cancellationToken);
                break;

            case MessageType.GetBackupSetManifest:
                await HandleGetBackupSetManifestAsync(stream, header, cancellationToken);
                break;

            case MessageType.DownloadFileVersion:
                await HandleDownloadFileVersionAsync(stream, header, cancellationToken);
                break;

            default:
                _logger.LogWarning("[SyncGateway] Unhandled MessageType: {MessageType}", header.MessageType);
                if (header.BodyLength > 0)
                {
                    await _reader.ReadBodyToStreamAsync(stream, Stream.Null, header.BodyLength, cancellationToken);
                }

                var errorMsg = new ErrorMessage
                {
                    Code = "NOT_SUPPORTED",
                    Message = $"MessageType '{header.MessageType}' is not supported by SyncGateway."
                };
                await _writer.WriteJsonFrameAsync(stream, MessageType.Error, errorMsg, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandleHelloAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var req = await _reader.ReadJsonBodyAsync<HelloRequest>(stream, header.BodyLength, cancellationToken);
        _logger.LogInformation("[SyncGateway] Received Hello from '{ClientName}' (Role: {Role})",
            req?.ClientName ?? "Unknown", req?.Role ?? "Unknown");

        if (req?.ClientId != null && req.ClientId != Guid.Empty)
        {
            _session.ClientId = req.ClientId;
        }

        var ack = new HelloResponse
        {
            ServerName = "Backup.SyncGateway",
            Version = "1.0",
            Role = "SyncGateway",
            ServiceName = "SyncGateway",
            Success = true,
            Message = "SyncGateway is ready."
        };

        await _writer.WriteJsonFrameAsync(stream, MessageType.HelloAck, ack, correlationId: header.MessageId, cancellationToken: cancellationToken);
    }

    private async Task HandlePingAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        if (header.BodyLength > 0)
        {
            await _reader.ReadBodyBytesAsync(stream, header.BodyLength, cancellationToken);
        }

        var pong = new PingMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        await _writer.WriteJsonFrameAsync(stream, MessageType.Pong, pong, correlationId: header.MessageId, cancellationToken: cancellationToken);
    }

    private async Task HandleRegisterClientAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var req = await _reader.ReadJsonBodyAsync<RegisterClientRequest>(stream, header.BodyLength, cancellationToken);
        if (req == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.RegisterClientResult,
                new RegisterClientResponse { Success = false, ErrorMessage = "RegisterClient body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var result = await _metadataClient.RegisterClientAsync(req, cancellationToken);
            if (result.Success && result.ClientId != Guid.Empty)
            {
                _session.ClientId = result.ClientId;
            }

            await _writer.WriteJsonFrameAsync(stream, MessageType.RegisterClientResult, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to proxy RegisterClient to MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.RegisterClientResult,
                new RegisterClientResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCreateBackupSetAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var req = await _reader.ReadJsonBodyAsync<CreateBackupSetRequest>(stream, header.BodyLength, cancellationToken);
        if (req == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.CreateBackupSetResult,
                new CreateBackupSetResponse { Success = false, ErrorMessage = "CreateBackupSet body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (_session.ClientId.HasValue && (req.ClientId == Guid.Empty || req.ClientId != _session.ClientId.Value))
        {
            req.ClientId = _session.ClientId.Value;
        }

        try
        {
            var result = await _metadataClient.CreateBackupSetAsync(req, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.CreateBackupSetResult, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to proxy CreateBackupSet to MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.CreateBackupSetResult,
                new CreateBackupSetResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleFileChangedAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<FileChangeRequest>(stream, header.BodyLength, cancellationToken);

        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse { Success = false, ErrorCode = "INVALID_REQUEST", ErrorMessage = "Request body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Enforce: one active upload at a time per session
        if (_session.ActiveUpload != null)
        {
            _logger.LogWarning("[SyncGateway] Rejected FileChanged: another upload is currently active on this connection.");
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.UploadInProgress,
                    ErrorMessage = "Another file upload operation is already pending on this connection."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Validate basic parameters
        if (request.BackupSetId == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse { Success = false, ErrorCode = BackupErrorCodes.BackupSetNotFound, ErrorMessage = "BackupSetId cannot be empty." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.Size < 0)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse { Success = false, ErrorCode = "INVALID_SIZE", ErrorMessage = "Size cannot be negative." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.RelativePath, out var normalizedPath, out var pathError))
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse { Success = false, ErrorCode = BackupErrorCodes.InvalidRelativePath, ErrorMessage = pathError },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!StorageKeyHelper.TryValidateAndNormalizeSha256(request.Sha256, out var normalizedSha, out var hashError))
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse { Success = false, ErrorCode = BackupErrorCodes.HashMismatch, ErrorMessage = hashError },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Call MetadataService.PrepareVersion
        PrepareVersionResponse prepareResp;
        try
        {
            prepareResp = await _metadataClient.PrepareVersionAsync(new PrepareVersionRequest
            {
                BackupSetId = request.BackupSetId,
                RelativePath = normalizedPath,
                Size = request.Size,
                Sha256 = normalizedSha,
                ChangeOrigin = request.ChangeOrigin,
                ActorClientId = _session.ClientId ?? request.ClientId,
                ClientId = _session.ClientId ?? request.ClientId,
                ConflictOriginalPath = request.ConflictOriginalPath,
                ConflictOriginalVersion = request.ConflictOriginalVersion
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] PrepareVersion failed due to MetadataService error");
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.MetadataUnavailable,
                    ErrorMessage = $"MetadataService unavailable: {ex.Message}"
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!prepareResp.Success)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = prepareResp.ErrorCode ?? "PREPARE_REJECTED",
                    ErrorMessage = prepareResp.ErrorMessage ?? "PrepareVersion rejected by metadata service."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Check if upload is actually required
        if (!prepareResp.UploadRequired)
        {
            _logger.LogInformation("[SyncGateway] File '{Path}' is already up-to-date in metadata. Skipping upload.", normalizedPath);
            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = true,
                    FileId = prepareResp.FileId,
                    VersionNumber = prepareResp.ExpectedVersionNumber,
                    AlreadyCommitted = true
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Upload is required: record active upload context in session
        Guid clientId = prepareResp.ClientId != Guid.Empty
            ? prepareResp.ClientId
            : (request.ClientId ?? _session.ClientId ?? Guid.Empty);

        _session.ActiveUpload = new ActiveUploadContext
        {
            OperationId = prepareResp.OperationId!.Value,
            ClientId = clientId,
            BackupSetId = request.BackupSetId,
            FileId = prepareResp.FileId,
            VersionNumber = prepareResp.ExpectedVersionNumber,
            ExpectedSize = request.Size,
            ExpectedSha256 = normalizedSha,
            PrepareMessageId = header.MessageId,
            ActorClientId = _session.ClientId ?? request.ClientId,
            ChangeOrigin = request.ChangeOrigin,
            ConflictOriginalPath = request.ConflictOriginalPath,
            ConflictOriginalVersion = request.ConflictOriginalVersion
        };

        // Respond with UploadRequired
        var uploadReqResp = new UploadRequiredResponse
        {
            UploadRequired = true,
            OperationId = prepareResp.OperationId,
            FileId = prepareResp.FileId,
            VersionNumber = prepareResp.ExpectedVersionNumber
        };

        await _writer.WriteJsonFrameAsync(stream, MessageType.UploadRequired, uploadReqResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
    }

    private async Task HandleUploadFileAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        if (_session.ActiveUpload == null)
        {
            _logger.LogWarning("[SyncGateway] Received UploadFile without a preceding pending upload operation.");
            if (header.BodyLength > 0)
            {
                await _reader.ReadBodyToStreamAsync(stream, Stream.Null, header.BodyLength, cancellationToken);
            }

            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.UploadNotExpected,
                    ErrorMessage = "No active upload operation pending on this connection."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        var activeOp = _session.ActiveUpload;
        _session.ClearActiveUpload(); // Ensure state is cleared regardless of outcome

        if (header.BodyLength != activeOp.ExpectedSize)
        {
            _logger.LogWarning("[SyncGateway] UploadFile frame BodyLength ({BodyLength}) != ExpectedSize ({ExpectedSize})",
                header.BodyLength, activeOp.ExpectedSize);

            if (header.BodyLength > 0)
            {
                await _reader.ReadBodyToStreamAsync(stream, Stream.Null, header.BodyLength, cancellationToken);
            }

            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.SizeMismatch,
                    ErrorMessage = $"BodyLength ({header.BodyLength}) does not match expected size ({activeOp.ExpectedSize})."
                },
                correlationId: activeOp.PrepareMessageId, cancellationToken: cancellationToken);
            return;
        }

        // Stream Relay: wrap client stream into non-owning bounded stream of exactly BodyLength bytes
        var boundedStream = new BoundedReadStream(stream, header.BodyLength);

        var storeRequest = new StoreFileRequest
        {
            OperationId = activeOp.OperationId,
            ClientId = activeOp.ClientId,
            BackupSetId = activeOp.BackupSetId,
            FileId = activeOp.FileId,
            VersionNumber = activeOp.VersionNumber,
            ExpectedSize = activeOp.ExpectedSize,
            ExpectedSha256 = activeOp.ExpectedSha256
        };

        StoreFileResultResponse storeResult;
        try
        {
            storeResult = await _storageClient.StoreFileAsync(storeRequest, boundedStream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Error relaying file to StorageService");
            await DrainRemainingAsync(stream, boundedStream.RemainingBytes, cancellationToken);

            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.StorageUnavailable,
                    ErrorMessage = $"StorageService unavailable: {ex.Message}"
                },
                correlationId: activeOp.PrepareMessageId, cancellationToken: cancellationToken);
            return;
        }

        // If StorageService failed (e.g. hash mismatch, conflict)
        if (!storeResult.Success)
        {
            _logger.LogWarning("[SyncGateway] StorageService returned failure: {ErrorCode} - {ErrorMessage}",
                storeResult.ErrorCode, storeResult.ErrorMessage);

            await DrainRemainingAsync(stream, boundedStream.RemainingBytes, cancellationToken);

            await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
                new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = storeResult.ErrorCode ?? BackupErrorCodes.StorageUnavailable,
                    ErrorMessage = storeResult.ErrorMessage ?? "StorageService failed to commit physical file."
                },
                correlationId: activeOp.PrepareMessageId, cancellationToken: cancellationToken);
            return;
        }

        // Storage Succeeded! Now commit version in MetadataService with retry
        var commitRequest = new CommitVersionRequest
        {
            OperationId = activeOp.OperationId,
            StorageKey = storeResult.StorageKey,
            ActualSize = storeResult.ActualSize,
            ActualSha256 = storeResult.ActualSha256 ?? activeOp.ExpectedSha256,
            ActorClientId = activeOp.ActorClientId,
            ConflictOriginalPath = activeOp.ConflictOriginalPath,
            ConflictOriginalVersion = activeOp.ConflictOriginalVersion
        };

        var commitResponse = await CommitWithRetryAsync(commitRequest, cancellationToken);

        await _writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted,
            new UploadCompletedResponse
            {
                Success = commitResponse.Success,
                FileId = commitResponse.FileId != Guid.Empty ? commitResponse.FileId : activeOp.FileId,
                VersionNumber = commitResponse.VersionNumber > 0 ? commitResponse.VersionNumber : activeOp.VersionNumber,
                StorageKey = storeResult.StorageKey,
                AlreadyCommitted = commitResponse.AlreadyCommitted,
                ErrorCode = commitResponse.ErrorCode,
                ErrorMessage = commitResponse.ErrorMessage
            },
            correlationId: activeOp.PrepareMessageId, cancellationToken: cancellationToken);
    }

    private async Task<CommitVersionResponse> CommitWithRetryAsync(
        CommitVersionRequest request,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var resp = await _metadataClient.CommitVersionAsync(request, cancellationToken);
                return resp;
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ProtocolException)
            {
                _logger.LogWarning(ex, "[SyncGateway] CommitVersion attempt {Attempt}/{Max} failed: {Message}",
                    attempt, maxAttempts, ex.Message);

                if (attempt < maxAttempts)
                {
                    await Task.Delay(100 * attempt, cancellationToken);
                    try { await _metadataClient.ConnectAsync(cancellationToken); } catch { }
                }
            }
        }

        return new CommitVersionResponse
        {
            Success = false,
            ErrorCode = BackupErrorCodes.MetadataCommitUncertain,
            ErrorMessage = "Physical file was stored, but metadata commit could not be confirmed after multiple retries."
        };
    }

    private async Task HandleManifestAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<ManifestRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.SyncPlan,
                new SyncPlanResponse { Success = false, ErrorMessage = "Manifest body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.BackupSetId == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.SyncPlan,
                new SyncPlanResponse { Success = false, ErrorMessage = "BackupSetId cannot be empty." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.ClientId != Guid.Empty)
        {
            if (_session.ClientId == null)
            {
                _session.ClientId = request.ClientId;
            }
            else if (_session.ClientId != request.ClientId)
            {
                _logger.LogWarning("[SyncGateway] ClientId mismatch in Manifest. Session: {SessionClientId}, Request: {ReqClientId}", _session.ClientId, request.ClientId);
            }
        }

        try
        {
            var serverSnapshot = await _metadataClient.GetFilesAsync(
                new GetFilesRequest { BackupSetId = request.BackupSetId, IncludeDeleted = true },
                cancellationToken);

            if (!serverSnapshot.Success)
            {
                await _writer.WriteJsonFrameAsync(stream, MessageType.SyncPlan,
                    new SyncPlanResponse { Success = false, ErrorMessage = $"Failed to get server files: {serverSnapshot.ErrorMessage}" },
                    correlationId: header.MessageId, cancellationToken: cancellationToken);
                return;
            }

            var serverFilesDict = serverSnapshot.Files
                .ToDictionary(f => f.RelativePath, f => f, StringComparer.OrdinalIgnoreCase);

            var uploadList = new List<SyncPlanUploadItem>();
            var deleteList = new List<string>();
            int unchangedCount = 0;
            var localPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (request.Files != null)
            {
                foreach (var localFile in request.Files)
                {
                    if (!ProtocolPathValidator.TryNormalizeAndValidate(localFile.RelativePath, out var normLocalPath, out _))
                    {
                        continue;
                    }

                    localPaths.Add(normLocalPath);

                    if (!serverFilesDict.TryGetValue(normLocalPath, out var serverFile))
                    {
                        // LOCAL exists, SERVER missing -> UPLOAD NEW
                        uploadList.Add(new SyncPlanUploadItem
                        {
                            RelativePath = normLocalPath,
                            Reason = SyncPlanReasons.New
                        });
                    }
                    else if (serverFile.IsDeleted)
                    {
                        // LOCAL exists, SERVER IsDeleted == true -> UPLOAD SERVER_DELETED
                        uploadList.Add(new SyncPlanUploadItem
                        {
                            RelativePath = normLocalPath,
                            Reason = SyncPlanReasons.ServerDeleted
                        });
                    }
                    else
                    {
                        // LOCAL exists, SERVER active
                        bool hashMatches = !string.IsNullOrWhiteSpace(serverFile.CurrentSha256) &&
                                           string.Equals(serverFile.CurrentSha256, localFile.Sha256, StringComparison.OrdinalIgnoreCase);

                        if (hashMatches)
                        {
                            unchangedCount++;
                        }
                        else
                        {
                            uploadList.Add(new SyncPlanUploadItem
                            {
                                RelativePath = normLocalPath,
                                Reason = SyncPlanReasons.Changed
                            });
                        }
                    }
                }
            }

            // Server files not present locally
            foreach (var (serverPath, serverFile) in serverFilesDict)
            {
                if (!localPaths.Contains(serverPath))
                {
                    if (!serverFile.IsDeleted)
                    {
                        deleteList.Add(serverPath);
                    }
                }
            }

            var syncPlan = new SyncPlanResponse
            {
                BackupSetId = request.BackupSetId,
                Upload = uploadList.OrderBy(u => u.RelativePath, StringComparer.Ordinal).ToList(),
                Delete = deleteList.OrderBy(d => d, StringComparer.Ordinal).ToList(),
                UnchangedCount = unchangedCount,
                Success = true
            };

            _logger.LogInformation("[SyncGateway] Manifest processed for BackupSet {BackupSetId}: {UploadCount} to upload, {DeleteCount} to delete, {UnchangedCount} unchanged",
                request.BackupSetId, syncPlan.Upload.Count, syncPlan.Delete.Count, syncPlan.UnchangedCount);

            await _writer.WriteJsonFrameAsync(stream, MessageType.SyncPlan, syncPlan, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to build SyncPlan for BackupSet {BackupSetId}", request.BackupSetId);
            await _writer.WriteJsonFrameAsync(stream, MessageType.SyncPlan,
                new SyncPlanResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleFileDeletedAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<FileDeletedRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileDeleted,
                new FileDeletedResponse { Success = false, ErrorMessage = "FileDeleted body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.BackupSetId == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileDeleted,
                new FileDeletedResponse { Success = false, ErrorMessage = "BackupSetId cannot be empty." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.RelativePath, out var normalizedPath, out var pathError))
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileDeleted,
                new FileDeletedResponse { Success = false, ErrorMessage = pathError },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var delResp = await _metadataClient.DeleteFileMetadataAsync(new DeleteFileMetadataRequest
            {
                BackupSetId = request.BackupSetId,
                RelativePath = normalizedPath,
                ClientId = _session.ClientId ?? request.ClientId,
                ActorClientId = _session.ClientId ?? request.ClientId
            }, cancellationToken);

            var response = new FileDeletedResponse
            {
                FileId = delResp.FileId,
                Success = delResp.Success,
                ErrorMessage = delResp.ErrorMessage
            };

            await _writer.WriteJsonFrameAsync(stream, MessageType.FileDeleted, response, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Error handling FileDeleted for '{Path}'", normalizedPath);
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileDeleted,
                new FileDeletedResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleFileRenamedAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<FileRenamedRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileRenamed,
                new FileRenamedResponse { Success = false, ErrorMessage = "FileRenamed body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.BackupSetId == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileRenamed,
                new FileRenamedResponse { Success = false, ErrorMessage = "BackupSetId cannot be empty." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.OldRelativePath, out var oldNormalized, out var oldError))
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileRenamed,
                new FileRenamedResponse { Success = false, ErrorMessage = $"Old path error: {oldError}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.NewRelativePath, out var newNormalized, out var newError))
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileRenamed,
                new FileRenamedResponse { Success = false, ErrorMessage = $"New path error: {newError}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var renResp = await _metadataClient.RenameFileMetadataAsync(new RenameFileMetadataRequest
            {
                BackupSetId = request.BackupSetId,
                OldRelativePath = oldNormalized,
                NewRelativePath = newNormalized,
                ClientId = _session.ClientId ?? request.ClientId,
                ActorClientId = _session.ClientId ?? request.ClientId
            }, cancellationToken);

            var response = new FileRenamedResponse
            {
                FileId = renResp.FileId,
                Success = renResp.Success,
                ErrorMessage = renResp.ErrorMessage
            };

            await _writer.WriteJsonFrameAsync(stream, MessageType.FileRenamed, response, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Error handling FileRenamed for '{OldPath}' -> '{NewPath}'", oldNormalized, newNormalized);
            await _writer.WriteJsonFrameAsync(stream, MessageType.FileRenamed,
                new FileRenamedResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleGetFilesAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<GetFilesRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.FilesResponse,
                new GetFilesResponse { Success = false, ErrorMessage = "Request body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var result = await _metadataClient.GetFilesAsync(request, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.FilesResponse, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to retrieve files from MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.FilesResponse,
                new GetFilesResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleGetVersionsAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<GetVersionsRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.VersionsResponse,
                new GetVersionsResponse { Success = false, ErrorMessage = "Request body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var result = await _metadataClient.GetVersionsAsync(request, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.VersionsResponse, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to retrieve versions from MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.VersionsResponse,
                new GetVersionsResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleGetChangeLogAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<GetChangeLogRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.ChangeLogResponse,
                new GetChangeLogResponse { Success = false, ErrorMessage = "Request body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var result = await _metadataClient.GetChangeLogsAsync(request, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.ChangeLogResponse, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to retrieve changelog from MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.ChangeLogResponse,
                new GetChangeLogResponse { Success = false, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleRestoreRequestAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<RestoreRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse { Success = false, ErrorCode = "INVALID_REQUEST", ErrorMessage = "RestoreRequest body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (_session.ClientId.HasValue && request.ClientId != _session.ClientId.Value)
        {
            _logger.LogWarning("[SyncGateway] Restore rejected: ClientId {ReqClientId} does not match active session ClientId {SessionClientId}",
                request.ClientId, _session.ClientId.Value);
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse { Success = false, ErrorCode = "CLIENT_MISMATCH", ErrorMessage = "ClientId does not match active session." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.BackupSetId == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse { Success = false, ErrorCode = "INVALID_BACKUP_SET", ErrorMessage = "BackupSetId cannot be empty." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.FileId == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse { Success = false, ErrorCode = "INVALID_FILE_ID", ErrorMessage = "FileId cannot be empty." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (request.VersionNumber <= 0)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse { Success = false, ErrorCode = "INVALID_VERSION", ErrorMessage = "VersionNumber must be greater than 0." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // 1. Resolve version from MetadataService
        ResolveVersionResponse resolveResp;
        try
        {
            resolveResp = await _metadataClient.ResolveVersionAsync(new ResolveVersionRequest
            {
                FileId = request.FileId,
                VersionNumber = request.VersionNumber,
                ClientId = _session.ClientId ?? request.ClientId
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] ResolveVersion failed in MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse { Success = false, ErrorCode = "METADATA_UNAVAILABLE", ErrorMessage = $"MetadataService error: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!resolveResp.Success || resolveResp.Version == null)
        {
            _logger.LogWarning("[SyncGateway] Version resolution failed: {ErrorCode} - {ErrorMessage}", resolveResp.ErrorCode, resolveResp.ErrorMessage);
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse
                {
                    FileId = request.FileId,
                    VersionNumber = request.VersionNumber,
                    Success = false,
                    ErrorCode = resolveResp.ErrorCode ?? "VERSION_NOT_FOUND",
                    ErrorMessage = resolveResp.ErrorMessage ?? $"Version {request.VersionNumber} not found."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(resolveResp.Version.StorageKey))
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady,
                new RestoreReadyResponse
                {
                    FileId = request.FileId,
                    VersionNumber = request.VersionNumber,
                    Success = false,
                    ErrorCode = "RESTORE_STORAGE_MISSING",
                    ErrorMessage = $"StorageKey missing for version {request.VersionNumber}."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("[Restore] Streaming version {Version} of file '{Path}' to client {ClientId} (StorageKey: {StorageKey})",
            request.VersionNumber, resolveResp.RelativePath, request.ClientId, resolveResp.Version.StorageKey);

        // 2. Stream from StorageService to Client via bounded userspace buffer
        try
        {
            var getResult = await _storageClient.StreamStoredFileAsync(
                new GetStoredFileRequest { StorageKey = resolveResp.Version.StorageKey },
                async (storageResult) =>
                {
                    // 2a. Send RestoreReady descriptor frame
                    var ready = new RestoreReadyResponse
                    {
                        FileId = resolveResp.FileId,
                        RelativePath = resolveResp.RelativePath,
                        VersionNumber = resolveResp.Version.VersionNumber,
                        Size = storageResult.Size,
                        Sha256 = storageResult.Sha256 ?? resolveResp.Version.Sha256,
                        Success = true
                    };
                    await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, correlationId: header.MessageId, cancellationToken: cancellationToken);

                    // 2b. Write RestoreFile binary header frame
                    var restoreFileHeader = new ProtocolHeader
                    {
                        Magic = ProtocolConstants.Magic,
                        ProtocolVersion = ProtocolConstants.Version,
                        MessageType = MessageType.RestoreFile,
                        MessageId = Guid.NewGuid(),
                        CorrelationId = header.MessageId,
                        ContentType = ProtocolContentTypes.ApplicationOctetStream,
                        BodyLength = storageResult.Size,
                        SentAtUtc = DateTime.UtcNow
                    };
                    await _writer.WriteHeaderAsync(stream, restoreFileHeader, cancellationToken);

                    // Relay stream directly into client stream
                    return stream;
                },
                cancellationToken);

            if (!getResult.Success)
            {
                _logger.LogWarning("[SyncGateway] StorageService download failed: {ErrorCode} - {ErrorMessage}", getResult.ErrorCode, getResult.ErrorMessage);
            }
            else
            {
                _logger.LogInformation("[Restore] Streamed version {Version} of '{Path}' to client successfully ({Size} bytes)",
                    request.VersionNumber, resolveResp.RelativePath, getResult.Size);
            }
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Streaming restore payload failed: {Message}", ex.Message);
        }
    }

    private async Task HandleRestoreCompletedAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var notif = await _reader.ReadJsonBodyAsync<RestoreCompletedNotification>(stream, header.BodyLength, cancellationToken);
        if (notif != null)
        {
            _logger.LogInformation("[Restore] Client {ClientId} completed restore for File {FileId} (v{Source} -> v{New}) - Status: {Status}",
                notif.ClientId, notif.FileId, notif.RestoredFromVersion, notif.NewVersionNumber, notif.Status);
        }

        await _writer.WriteJsonFrameAsync(stream, MessageType.RestoreCompleted,
            new RestoreCompletedResponse { Success = true },
            correlationId: header.MessageId, cancellationToken: cancellationToken);
    }

    private async Task HandleJoinSharedBackupSetAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<JoinSharedBackupSetRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.JoinSharedBackupSetResult,
                new JoinSharedBackupSetResponse { Success = false, ErrorCode = BackupErrorCodes.InvalidInput, ErrorMessage = "Request body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (_session.ClientId.HasValue)
        {
            request.ClientId = _session.ClientId.Value;
        }

        if (!request.ClientId.HasValue || request.ClientId.Value == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.JoinSharedBackupSetResult,
                new JoinSharedBackupSetResponse { Success = false, ErrorCode = "CLIENT_REQUIRED", ErrorMessage = "ClientId is required to join a shared backup set." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var result = await _metadataClient.JoinSharedBackupSetAsync(request, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.JoinSharedBackupSetResult, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to join shared backup set via MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.JoinSharedBackupSetResult,
                new JoinSharedBackupSetResponse { Success = false, ErrorCode = BackupErrorCodes.MetadataUnavailable, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleGetBackupSetMembersAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<GetBackupSetMembersRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetMembersResult,
                new GetBackupSetMembersResponse { Success = false, ErrorCode = BackupErrorCodes.InvalidInput, ErrorMessage = "Request body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (_session.ClientId.HasValue)
        {
            request.ClientId = _session.ClientId.Value;
        }

        try
        {
            var result = await _metadataClient.GetBackupSetMembersAsync(request, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetMembersResult, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to get backup set members from MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetMembersResult,
                new GetBackupSetMembersResponse { Success = false, ErrorCode = BackupErrorCodes.MetadataUnavailable, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleGetBackupSetManifestAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<GetBackupSetManifestRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetManifestResult,
                new GetBackupSetManifestResponse { Success = false, ErrorCode = BackupErrorCodes.InvalidInput, ErrorMessage = "Request body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Authoritative ClientId from session
        if (_session.ClientId.HasValue)
        {
            request.ClientId = _session.ClientId.Value;
        }

        if (request.BackupSetId == Guid.Empty)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetManifestResult,
                new GetBackupSetManifestResponse { Success = false, ErrorCode = BackupErrorCodes.InvalidInput, ErrorMessage = "BackupSetId cannot be empty." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var result = await _metadataClient.GetBackupSetManifestAsync(request, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetManifestResult, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to get backup set manifest from MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetManifestResult,
                new GetBackupSetManifestResponse { Success = false, ErrorCode = BackupErrorCodes.MetadataUnavailable, ErrorMessage = $"MetadataService unavailable [{BackupErrorCodes.MetadataUnavailable}]: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleDownloadFileVersionAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<DownloadFileVersionRequest>(stream, header.BodyLength, cancellationToken);
        if (request == null)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady,
                new DownloadFileVersionReadyResponse { Success = false, ErrorCode = BackupErrorCodes.InvalidInput, ErrorMessage = "DownloadFileVersionRequest body cannot be null." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // ClientId validation
        if (_session.ClientId.HasValue && request.ClientId.HasValue && request.ClientId.Value != _session.ClientId.Value)
        {
            _logger.LogWarning("[SyncGateway] Download rejected: ClientId {ReqClientId} does not match active session ClientId {SessionClientId}",
                request.ClientId, _session.ClientId.Value);
            await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady,
                new DownloadFileVersionReadyResponse { Success = false, ErrorCode = BackupErrorCodes.AccessDenied, ErrorMessage = "ClientId does not match active session." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        var actorClientId = _session.ClientId ?? request.ClientId;

        if (request.BackupSetId == Guid.Empty || request.FileId == Guid.Empty || request.VersionNumber <= 0)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady,
                new DownloadFileVersionReadyResponse { Success = false, ErrorCode = BackupErrorCodes.InvalidInput, ErrorMessage = "Invalid BackupSetId, FileId or VersionNumber." },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // 1. Resolve version from MetadataService (enforces access control for BackupSet and ClientId)
        ResolveVersionResponse resolveResp;
        try
        {
            resolveResp = await _metadataClient.ResolveVersionAsync(new ResolveVersionRequest
            {
                FileId = request.FileId,
                VersionNumber = request.VersionNumber,
                ClientId = actorClientId
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] ResolveVersion failed in MetadataService during DownloadFileVersion");
            await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady,
                new DownloadFileVersionReadyResponse { Success = false, ErrorCode = BackupErrorCodes.MetadataUnavailable, ErrorMessage = $"MetadataService error: {ex.Message}" },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!resolveResp.Success || resolveResp.Version == null)
        {
            _logger.LogWarning("[SyncGateway] DownloadFileVersion resolution failed: {ErrorCode} - {ErrorMessage}", resolveResp.ErrorCode, resolveResp.ErrorMessage);
            await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady,
                new DownloadFileVersionReadyResponse
                {
                    BackupSetId = request.BackupSetId,
                    FileId = request.FileId,
                    VersionNumber = request.VersionNumber,
                    Success = false,
                    ErrorCode = resolveResp.ErrorCode ?? "VERSION_NOT_FOUND",
                    ErrorMessage = resolveResp.ErrorMessage ?? $"Version {request.VersionNumber} not found."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Cross-BackupSet check: verify that the resolved file belongs to the requested BackupSetId
        if (resolveResp.BackupSetId != request.BackupSetId)
        {
            _logger.LogWarning("[SyncGateway] Download rejected: file {FileId} belongs to set {ActualSet}, not requested set {ReqSet}",
                request.FileId, resolveResp.BackupSetId, request.BackupSetId);
            await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady,
                new DownloadFileVersionReadyResponse
                {
                    BackupSetId = request.BackupSetId,
                    FileId = request.FileId,
                    VersionNumber = request.VersionNumber,
                    Success = false,
                    ErrorCode = BackupErrorCodes.AccessDenied,
                    ErrorMessage = "File does not belong to specified BackupSet."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(resolveResp.Version.StorageKey))
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady,
                new DownloadFileVersionReadyResponse
                {
                    BackupSetId = request.BackupSetId,
                    FileId = request.FileId,
                    VersionNumber = request.VersionNumber,
                    Success = false,
                    ErrorCode = "STORAGE_KEY_MISSING",
                    ErrorMessage = $"StorageKey missing for version {request.VersionNumber}."
                },
                correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("[DownloadFileVersion] Streaming version {Version} of file '{Path}' to client {ClientId} (StorageKey: {StorageKey})",
            request.VersionNumber, resolveResp.RelativePath, actorClientId, resolveResp.Version.StorageKey);

        // 2. Stream from StorageService to Client via direct bounded streaming
        try
        {
            var getResult = await _storageClient.StreamStoredFileAsync(
                new GetStoredFileRequest { StorageKey = resolveResp.Version.StorageKey },
                async (storageResult) =>
                {
                    // 2a. Send DownloadFileVersionReady descriptor frame
                    var ready = new DownloadFileVersionReadyResponse
                    {
                        BackupSetId = request.BackupSetId,
                        FileId = resolveResp.FileId,
                        RelativePath = resolveResp.RelativePath,
                        VersionNumber = resolveResp.Version.VersionNumber,
                        Size = storageResult.Size,
                        Sha256 = storageResult.Sha256 ?? resolveResp.Version.Sha256,
                        Success = true
                    };
                    await _writer.WriteJsonFrameAsync(stream, MessageType.DownloadFileVersionReady, ready, correlationId: header.MessageId, cancellationToken: cancellationToken);

                    // 2b. Write DownloadFileVersionData binary header frame
                    var dataHeader = new ProtocolHeader
                    {
                        Magic = ProtocolConstants.Magic,
                        ProtocolVersion = ProtocolConstants.Version,
                        MessageType = MessageType.DownloadFileVersionData,
                        MessageId = Guid.NewGuid(),
                        CorrelationId = header.MessageId,
                        ContentType = ProtocolContentTypes.ApplicationOctetStream,
                        BodyLength = storageResult.Size,
                        SentAtUtc = DateTime.UtcNow
                    };
                    await _writer.WriteHeaderAsync(stream, dataHeader, cancellationToken);

                    // Relay stream directly into client stream
                    return stream;
                },
                cancellationToken);

            if (!getResult.Success)
            {
                _logger.LogWarning("[SyncGateway] StorageService download failed: {ErrorCode} - {ErrorMessage}", getResult.ErrorCode, getResult.ErrorMessage);
            }
            else
            {
                _logger.LogInformation("[DownloadFileVersion] Streamed version {Version} of '{Path}' to client successfully ({Size} bytes)",
                    request.VersionNumber, resolveResp.RelativePath, getResult.Size);
            }
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Stream failure during DownloadFileVersion for '{Path}'", resolveResp.RelativePath);
            throw;
        }
    }

    private static async Task DrainRemainingAsync(Stream stream, long remainingBytes, CancellationToken cancellationToken)
    {
        if (remainingBytes <= 0) return;

        byte[] buffer = new byte[Math.Min(65536, remainingBytes)];
        long left = remainingBytes;
        while (left > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, left);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0) break;
            left -= read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _metadataClient.DisposeAsync();
        await _storageClient.DisposeAsync();
    }
}
