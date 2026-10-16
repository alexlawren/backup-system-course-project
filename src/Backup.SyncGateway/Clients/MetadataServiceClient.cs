using System.Net.Sockets;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;

namespace Backup.SyncGateway.Clients;

/// <summary>
/// Client helper communicating with Backup.MetadataService over Protocol v1.
/// </summary>
public class MetadataServiceClient : IMetadataServiceClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly ProtocolFrameReader _reader;
    private readonly ProtocolFrameWriter _writer;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public MetadataServiceClient(string host, int port)
    {
        _host = host;
        _port = port;
        _reader = new ProtocolFrameReader();
        _writer = new ProtocolFrameWriter();
    }

    public void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        try { _tcpClient?.Dispose(); } catch { }
        _tcpClient = null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_tcpClient != null && _tcpClient.Connected && _stream != null)
        {
            return;
        }

        Disconnect();

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_host, _port, cancellationToken);
        _stream = _tcpClient.GetStream();
    }

    public async Task<HelloResponse?> HelloAsync(string clientName = "Backup.SyncGateway", CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            var req = new HelloRequest { ClientName = clientName, Version = "1.0" };
            var header = await _writer.WriteJsonFrameAsync(_stream!, MessageType.Hello, req, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);
            if (respHeader == null || respHeader.MessageType != MessageType.HelloAck)
            {
                Disconnect();
                return null;
            }
            return await _reader.ReadJsonBodyAsync<HelloResponse>(_stream!, respHeader.BodyLength, cancellationToken);
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public async Task<RegisterClientResponse> RegisterClientAsync(RegisterClientRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<RegisterClientRequest, RegisterClientResponse>(
            MessageType.RegisterClient,
            MessageType.RegisterClientResult,
            request,
            cancellationToken);
    }

    public async Task<CreateBackupSetResponse> CreateBackupSetAsync(CreateBackupSetRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<CreateBackupSetRequest, CreateBackupSetResponse>(
            MessageType.CreateBackupSet,
            MessageType.CreateBackupSetResult,
            request,
            cancellationToken);
    }

    public async Task<PrepareVersionResponse> PrepareVersionAsync(PrepareVersionRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<PrepareVersionRequest, PrepareVersionResponse>(
            MessageType.PrepareVersion,
            MessageType.PrepareVersionResult,
            request,
            cancellationToken);
    }

    public async Task<CommitVersionResponse> CommitVersionAsync(CommitVersionRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<CommitVersionRequest, CommitVersionResponse>(
            MessageType.CommitVersion,
            MessageType.CommitVersionResult,
            request,
            cancellationToken);
    }

    public async Task<DeleteFileMetadataResponse> DeleteFileMetadataAsync(DeleteFileMetadataRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<DeleteFileMetadataRequest, DeleteFileMetadataResponse>(
            MessageType.DeleteFileMetadata,
            MessageType.DeleteFileMetadataResult,
            request,
            cancellationToken);
    }

    public async Task<RenameFileMetadataResponse> RenameFileMetadataAsync(RenameFileMetadataRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<RenameFileMetadataRequest, RenameFileMetadataResponse>(
            MessageType.RenameFileMetadata,
            MessageType.RenameFileMetadataResult,
            request,
            cancellationToken);
    }

    public async Task<GetFilesResponse> GetFilesAsync(GetFilesRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<GetFilesRequest, GetFilesResponse>(
            MessageType.GetFiles,
            MessageType.FilesResponse,
            request,
            cancellationToken);
    }

    public async Task<GetVersionsResponse> GetVersionsAsync(GetVersionsRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<GetVersionsRequest, GetVersionsResponse>(
            MessageType.GetVersions,
            MessageType.VersionsResponse,
            request,
            cancellationToken);
    }

    public async Task<ResolveVersionResponse> ResolveVersionAsync(ResolveVersionRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<ResolveVersionRequest, ResolveVersionResponse>(
            MessageType.ResolveVersion,
            MessageType.ResolveVersionResult,
            request,
            cancellationToken);
    }

    public async Task<GetChangeLogResponse> GetChangeLogsAsync(GetChangeLogRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<GetChangeLogRequest, GetChangeLogResponse>(
            MessageType.GetChangeLog,
            MessageType.ChangeLogResponse,
            request,
            cancellationToken);
    }

    public async Task<JoinSharedBackupSetResponse> JoinSharedBackupSetAsync(JoinSharedBackupSetRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<JoinSharedBackupSetRequest, JoinSharedBackupSetResponse>(
            MessageType.JoinSharedBackupSet,
            MessageType.JoinSharedBackupSetResult,
            request,
            cancellationToken);
    }

    public async Task<GetBackupSetMembersResponse> GetBackupSetMembersAsync(GetBackupSetMembersRequest request, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<GetBackupSetMembersRequest, GetBackupSetMembersResponse>(
            MessageType.GetBackupSetMembers,
            MessageType.GetBackupSetMembersResult,
            request,
            cancellationToken);
    }

    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        MessageType requestType,
        MessageType expectedResponseType,
        TRequest request,
        CancellationToken cancellationToken) where TResponse : class, new()
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var reqHeader = await _writer.WriteJsonFrameAsync(_stream!, requestType, request, cancellationToken: cancellationToken);
            var respHeader = await _reader.ReadHeaderAsync(_stream!, cancellationToken);

            if (respHeader == null)
            {
                Disconnect();
                throw new ProtocolException("Remote host closed TCP stream unexpectedly.");
            }

            if (respHeader.MessageType == MessageType.Error)
            {
                var err = await _reader.ReadJsonBodyAsync<ErrorMessage>(_stream!, respHeader.BodyLength, cancellationToken);
                throw new ProtocolException($"MetadataService returned Error [{err?.Code}]: {err?.Message}");
            }

            if (respHeader.MessageType != expectedResponseType)
            {
                Disconnect();
                throw new ProtocolException($"Unexpected response MessageType. Expected '{expectedResponseType}', received '{respHeader.MessageType}'.");
            }

            var response = await _reader.ReadJsonBodyAsync<TResponse>(_stream!, respHeader.BodyLength, cancellationToken);
            return response ?? new TResponse();
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_tcpClient == null || !_tcpClient.Connected || _stream == null)
        {
            await ConnectAsync(cancellationToken);
        }
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
