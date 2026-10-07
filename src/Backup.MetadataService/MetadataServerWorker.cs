using System.Net;
using System.Net.Sockets;
using Backup.MetadataService.Configuration;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.MetadataService;

public class MetadataServerWorker : BackgroundService
{
    private readonly ILogger<MetadataServerWorker> _logger;
    private readonly MetadataServiceOptions _options;
    private TcpListener? _listener;

    public MetadataServerWorker(
        ILogger<MetadataServerWorker> logger,
        IOptions<MetadataServiceOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ip = IPAddress.Parse(_options.Host);
        _listener = new TcpListener(ip, _options.Port);
        _listener.Start();

        _logger.LogInformation("[MetadataService] Listening on {Host}:{Port}", _options.Host, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient tcpClient;
                try
                {
                    tcpClient = await _listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleClientAsync(tcpClient, stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("[MetadataService] Listener stopped.");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogDebug("[MetadataService] Connection accepted from {RemoteEndpoint}", remoteEndpoint);

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var header = await reader.ReadHeaderAsync(stream, cancellationToken);
                    if (header == null)
                    {
                        break; // Normal client disconnect
                    }

                    switch (header.MessageType)
                    {
                        case MessageType.Hello:
                            var helloReq = await reader.ReadJsonBodyAsync<HelloRequest>(stream, header.BodyLength, cancellationToken);
                            _logger.LogInformation("[MetadataService] Received Hello from '{ClientName}'", helloReq?.ClientName ?? "Unknown");

                            var helloAck = new HelloResponse
                            {
                                ServerName = "Backup.MetadataService",
                                Version = "1.0",
                                Success = true,
                                Message = "MetadataService is ready."
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.HelloAck, helloAck, correlationId: header.MessageId, cancellationToken: cancellationToken);
                            break;

                        case MessageType.Ping:
                            var pingMsg = await reader.ReadJsonBodyAsync<PingMessage>(stream, header.BodyLength, cancellationToken);
                            _logger.LogInformation("[MetadataService] Received Ping (MsgId: {MessageId})", header.MessageId);

                            var pongMsg = new PingMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                            await writer.WriteJsonFrameAsync(stream, MessageType.Pong, pongMsg, correlationId: header.MessageId, cancellationToken: cancellationToken);
                            break;

                        default:
                            _logger.LogWarning("[MetadataService] Unhandled MessageType: {MessageType}", header.MessageType);
                            if (header.BodyLength > 0)
                            {
                                await reader.ReadBodyBytesAsync(stream, header.BodyLength, cancellationToken);
                            }

                            var errorMsg = new ErrorMessage
                            {
                                Code = "NOT_IMPLEMENTED",
                                Message = $"MessageType '{header.MessageType}' is not handled in Change-set #1."
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.Error, errorMsg, correlationId: header.MessageId, cancellationToken: cancellationToken);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning("[MetadataService] Protocol error from {RemoteEndpoint}: {Message}", remoteEndpoint, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _logger.LogDebug("[MetadataService] Connection closed with {RemoteEndpoint}: {Message}", remoteEndpoint, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MetadataService] Error handling client connection from {RemoteEndpoint}", remoteEndpoint);
        }
    }
}
