using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Backup.StorageService.Storage;

/// <summary>
/// Session-level message dispatcher for a single TCP connection.
/// Tracks per-connection state such as pending StoreFile requests and enforces
/// the rule that no interleaving or multiplexing is permitted.
/// </summary>
public class StorageMessageDispatcher
{
    private readonly IFileStorageService _storageService;
    private readonly ILogger<StorageMessageDispatcher> _logger;
    private readonly ProtocolFrameReader _reader;
    private readonly ProtocolFrameWriter _writer;

    private StoreFileRequest? _pendingStoreRequest;
    private Guid? _pendingStoreMessageId;

    public StorageMessageDispatcher(
        IFileStorageService storageService,
        ILogger<StorageMessageDispatcher> logger)
    {
        _storageService = storageService;
        _logger = logger;
        _reader = new ProtocolFrameReader();
        _writer = new ProtocolFrameWriter();
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
                _logger.LogWarning("[StorageDispatcher] Protocol error while reading header: {Message}", pe.Message);
                var err = new ErrorMessage { Code = StorageErrorCodes.InvalidStorageRequest, Message = pe.Message };
                await _writer.WriteJsonFrameAsync(stream, MessageType.Error, err, cancellationToken: cancellationToken);
                break;
            }

            if (header == null)
            {
                // Normal client disconnect
                break;
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

            case MessageType.StoreFile:
                await HandleStoreFileAsync(stream, header, cancellationToken);
                break;

            case MessageType.UploadFile:
                await HandleUploadFileAsync(stream, header, cancellationToken);
                break;

            case MessageType.GetStoredFile:
                await HandleGetStoredFileAsync(stream, header, cancellationToken);
                break;

            default:
                _logger.LogWarning("[StorageDispatcher] Unhandled MessageType: {MessageType}", header.MessageType);
                if (header.BodyLength > 0)
                {
                    await _reader.ReadBodyToStreamAsync(stream, Stream.Null, header.BodyLength, cancellationToken);
                }

                var errorMsg = new ErrorMessage
                {
                    Code = StorageErrorCodes.InvalidStorageRequest,
                    Message = $"MessageType '{header.MessageType}' is not supported by StorageService."
                };
                await _writer.WriteJsonFrameAsync(stream, MessageType.Error, errorMsg, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandleHelloAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var req = await _reader.ReadJsonBodyAsync<HelloRequest>(stream, header.BodyLength, cancellationToken);
        _logger.LogInformation("[StorageDispatcher] Received Hello from '{ClientName}'", req?.ClientName ?? "Unknown");

        var resp = new HelloResponse
        {
            ServerName = "Backup.StorageService",
            Version = "1.0",
            Success = true,
            Message = "StorageService is ready."
        };

        await _writer.WriteJsonFrameAsync(stream, MessageType.HelloAck, resp, correlationId: header.MessageId, cancellationToken: cancellationToken);
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

    private async Task HandleStoreFileAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<StoreFileRequest>(stream, header.BodyLength, cancellationToken);

        if (request == null)
        {
            var err = new StoreFileReadyResponse
            {
                OperationId = Guid.Empty,
                Accepted = false,
                ErrorCode = StorageErrorCodes.InvalidStorageRequest,
                ErrorMessage = "StoreFile payload cannot be null or empty."
            };
            await _writer.WriteJsonFrameAsync(stream, MessageType.StoreFileReady, err, correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Check if an upload is already pending on this connection (Enforce no interleaving)
        if (_pendingStoreRequest != null)
        {
            _logger.LogWarning("[StorageDispatcher] Rejected StoreFile: connection already has an active pending upload.");
            var conflict = new StoreFileReadyResponse
            {
                OperationId = request.OperationId,
                Accepted = false,
                ErrorCode = StorageErrorCodes.InvalidStorageRequest,
                ErrorMessage = "Another upload operation is already pending on this connection. Multiplexing / interleaving is not supported."
            };
            await _writer.WriteJsonFrameAsync(stream, MessageType.StoreFileReady, conflict, correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Validate request parameters
        string? validationError = null;
        string? errorCode = null;

        if (request.OperationId == Guid.Empty)
        {
            validationError = "OperationId cannot be an empty GUID.";
            errorCode = StorageErrorCodes.InvalidStorageRequest;
        }
        else if (request.ClientId == Guid.Empty)
        {
            validationError = "ClientId cannot be an empty GUID.";
            errorCode = StorageErrorCodes.InvalidStorageRequest;
        }
        else if (request.BackupSetId == Guid.Empty)
        {
            validationError = "BackupSetId cannot be an empty GUID.";
            errorCode = StorageErrorCodes.InvalidStorageRequest;
        }
        else if (request.FileId == Guid.Empty)
        {
            validationError = "FileId cannot be an empty GUID.";
            errorCode = StorageErrorCodes.InvalidStorageRequest;
        }
        else if (request.VersionNumber <= 0)
        {
            validationError = $"VersionNumber must be greater than zero, got {request.VersionNumber}.";
            errorCode = StorageErrorCodes.InvalidStorageRequest;
        }
        else if (request.ExpectedSize < 0)
        {
            validationError = $"ExpectedSize must be non-negative, got {request.ExpectedSize}.";
            errorCode = StorageErrorCodes.InvalidStorageRequest;
        }
        else if (!StorageKeyHelper.TryValidateAndNormalizeSha256(request.ExpectedSha256, out var normalizedSha, out var hashError))
        {
            validationError = hashError ?? "Invalid SHA-256 hash.";
            errorCode = StorageErrorCodes.InvalidHash;
        }
        else
        {
            request.ExpectedSha256 = normalizedSha;
        }

        if (validationError != null)
        {
            _logger.LogWarning("[StorageDispatcher] StoreFile validation failed for Op {OperationId}: {Error}",
                request.OperationId, validationError);

            var rejectResponse = new StoreFileReadyResponse
            {
                OperationId = request.OperationId,
                Accepted = false,
                ErrorCode = errorCode ?? StorageErrorCodes.InvalidStorageRequest,
                ErrorMessage = validationError
            };
            await _writer.WriteJsonFrameAsync(stream, MessageType.StoreFileReady, rejectResponse, correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // Accepted: store in session state and await UploadFile
        _pendingStoreRequest = request;
        _pendingStoreMessageId = header.MessageId;

        _logger.LogInformation("[StorageDispatcher] Accepted StoreFile for Op {OperationId}, awaiting binary stream ({Size} bytes)",
            request.OperationId, request.ExpectedSize);

        var readyResponse = new StoreFileReadyResponse
        {
            OperationId = request.OperationId,
            Accepted = true
        };
        await _writer.WriteJsonFrameAsync(stream, MessageType.StoreFileReady, readyResponse, correlationId: header.MessageId, cancellationToken: cancellationToken);
    }

    private async Task HandleUploadFileAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        if (_pendingStoreRequest == null)
        {
            _logger.LogWarning("[StorageDispatcher] Received UploadFile without a preceding accepted StoreFile request.");

            // Drain payload if any
            if (header.BodyLength > 0)
            {
                await _reader.ReadBodyToStreamAsync(stream, Stream.Null, header.BodyLength, cancellationToken);
            }

            var err = new StoreFileResultResponse
            {
                OperationId = Guid.Empty,
                Success = false,
                ErrorCode = StorageErrorCodes.InvalidStorageRequest,
                ErrorMessage = "No active StoreFile handshake found on this connection."
            };
            await _writer.WriteJsonFrameAsync(stream, MessageType.StoreFileResult, err, correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        var currentRequest = _pendingStoreRequest;
        var correlationId = _pendingStoreMessageId ?? header.MessageId;

        // Clear session state so next operation can proceed regardless of outcome
        _pendingStoreRequest = null;
        _pendingStoreMessageId = null;

        // Verify body length against expected size
        if (header.BodyLength != currentRequest.ExpectedSize)
        {
            _logger.LogWarning("[StorageDispatcher] BodyLength {BodyLength} != ExpectedSize {ExpectedSize}",
                header.BodyLength, currentRequest.ExpectedSize);

            if (header.BodyLength > 0)
            {
                await _reader.ReadBodyToStreamAsync(stream, Stream.Null, header.BodyLength, cancellationToken);
            }

            var sizeError = new StoreFileResultResponse
            {
                OperationId = currentRequest.OperationId,
                Success = false,
                ActualSize = header.BodyLength,
                ErrorCode = StorageErrorCodes.SizeMismatch,
                ErrorMessage = $"UploadFile frame BodyLength ({header.BodyLength}) does not match ExpectedSize ({currentRequest.ExpectedSize})."
            };
            await _writer.WriteJsonFrameAsync(stream, MessageType.StoreFileResult, sizeError, correlationId: correlationId, cancellationToken: cancellationToken);
            return;
        }

        // Stream and verify file
        StoreFileResultResponse result;
        try
        {
            result = await _storageService.WriteUploadedFileAsync(currentRequest, stream, header.BodyLength, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorageDispatcher] Error during WriteUploadedFileAsync for Op {OperationId}", currentRequest.OperationId);
            result = new StoreFileResultResponse
            {
                OperationId = currentRequest.OperationId,
                Success = false,
                ErrorCode = StorageErrorCodes.StorageIoError,
                ErrorMessage = $"Storage service error: {ex.Message}"
            };
        }

        await _writer.WriteJsonFrameAsync(stream, MessageType.StoreFileResult, result, correlationId: correlationId, cancellationToken: cancellationToken);
    }

    private async Task HandleGetStoredFileAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken)
    {
        var request = await _reader.ReadJsonBodyAsync<GetStoredFileRequest>(stream, header.BodyLength, cancellationToken);

        if (request == null || string.IsNullOrWhiteSpace(request.StorageKey))
        {
            var err = new GetStoredFileResultResponse
            {
                StorageKey = string.Empty,
                Success = false,
                ErrorCode = StorageErrorCodes.InvalidStorageRequest,
                ErrorMessage = "StorageKey cannot be null or empty."
            };
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetStoredFileResult, err, correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        var info = await _storageService.GetStoredFileInfoAsync(request.StorageKey, cancellationToken);

        if (!info.Success)
        {
            await _writer.WriteJsonFrameAsync(stream, MessageType.GetStoredFileResult, info, correlationId: header.MessageId, cancellationToken: cancellationToken);
            return;
        }

        // File exists and verified. Send GetStoredFileResult descriptor first.
        await _writer.WriteJsonFrameAsync(stream, MessageType.GetStoredFileResult, info, correlationId: header.MessageId, cancellationToken: cancellationToken);

        // Then stream binary payload using StoredFile frame
        if (!_storageService.TryResolvePhysicalPath(request.StorageKey, out var physicalPath, out var resolveErr))
        {
            return;
        }

        await using var fileStream = _storageService.OpenStoredFileReadStream(physicalPath);
        var storedFileHeader = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = MessageType.StoredFile,
            MessageId = Guid.NewGuid(),
            CorrelationId = header.MessageId,
            ContentType = ProtocolContentTypes.ApplicationOctetStream,
            BodyLength = info.Size,
            SentAtUtc = DateTime.UtcNow
        };

        await _writer.WriteFrameAsync(stream, storedFileHeader, fileStream, cancellationToken);
    }
}
