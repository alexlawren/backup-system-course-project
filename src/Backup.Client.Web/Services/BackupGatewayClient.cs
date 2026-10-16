using System.Net.Sockets;
using Backup.Client.Web.Configuration;
using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Validation;
using Microsoft.Extensions.Options;

namespace Backup.Client.Web.Services;

public class BackupGatewayClient : IAsyncDisposable, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ProtocolFrameReader _reader;
    private readonly ProtocolFrameWriter _writer;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
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

    private static readonly TimeSpan[] ReconnectBackoffs =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    private bool _wasConnectedBefore;
    private Guid? _activeClientId;
    private string _clientName = "Backup.Client.Web";

    public Guid? ActiveClientId => _activeClientId;
    public event Action? Reconnected;

    public void SetActiveClientId(Guid? clientId)
    {
        if (clientId.HasValue && clientId != Guid.Empty)
        {
            _activeClientId = clientId;
        }
    }

    public void MarkDisconnected()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        try { _tcpClient?.Dispose(); } catch { }
        _tcpClient = null;
    }

    public bool IsConnected => _tcpClient != null && _tcpClient.Connected && _stream != null;

    private async Task ConnectInternalAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected && _stream != null)
        {
            return;
        }

        MarkDisconnected();

        bool wasConnected = _wasConnectedBefore;
        Exception? lastEx = null;

        for (int attempt = 0; attempt <= ReconnectBackoffs.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_host, _port, cancellationToken);
                _stream = _tcpClient.GetStream();

                if (_activeClientId.HasValue || wasConnected)
                {
                    var req = new HelloRequest
                    {
                        ClientName = _clientName,
                        Version = "1.0",
                        Role = "Client",
                        MachineName = Environment.MachineName,
                        ClientId = _activeClientId
                    };

                    await _writer.WriteJsonFrameAsync(_stream, MessageType.Hello, req, cancellationToken: cancellationToken);
                    var respHeader = await _reader.ReadHeaderAsync(_stream, cancellationToken);
                    if (respHeader == null || respHeader.MessageType != MessageType.HelloAck)
                    {
                        throw new ProtocolException($"Gateway handshake failed: unexpected response '{respHeader?.MessageType}'.");
                    }
                    if (respHeader.BodyLength > 0)
                    {
                        await _reader.ReadJsonBodyAsync<HelloResponse>(_stream, respHeader.BodyLength, cancellationToken);
                    }
                }

                _wasConnectedBefore = true;

                if (wasConnected)
                {
                    Reconnected?.Invoke();
                }

                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ProtocolException)
            {
                lastEx = ex;
                MarkDisconnected();

                if (attempt < ReconnectBackoffs.Length)
                {
                    await Task.Delay(ReconnectBackoffs[attempt], cancellationToken);
                }
            }
        }

        throw lastEx ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await ConnectInternalAsync(cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<HelloResponse?> HelloAsync(
        string clientName = "Backup.Client.Web",
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        _clientName = clientName;
        if (clientId.HasValue && clientId != Guid.Empty)
        {
            _activeClientId = clientId;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var req = new HelloRequest
            {
                ClientName = clientName,
                Version = "1.0",
                Role = "Client",
                MachineName = Environment.MachineName,
                ClientId = clientId ?? _activeClientId
            };

            var reqHeader = await _writer.WriteJsonFrameAsync(_stream!, MessageType.Hello, req, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null || respHeader.MessageType != MessageType.HelloAck)
            {
                MarkDisconnected();
                return null;
            }

            return await _reader.ReadJsonBodyAsync<HelloResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            await _writer.WriteControlFrameAsync(_stream!, MessageType.Ping, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
            if (respHeader == null)
            {
                MarkDisconnected();
                return false;
            }

            if (respHeader.BodyLength > 0)
            {
                await _reader.ReadBodyBytesAsync(_stream!, respHeader.BodyLength, cancellationToken);
            }

            return respHeader.MessageType == MessageType.Pong;
        }
        catch
        {
            MarkDisconnected();
            return false;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<RegisterClientResponse> RegisterClientAsync(
        RegisterClientRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.RegisterClient, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
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
            if (result != null && result.Success && result.ClientId != Guid.Empty)
            {
                _activeClientId = result.ClientId;
            }
            return result ?? new RegisterClientResponse { Success = false, ErrorMessage = "Empty RegisterClient response." };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<CreateBackupSetResponse> CreateBackupSetAsync(
        CreateBackupSetRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.CreateBackupSet, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
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
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<SyncPlanResponse> SendManifestAsync(
        ManifestRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.Manifest, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
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
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<FileDeletedResponse> DeleteFileAsync(
        FileDeletedRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.FileDeleted, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
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
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<FileRenamedResponse> RenameFileAsync(
        FileRenamedRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.FileRenamed, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
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
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
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
        long? precomputedSize = null,
        BackupChangeOrigin changeOrigin = BackupChangeOrigin.Normal)
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

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
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
                Sha256 = sha256,
                ChangeOrigin = changeOrigin
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
                MarkDisconnected();
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
            var completedHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
            if (completedHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP stream while awaiting UploadCompleted.");
            }

            if (completedHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, completedHeader.BodyLength, cancellationToken);
                return new UploadCompletedResponse
                {
                    Success = false,
                    ErrorCode = err?.Code ?? "ERROR",
                    ErrorMessage = err?.Message ?? "Server returned error during commit."
                };
            }

            if (completedHeader.MessageType != MessageType.UploadCompleted)
            {
                throw new ProtocolException($"Expected UploadCompleted, but received '{completedHeader.MessageType}'.");
            }

            var finalResult = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(_stream!, completedHeader.BodyLength, cancellationToken);
            return finalResult ?? new UploadCompletedResponse
            {
                Success = false,
                ErrorMessage = "Empty UploadCompleted response received."
            };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<GetFilesResponse> GetFilesAsync(
        GetFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer.WriteJsonFrameAsync(_stream!, MessageType.GetFiles, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection.");
            }

            if (respHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
                return new GetFilesResponse { Success = false, ErrorMessage = err?.Message ?? "Failed to get files." };
            }

            if (respHeader.MessageType != MessageType.FilesResponse)
            {
                throw new ProtocolException($"Expected FilesResponse, received '{respHeader.MessageType}'.");
            }

            var result = await _reader.ReadJsonBodyAsync<GetFilesResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return result ?? new GetFilesResponse { Success = false, ErrorMessage = "Empty FilesResponse received." };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<GetVersionsResponse> GetVersionsAsync(
        GetVersionsRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer.WriteJsonFrameAsync(_stream!, MessageType.GetVersions, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection.");
            }

            if (respHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
                return new GetVersionsResponse { Success = false, ErrorMessage = err?.Message ?? "Failed to get versions." };
            }

            if (respHeader.MessageType != MessageType.VersionsResponse)
            {
                throw new ProtocolException($"Expected VersionsResponse, received '{respHeader.MessageType}'.");
            }

            var result = await _reader.ReadJsonBodyAsync<GetVersionsResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return result ?? new GetVersionsResponse { Success = false, ErrorMessage = "Empty VersionsResponse received." };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<GetChangeLogResponse> GetChangeLogAsync(
        GetChangeLogRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer.WriteJsonFrameAsync(_stream!, MessageType.GetChangeLog, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection.");
            }

            if (respHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
                return new GetChangeLogResponse { Success = false, ErrorMessage = err?.Message ?? "Failed to get changelog." };
            }

            if (respHeader.MessageType != MessageType.ChangeLogResponse)
            {
                throw new ProtocolException($"Expected ChangeLogResponse, received '{respHeader.MessageType}'.");
            }

            var result = await _reader.ReadJsonBodyAsync<GetChangeLogResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return result ?? new GetChangeLogResponse { Success = false, ErrorMessage = "Empty ChangeLogResponse received." };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<JoinSharedBackupSetResponse> JoinSharedBackupSetAsync(
        JoinSharedBackupSetRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer.WriteJsonFrameAsync(_stream!, MessageType.JoinSharedBackupSet, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection.");
            }

            if (respHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
                return new JoinSharedBackupSetResponse { Success = false, ErrorMessage = err?.Message ?? "Failed to join shared backup set." };
            }

            if (respHeader.MessageType != MessageType.JoinSharedBackupSetResult)
            {
                throw new ProtocolException($"Expected JoinSharedBackupSetResult, received '{respHeader.MessageType}'.");
            }

            var result = await _reader.ReadJsonBodyAsync<JoinSharedBackupSetResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return result ?? new JoinSharedBackupSetResponse { Success = false, ErrorMessage = "Empty JoinSharedBackupSetResult received." };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<GetBackupSetMembersResponse> GetBackupSetMembersAsync(
        GetBackupSetMembersRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer.WriteJsonFrameAsync(_stream!, MessageType.GetBackupSetMembers, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection.");
            }

            if (respHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
                return new GetBackupSetMembersResponse { Success = false, ErrorMessage = err?.Message ?? "Failed to get members." };
            }

            if (respHeader.MessageType != MessageType.GetBackupSetMembersResult)
            {
                throw new ProtocolException($"Expected GetBackupSetMembersResult, received '{respHeader.MessageType}'.");
            }

            var result = await _reader.ReadJsonBodyAsync<GetBackupSetMembersResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return result ?? new GetBackupSetMembersResponse { Success = false, ErrorMessage = "Empty GetBackupSetMembersResult received." };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<GetBackupSetManifestResponse> GetBackupSetManifestAsync(
        GetBackupSetManifestRequest request,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer.WriteJsonFrameAsync(_stream!, MessageType.GetBackupSetManifest, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection.");
            }

            if (respHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
                return new GetBackupSetManifestResponse { Success = false, ErrorCode = err?.Code ?? "ERROR", ErrorMessage = err?.Message ?? "Failed to get manifest." };
            }

            if (respHeader.MessageType != MessageType.GetBackupSetManifestResult)
            {
                throw new ProtocolException($"Expected GetBackupSetManifestResult, received '{respHeader.MessageType}'.");
            }

            var result = await _reader.ReadJsonBodyAsync<GetBackupSetManifestResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return result ?? new GetBackupSetManifestResponse { Success = false, ErrorMessage = "Empty GetBackupSetManifestResult received." };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<SharedFileDownloadResult> DownloadSharedFileVersionAsync(
        DownloadFileVersionRequest request,
        string localRootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localRootPath) || !Directory.Exists(localRootPath))
        {
            return new SharedFileDownloadResult
            {
                Success = false,
                ErrorCode = "INVALID_ROOT_PATH",
                ErrorMessage = $"Local root path '{localRootPath}' does not exist."
            };
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            // 1. Send DownloadFileVersion request
            await _writer.WriteJsonFrameAsync(_stream!, MessageType.DownloadFileVersion, request, cancellationToken: cancellationToken);

            // 2. Receive DownloadFileVersionReady descriptor
            var readyHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
            if (readyHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection before sending DownloadFileVersionReady.");
            }

            if (readyHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, readyHeader.BodyLength, cancellationToken);
                return new SharedFileDownloadResult
                {
                    Success = false,
                    ErrorCode = err?.Code ?? "ERROR",
                    ErrorMessage = err?.Message ?? "Download rejected by server."
                };
            }

            if (readyHeader.MessageType != MessageType.DownloadFileVersionReady)
            {
                throw new ProtocolException($"Expected DownloadFileVersionReady, received '{readyHeader.MessageType}'.");
            }

            var ready = await _reader.ReadJsonBodyAsync<DownloadFileVersionReadyResponse>(_stream!, readyHeader.BodyLength, cancellationToken);
            if (ready == null || !ready.Success)
            {
                return new SharedFileDownloadResult
                {
                    Success = false,
                    ErrorCode = ready?.ErrorCode ?? "DOWNLOAD_FAILED",
                    ErrorMessage = ready?.ErrorMessage ?? "DownloadFileVersionReady descriptor indicated failure."
                };
            }

            // 3. Receive DownloadFileVersionData binary header
            var fileHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
            if (fileHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection before sending DownloadFileVersionData payload.");
            }

            if (fileHeader.MessageType != MessageType.DownloadFileVersionData)
            {
                throw new ProtocolException($"Expected DownloadFileVersionData binary frame, received '{fileHeader.MessageType}'.");
            }

            if (fileHeader.BodyLength != ready.Size)
            {
                throw new ProtocolException($"Body length mismatch: header has {fileHeader.BodyLength}, ready descriptor has {ready.Size}.");
            }

            // 4. Validate target path safety
            if (!ProtocolPathValidator.TryNormalizeAndValidate(ready.RelativePath, out var normalizedRelative, out var pathErr))
            {
                await DrainRemainingAsync(_stream!, fileHeader.BodyLength, cancellationToken);
                return new SharedFileDownloadResult
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.InvalidRelativePath,
                    ErrorMessage = $"Server returned invalid relative path: {pathErr}"
                };
            }

            string fullRoot = Path.GetFullPath(localRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string targetFullPath = Path.GetFullPath(Path.Combine(localRootPath, normalizedRelative));

            if (!targetFullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                await DrainRemainingAsync(_stream!, fileHeader.BodyLength, cancellationToken);
                return new SharedFileDownloadResult
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.InvalidRelativePath,
                    ErrorMessage = $"Security violation: Download path '{targetFullPath}' escapes BackupSet root '{fullRoot}'."
                };
            }

            // 5. Ensure parent directories exist
            string? targetDir = Path.GetDirectoryName(targetFullPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 6. Write to temporary file in the same directory while calculating SHA-256
            string tempPath = Path.Combine(targetDir!, $"{Path.GetFileName(targetFullPath)}.sharedsync.{Guid.NewGuid():N}.tmp");
            string actualSha256;
            long actualSize = 0;

            try
            {
                using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                using (var sha256 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
                {
                    byte[] buffer = new byte[65536];
                    long remaining = ready.Size;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await _stream!.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                        if (read == 0)
                        {
                            throw new ProtocolException($"Unexpected end of stream while streaming download payload. Remaining: {remaining} bytes.");
                        }

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        sha256.AppendData(buffer, 0, read);
                        actualSize += read;
                        remaining -= read;
                    }

                    await fileStream.FlushAsync(cancellationToken);
                    actualSha256 = Convert.ToHexStringLower(sha256.GetHashAndReset());
                }

                // 7. Validate size and hash
                if (actualSize != ready.Size)
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    return new SharedFileDownloadResult
                    {
                        Success = false,
                        ErrorCode = BackupErrorCodes.SizeMismatch,
                        ErrorMessage = $"Size mismatch: expected {ready.Size}, received {actualSize}"
                    };
                }

                if (!string.Equals(actualSha256, ready.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    return new SharedFileDownloadResult
                    {
                        Success = false,
                        ErrorCode = BackupErrorCodes.HashMismatch,
                        ErrorMessage = $"SHA-256 mismatch: expected {ready.Sha256}, calculated {actualSha256}"
                    };
                }

                return new SharedFileDownloadResult
                {
                    Success = true,
                    TempFilePath = tempPath,
                    TargetFilePath = targetFullPath,
                    RelativePath = normalizedRelative,
                    Size = actualSize,
                    Sha256 = actualSha256,
                    VersionNumber = ready.VersionNumber,
                    FileId = ready.FileId
                };
            }
            catch (Exception ex)
            {
                MarkDisconnected();
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                return new SharedFileDownloadResult
                {
                    Success = false,
                    ErrorCode = "DOWNLOAD_STREAM_ERROR",
                    ErrorMessage = $"Stream transfer error: {ex.Message}"
                };
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<RestoreDownloadResult> DownloadAndVerifyRestoreFileAsync(
        RestoreRequest request,
        string localRootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localRootPath) || !Directory.Exists(localRootPath))
        {
            return new RestoreDownloadResult
            {
                Success = false,
                ErrorCode = "INVALID_ROOT_PATH",
                ErrorMessage = $"Local root path '{localRootPath}' does not exist."
            };
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            // 1. Send RestoreRequest
            await _writer.WriteJsonFrameAsync(_stream!, MessageType.RestoreRequest, request, cancellationToken: cancellationToken);

            // 2. Receive RestoreReady descriptor
            var readyHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
            if (readyHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection before sending RestoreReady.");
            }

            if (readyHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, readyHeader.BodyLength, cancellationToken);
                return new RestoreDownloadResult
                {
                    Success = false,
                    ErrorCode = err?.Code ?? "ERROR",
                    ErrorMessage = err?.Message ?? "Restore rejected by server."
                };
            }

            if (readyHeader.MessageType != MessageType.RestoreReady)
            {
                throw new ProtocolException($"Expected RestoreReady, received '{readyHeader.MessageType}'.");
            }

            var ready = await _reader.ReadJsonBodyAsync<RestoreReadyResponse>(_stream!, readyHeader.BodyLength, cancellationToken);
            if (ready == null || !ready.Success)
            {
                return new RestoreDownloadResult
                {
                    Success = false,
                    ErrorCode = ready?.ErrorCode ?? "RESTORE_FAILED",
                    ErrorMessage = ready?.ErrorMessage ?? "RestoreReady descriptor indicated failure."
                };
            }

            // 3. Receive RestoreFile binary header
            var fileHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
            if (fileHeader == null)
            {
                MarkDisconnected();
                throw new ProtocolException("Remote host closed TCP connection before sending RestoreFile payload.");
            }

            if (fileHeader.MessageType != MessageType.RestoreFile)
            {
                throw new ProtocolException($"Expected RestoreFile binary frame, received '{fileHeader.MessageType}'.");
            }

            if (fileHeader.BodyLength != ready.Size)
            {
                throw new ProtocolException($"Body length mismatch: header has {fileHeader.BodyLength}, ready descriptor has {ready.Size}.");
            }

            // 4. Validate target path safety
            if (!ProtocolPathValidator.TryNormalizeAndValidate(ready.RelativePath, out var normalizedRelative, out var pathErr))
            {
                await DrainRemainingAsync(_stream!, fileHeader.BodyLength, cancellationToken);
                return new RestoreDownloadResult
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.InvalidRelativePath,
                    ErrorMessage = $"Server returned invalid relative path: {pathErr}"
                };
            }

            string fullRoot = Path.GetFullPath(localRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string targetFullPath = Path.GetFullPath(Path.Combine(localRootPath, normalizedRelative));

            if (!targetFullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                await DrainRemainingAsync(_stream!, fileHeader.BodyLength, cancellationToken);
                return new RestoreDownloadResult
                {
                    Success = false,
                    ErrorCode = BackupErrorCodes.InvalidRelativePath,
                    ErrorMessage = $"Security violation: Restore path '{targetFullPath}' escapes BackupSet root '{fullRoot}'."
                };
            }

            // 5. Ensure parent directories exist
            string? targetDir = Path.GetDirectoryName(targetFullPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 6. Write to temporary file in the same directory while calculating SHA-256
            string tempPath = Path.Combine(targetDir!, $"{Path.GetFileName(targetFullPath)}.restore.{Guid.NewGuid():N}.tmp");
            string actualSha256;
            long actualSize = 0;

            try
            {
                using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                using (var sha256 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
                {
                    byte[] buffer = new byte[65536];
                    long remaining = ready.Size;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await _stream!.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                        if (read == 0)
                        {
                            throw new ProtocolException($"Unexpected end of stream while streaming restore file payload. Remaining: {remaining} bytes.");
                        }

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        sha256.AppendData(buffer, 0, read);
                        actualSize += read;
                        remaining -= read;
                    }

                    await fileStream.FlushAsync(cancellationToken);
                    actualSha256 = Convert.ToHexStringLower(sha256.GetHashAndReset());
                }

                // 7. Validate size and hash
                if (actualSize != ready.Size)
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    return new RestoreDownloadResult
                    {
                        Success = false,
                        ErrorCode = "RESTORE_SIZE_MISMATCH",
                        ErrorMessage = $"Size mismatch: expected {ready.Size}, received {actualSize}"
                    };
                }

                if (!string.Equals(actualSha256, ready.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    return new RestoreDownloadResult
                    {
                        Success = false,
                        ErrorCode = BackupErrorCodes.HashMismatch,
                        ErrorMessage = $"SHA-256 mismatch: expected {ready.Sha256}, calculated {actualSha256}"
                    };
                }

                return new RestoreDownloadResult
                {
                    Success = true,
                    TempFilePath = tempPath,
                    TargetFilePath = targetFullPath,
                    RelativePath = normalizedRelative,
                    Size = actualSize,
                    Sha256 = actualSha256,
                    VersionNumber = ready.VersionNumber,
                    FileId = ready.FileId
                };
            }
            catch (Exception ex)
            {
                MarkDisconnected();
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                return new RestoreDownloadResult
                {
                    Success = false,
                    ErrorCode = "RESTORE_STREAM_ERROR",
                    ErrorMessage = $"Stream transfer error: {ex.Message}"
                };
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<RestoreCompletedResponse> SendRestoreCompletedAsync(
        RestoreCompletedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            await _writer.WriteJsonFrameAsync(_stream!, MessageType.RestoreCompleted, notification, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null || respHeader.MessageType != MessageType.RestoreCompleted)
            {
                if (respHeader == null) MarkDisconnected();
                return new RestoreCompletedResponse { Success = true };
            }

            var result = await _reader.ReadJsonBodyAsync<RestoreCompletedResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return result ?? new RestoreCompletedResponse { Success = true };
        }
        catch (Exception)
        {
            MarkDisconnected();
            throw;
        }
        finally
        {
            _connectionLock.Release();
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

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || _stream == null)
        {
            await ConnectInternalAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        MarkDisconnected();
        _connectionLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
