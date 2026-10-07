using System.Net.Sockets;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;

namespace Backup.SyncGateway.Clients;

/// <summary>
/// Client helper communicating with Backup.StorageService over TCP Protocol v1.
/// Implements streaming binary upload and download with bounded buffering.
/// </summary>
public class StorageServiceClient : IStorageServiceClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly ProtocolFrameReader _reader;
    private readonly ProtocolFrameWriter _writer;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public StorageServiceClient(string host, int port)
    {
        _host = host;
        _port = port;
        _reader = new ProtocolFrameReader();
        _writer = new ProtocolFrameWriter();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpClient != null && _tcpClient.Connected)
        {
            return;
        }

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_host, _port, cancellationToken);
        _stream = _tcpClient.GetStream();
    }

    public async Task<HelloResponse?> HelloAsync(string clientName = "Backup.SyncGateway", CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var req = new HelloRequest { ClientName = clientName, Version = "1.0" };
        var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.Hello, req, cancellationToken: cancellationToken);
        var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

        if (respHeader == null || respHeader.MessageType != MessageType.HelloAck)
        {
            return null;
        }

        return await _reader.ReadJsonBodyAsync<HelloResponse>(_stream!, respHeader.BodyLength, cancellationToken);
    }

    /// <summary>
    /// Executes the full StoreFile handshake:
    /// 1. Sends StoreFile descriptor frame.
    /// 2. Receives StoreFileReady frame.
    /// 3. Streams binary payload via UploadFile frame from <paramref name="content"/>.
    /// 4. Receives and returns StoreFileResultResponse.
    /// 
    /// Note on ownership: The caller retains full ownership of <paramref name="content"/>.
    /// The stream is NOT closed or disposed by this method.
    /// </summary>
    public async Task<StoreFileResultResponse> StoreFileAsync(
        StoreFileRequest request,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        await EnsureConnectedAsync(cancellationToken);

        // 1. Send StoreFile descriptor
        var storeFileHeader = await _writer.WriteJsonFrameAsync(
            _stream!,
            MessageType.StoreFile,
            request,
            cancellationToken: cancellationToken);

        // 2. Await StoreFileReady response
        var readyHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
        if (readyHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP stream before sending StoreFileReady.");
        }

        if (readyHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, readyHeader.BodyLength, cancellationToken);
            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ErrorCode = err?.Code ?? "ERROR",
                ErrorMessage = err?.Message ?? "Server returned error."
            };
        }

        if (readyHeader.MessageType != MessageType.StoreFileReady)
        {
            throw new ProtocolException($"Expected StoreFileReady, received {readyHeader.MessageType}.");
        }

        var readyResp = await _reader.ReadJsonBodyAsync<StoreFileReadyResponse>(_stream!, readyHeader.BodyLength, cancellationToken);
        if (readyResp == null || !readyResp.Accepted)
        {
            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ErrorCode = readyResp?.ErrorCode ?? "REJECTED",
                ErrorMessage = readyResp?.ErrorMessage ?? "StoreFile request rejected by server."
            };
        }

        // 3. Stream binary payload via UploadFile frame
        var uploadHeader = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = MessageType.UploadFile,
            MessageId = Guid.NewGuid(),
            CorrelationId = storeFileHeader.MessageId,
            ContentType = ProtocolContentTypes.ApplicationOctetStream,
            BodyLength = request.ExpectedSize,
            SentAtUtc = DateTime.UtcNow
        };

        await _writer.WriteFrameAsync(_stream!, uploadHeader, content, cancellationToken);

        // 4. Await StoreFileResult response
        var resultHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
        if (resultHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP stream before sending StoreFileResult.");
        }

        if (resultHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, resultHeader.BodyLength, cancellationToken);
            return new StoreFileResultResponse
            {
                OperationId = request.OperationId,
                Success = false,
                ErrorCode = err?.Code ?? "ERROR",
                ErrorMessage = err?.Message ?? "Server returned error."
            };
        }

        if (resultHeader.MessageType != MessageType.StoreFileResult)
        {
            throw new ProtocolException($"Expected StoreFileResult, received {resultHeader.MessageType}.");
        }

        var result = await _reader.ReadJsonBodyAsync<StoreFileResultResponse>(_stream!, resultHeader.BodyLength, cancellationToken);
        return result ?? new StoreFileResultResponse
        {
            OperationId = request.OperationId,
            Success = false,
            ErrorMessage = "Empty StoreFileResult response received."
        };
    }

    /// <summary>
    /// Retrieves a stored file blob from Backup.StorageService:
    /// 1. Sends GetStoredFile request.
    /// 2. Receives GetStoredFileResult descriptor containing size and SHA-256.
    /// 3. If found, streams the StoredFile binary body into <paramref name="destinationStream"/>.
    /// 
    /// Note on ownership: The caller retains full ownership of <paramref name="destinationStream"/>.
    /// The stream is NOT closed or disposed by this method.
    /// </summary>
    public async Task<GetStoredFileResultResponse> GetStoredFileAsync(
        GetStoredFileRequest request,
        Stream destinationStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destinationStream);

        await EnsureConnectedAsync(cancellationToken);

        // 1. Send GetStoredFile descriptor
        await _writer.WriteJsonFrameAsync(_stream!, MessageType.GetStoredFile, request, cancellationToken: cancellationToken);

        // 2. Receive GetStoredFileResult descriptor
        var resultHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
        if (resultHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP stream unexpectedly.");
        }

        if (resultHeader.MessageType == MessageType.Error)
        {
            var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, resultHeader.BodyLength, cancellationToken);
            return new GetStoredFileResultResponse
            {
                StorageKey = request.StorageKey,
                Success = false,
                ErrorCode = err?.Code ?? "ERROR",
                ErrorMessage = err?.Message ?? "Server returned error."
            };
        }

        if (resultHeader.MessageType != MessageType.GetStoredFileResult)
        {
            throw new ProtocolException($"Expected GetStoredFileResult, received {resultHeader.MessageType}.");
        }

        var result = await _reader.ReadJsonBodyAsync<GetStoredFileResultResponse>(_stream!, resultHeader.BodyLength, cancellationToken);
        if (result == null || !result.Success)
        {
            return result ?? new GetStoredFileResultResponse
            {
                StorageKey = request.StorageKey,
                Success = false,
                ErrorMessage = "File not found or empty result received."
            };
        }

        // 3. Receive StoredFile binary frame
        var storedHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
        if (storedHeader == null)
        {
            throw new ProtocolException("Remote host closed TCP stream before sending StoredFile payload.");
        }

        if (storedHeader.MessageType != MessageType.StoredFile)
        {
            throw new ProtocolException($"Expected StoredFile binary frame, received {storedHeader.MessageType}.");
        }

        await _reader.ReadBodyToStreamAsync(_stream!, destinationStream, storedHeader.BodyLength, cancellationToken);
        return result;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_tcpClient == null || !_tcpClient.Connected || _stream == null)
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
