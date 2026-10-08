using System.Net.Sockets;
using Backup.Client.Web.Configuration;
using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Microsoft.Extensions.Options;

namespace Backup.Client.Web.Services;

public class BackupGatewayClient : IAsyncDisposable, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ProtocolFrameReader _reader;
    private readonly ProtocolFrameWriter _writer;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public BackupGatewayClient(string host, int port)
    {
        _host = host;
        _port = port;
        _reader = new ProtocolFrameReader();
        _writer = new ProtocolFrameWriter();
    }

    public BackupGatewayClient(IOptions<GatewayOptions> options)
        : this(options.Value.Host, options.Value.Port)
    {
    }

    public bool IsConnected => _tcpClient != null && _tcpClient.Connected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        _tcpClient?.Dispose();
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_host, _port, cancellationToken);
        _stream = _tcpClient.GetStream();
    }

    public async Task<HelloResponse?> HelloAsync(
        string clientName = "Backup.Client.Web",
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var req = new HelloRequest
        {
            ClientName = clientName,
            Version = "1.0",
            Role = "Client",
            MachineName = Environment.MachineName,
            ClientId = clientId
        };

        var reqHeader = await _writer.WriteJsonFrameAsync(_stream!, MessageType.Hello, req, cancellationToken: cancellationToken);
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

        if (respHeader == null || respHeader.MessageType != MessageType.HelloAck)
        {
            return null;
        }

        return await _reader.ReadJsonBodyAsync<HelloResponse>(_stream!, respHeader.BodyLength, cancellationToken);
    }

    public async Task<RegisterClientResponse> RegisterClientAsync(
        RegisterClientRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.RegisterClient, request, cancellationToken: cancellationToken);
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

        if (respHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP connection.");
        }

        if (respHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
            return new RegisterClientResponse
            {
                Success = false,
                ErrorMessage = err?.Message ?? "Registration failed."
            };
        }

        var result = await _reader.ReadJsonBodyAsync<RegisterClientResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        return result ?? new RegisterClientResponse { Success = false, ErrorMessage = "Empty RegisterClient response." };
    }

    public async Task<CreateBackupSetResponse> CreateBackupSetAsync(
        CreateBackupSetRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.CreateBackupSet, request, cancellationToken: cancellationToken);
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

        if (respHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP connection.");
        }

        if (respHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
            return new CreateBackupSetResponse
            {
                Success = false,
                ErrorMessage = err?.Message ?? "Failed to create backup set."
            };
        }

        var result = await _reader.ReadJsonBodyAsync<CreateBackupSetResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        return result ?? new CreateBackupSetResponse { Success = false, ErrorMessage = "Empty CreateBackupSet response." };
    }

    public async Task<SyncPlanResponse> SendManifestAsync(
        ManifestRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.Manifest, request, cancellationToken: cancellationToken);
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

        if (respHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP connection.");
        }

        if (respHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
            return new SyncPlanResponse
            {
                Success = false,
                ErrorMessage = err?.Message ?? "Manifest failed."
            };
        }

        if (respHeader.MessageType != MessageType.SyncPlan)
        {
            throw new ProtocolException($"Expected SyncPlan, but received '{respHeader.MessageType}'.");
        }

        var result = await _reader.ReadJsonBodyAsync<SyncPlanResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        return result ?? new SyncPlanResponse { Success = false, ErrorMessage = "Empty SyncPlan response." };
    }

    public async Task<FileDeletedResponse> DeleteFileAsync(
        FileDeletedRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.FileDeleted, request, cancellationToken: cancellationToken);
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

        if (respHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP connection.");
        }

        if (respHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
            return new FileDeletedResponse
            {
                Success = false,
                ErrorMessage = err?.Message ?? "File deletion failed."
            };
        }

        if (respHeader.MessageType != MessageType.FileDeleted)
        {
            throw new ProtocolException($"Expected FileDeleted response, but received '{respHeader.MessageType}'.");
        }

        var result = await _reader.ReadJsonBodyAsync<FileDeletedResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        return result ?? new FileDeletedResponse { Success = false, ErrorMessage = "Empty FileDeleted response." };
    }

    public async Task<FileRenamedResponse> RenameFileAsync(
        FileRenamedRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.FileRenamed, request, cancellationToken: cancellationToken);
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

        if (respHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP connection.");
        }

        if (respHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
            return new FileRenamedResponse
            {
                Success = false,
                ErrorMessage = err?.Message ?? "File rename failed."
            };
        }

        if (respHeader.MessageType != MessageType.FileRenamed)
        {
            throw new ProtocolException($"Expected FileRenamed response, but received '{respHeader.MessageType}'.");
        }

        var result = await _reader.ReadJsonBodyAsync<FileRenamedResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        return result ?? new FileRenamedResponse { Success = false, ErrorMessage = "Empty FileRenamed response." };
    }

    /// <summary>
    /// Executes the full E2E backup workflow for a single local file:
    /// 1. Computes streaming hash and length (or uses precomputed).
    /// 2. Sends FileChanged frame to Gateway.
    /// 3. If UploadRequired=false, completes immediately.
    /// 4. If UploadRequired=true, opens FileStream and sends UploadFile binary frame.
    /// 5. Awaits and returns UploadCompletedResponse.
    /// </summary>
    public async Task<UploadCompletedResponse> BackupFileAsync(
        Guid backupSetId,
        string normalizedRelativePath,
        string absoluteFilePath,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        string? precomputedSha256 = null,
        long? precomputedSize = null)
    {
        if (!File.Exists(absoluteFilePath))
        {
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.InvalidLocalFile,
                ErrorMessage = $"Local file '{absoluteFilePath}' not found."
            };
        }

        await EnsureConnectedAsync(cancellationToken);

        // 1. Calculate length and streaming SHA-256
        long fileLength = precomputedSize ?? FileStreamingHelper.GetFileLength(absoluteFilePath);
        string sha256 = !string.IsNullOrWhiteSpace(precomputedSha256)
            ? precomputedSha256
            : await FileStreamingHelper.ComputeFileSha256Async(absoluteFilePath, cancellationToken);

        var changeReq = new FileChangeRequest
        {
            BackupSetId = backupSetId,
            ClientId = clientId,
            RelativePath = normalizedRelativePath,
            Size = fileLength,
            Sha256 = sha256
        };

        // 2. Send FileChanged
        var fileChangedHeader = await _writer.WriteJsonFrameAsync(
            _stream!,
            MessageType.FileChanged,
            changeReq,
            cancellationToken: cancellationToken);

        // 3. Receive Gateway response (UploadRequired or UploadCompleted)
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
        if (respHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP stream while awaiting response to FileChanged.");
        }

        if (respHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = err?.Code ?? "ERROR",
                ErrorMessage = err?.Message ?? "Gateway returned error."
            };
        }

        if (respHeader.MessageType == MessageType.UploadCompleted)
        {
            // Already up-to-date! No upload required.
            var completedResp = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return completedResp ?? new UploadCompletedResponse { Success = true };
        }

        if (respHeader.MessageType != MessageType.UploadRequired)
        {
            throw new ProtocolException($"Expected UploadRequired or UploadCompleted, but received '{respHeader.MessageType}'.");
        }

        var uploadReq = await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        if (uploadReq == null || !uploadReq.UploadRequired)
        {
            return new UploadCompletedResponse
            {
                Success = true,
                FileId = uploadReq?.FileId,
                VersionNumber = uploadReq?.VersionNumber
            };
        }

        // 4. Verify file hasn't changed since initial hash
        long currentLength = FileStreamingHelper.GetFileLength(absoluteFilePath);
        if (currentLength != fileLength)
        {
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.FileChangedDuringPrepare,
                ErrorMessage = "Local file size changed between Prepare and Upload phases."
            };
        }

        // 5. Send UploadFile binary frame
        var uploadHeader = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = MessageType.UploadFile,
            MessageId = Guid.NewGuid(),
            CorrelationId = fileChangedHeader.MessageId,
            ContentType = ProtocolContentTypes.ApplicationOctetStream,
            BodyLength = fileLength,
            SentAtUtc = DateTime.UtcNow
        };

        await using (var fileStream = new FileStream(
            absoluteFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous))
        {
            await _writer.WriteFrameAsync(_stream!, uploadHeader, fileStream, cancellationToken);
        }

        // 6. Await UploadCompleted frame
        var finalHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
        if (finalHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP stream while awaiting UploadCompleted.");
        }

        if (finalHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, finalHeader.BodyLength, cancellationToken);
            return new UploadCompletedResponse
            {
                Success = false,
                ErrorCode = err?.Code ?? "ERROR",
                ErrorMessage = err?.Message ?? "Server returned error."
            };
        }

        if (finalHeader.MessageType != MessageType.UploadCompleted)
        {
            throw new ProtocolException($"Expected UploadCompleted, but received '{finalHeader.MessageType}'.");
        }

        var result = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(_stream!, finalHeader.BodyLength, cancellationToken);
        return result ?? new UploadCompletedResponse { Success = false, ErrorMessage = "Empty UploadCompleted response received." };
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || _stream == null)
        {
            await ConnectAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream != null)
        {
            await _stream.DisposeAsync();
            _stream = null;
        }

        _tcpClient?.Dispose();
        _tcpClient = null;
    }
}
