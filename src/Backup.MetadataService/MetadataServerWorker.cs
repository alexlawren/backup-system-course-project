using System.Net;
using System.Net.Sockets;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Services;
using Backup.Shared.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.MetadataService;

public class MetadataServerWorker : BackgroundService
{
    private readonly ILogger<MetadataServerWorker> _logger;
    private readonly MetadataServiceOptions _options;
    private readonly MetadataMessageDispatcher _dispatcher;
    private readonly ProtocolFrameReader _reader;
    private TcpListener? _listener;

    public MetadataServerWorker(
        ILogger<MetadataServerWorker> logger,
        IOptions<MetadataServiceOptions> options,
        MetadataMessageDispatcher dispatcher,
        ProtocolFrameReader reader)
    {
        _logger = logger;
        _options = options.Value;
        _dispatcher = dispatcher;
        _reader = reader;
    }

    public int? BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port;

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
                while (!cancellationToken.IsCancellationRequested)
                {
                    var header = await _reader.ReadHeaderAsync(stream, cancellationToken);
                    if (header == null)
                    {
                        break; // Normal peer disconnect
                    }

                    await _dispatcher.DispatchAsync(header, stream, cancellationToken);
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
