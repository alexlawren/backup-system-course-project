using System.Net;
using System.Net.Sockets;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.SyncGateway.Clients;
using Backup.SyncGateway.Configuration;
using Backup.SyncGateway.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.SyncGateway;

public class GatewayServerWorker : BackgroundService
{
    private readonly ILogger<GatewayServerWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly GatewayOptions _gatewayOptions;
    private readonly DownstreamServicesOptions _downstreamOptions;
    private TcpListener? _listener;

    public GatewayServerWorker(
        ILogger<GatewayServerWorker> logger,
        ILoggerFactory loggerFactory,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<DownstreamServicesOptions> downstreamOptions)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _gatewayOptions = gatewayOptions.Value;
        _downstreamOptions = downstreamOptions.Value;
    }

    public int? BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Startup smoke verification with downstream services
        await CheckDownstreamServiceAsync("MetadataService", _downstreamOptions.MetadataService.Host, _downstreamOptions.MetadataService.Port, stoppingToken);
        await CheckDownstreamServiceAsync("StorageService", _downstreamOptions.StorageService.Host, _downstreamOptions.StorageService.Port, stoppingToken);

        // 2. Start Gateway TCP Listener
        var ip = IPAddress.Parse(_gatewayOptions.Host);
        _listener = new TcpListener(ip, _gatewayOptions.Port);
        _listener.Start();

        var boundPort = BoundPort ?? _gatewayOptions.Port;
        _logger.LogInformation("[SyncGateway] Listening on {Host}:{Port}", _gatewayOptions.Host, boundPort);

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
            _logger.LogInformation("[SyncGateway] Listener stopped.");
        }
    }

    private async Task CheckDownstreamServiceAsync(string serviceName, string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            await client.ConnectAsync(host, port, cts.Token);
            await using var stream = client.GetStream();

            var reader = new ProtocolFrameReader();
            var writer = new ProtocolFrameWriter();

            var helloHeader = await writer.WriteJsonFrameAsync(
                stream,
                MessageType.Hello,
                new HelloRequest { ClientName = "Backup.SyncGateway", Version = "1.0", Role = "SyncGateway" },
                cancellationToken: cts.Token);

            var respHeader = await reader.ReadHeaderAsync(stream, cts.Token);
            if (respHeader != null && respHeader.MessageType == MessageType.HelloAck && respHeader.CorrelationId == helloHeader.MessageId)
            {
                _logger.LogInformation("[SyncGateway] {ServiceName} handshake OK", serviceName);
            }
            else
            {
                _logger.LogWarning("[SyncGateway] {ServiceName} returned unexpected handshake response: {RespType}", serviceName, respHeader?.MessageType);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ProtocolException)
        {
            _logger.LogWarning("[SyncGateway] Downstream {ServiceName} at {Host}:{Port} is unavailable at startup: {Message}", serviceName, host, port, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SyncGateway] Unexpected error during {ServiceName} handshake check", serviceName);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogDebug("[SyncGateway] Connection accepted from {RemoteEndpoint}", remoteEndpoint);

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var metadataClient = new MetadataServiceClient(
                    _downstreamOptions.MetadataService.Host,
                    _downstreamOptions.MetadataService.Port);

                var storageClient = new StorageServiceClient(
                    _downstreamOptions.StorageService.Host,
                    _downstreamOptions.StorageService.Port);

                var sessionLogger = _loggerFactory.CreateLogger<GatewaySessionHandler>();
                await using var sessionHandler = new GatewaySessionHandler(
                    metadataClient,
                    storageClient,
                    sessionLogger);

                await sessionHandler.ProcessConnectionAsync(stream, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning("[SyncGateway] Protocol error from {RemoteEndpoint}: {Message}", remoteEndpoint, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _logger.LogDebug("[SyncGateway] Connection closed with {RemoteEndpoint}: {Message}", remoteEndpoint, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SyncGateway] Error handling client connection from {RemoteEndpoint}", remoteEndpoint);
        }
    }
}
