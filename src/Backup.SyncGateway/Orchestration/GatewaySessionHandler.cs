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
                new RegisterClientResponse { Success = false, ErrorMessage = $"MetadataService unavailable: {ex.Message}" },
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

        try
        {
            var result = await _metadataClient.CreateBackupSetAsync(req, cancellationToken);
            await _writer.WriteJsonFrameAsync(stream, MessageType.CreateBackupSetResult, result, correlationId: header.MessageId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Failed to proxy CreateBackupSet to MetadataService");
            await _writer.WriteJsonFrameAsync(stream, MessageType.CreateBackupSetResult,
                new CreateBackupSetResponse { Success = false, ErrorMessage = $"MetadataService unavailable: {ex.Message}" },
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
                Sha256 = normalizedSha
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
                    ErrorCode = "PREPARE_REJECTED",
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
            PrepareMessageId = header.MessageId
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
            ActualSha256 = storeResult.ActualSha256 ?? activeOp.ExpectedSha256
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
