using System.Net;
using System.Net.Sockets;
using Backup.Shared.Protocol;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backup.StorageService;

public class StorageServerWorker : BackgroundService
{
    private readonly ILogger<StorageServerWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly StorageServiceOptions _options;
    private readonly IFileStorageService _storageService;
    private TcpListener? _listener;

    public StorageServerWorker(
        ILogger<StorageServerWorker> logger,
        ILoggerFactory loggerFactory,
        IOptions<StorageServiceOptions> options,
        IFileStorageService storageService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _storageService = storageService;
    }

    public int? BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure storage directory structure is initialized on worker start
        _storageService.Initialize();

        var ip = IPAddress.Parse(_options.Host);
        _listener = new TcpListener(ip, _options.Port);
        _listener.Start();

        var actualPort = (_listener.LocalEndpoint as IPEndPoint)?.Port ?? _options.Port;
        _logger.LogInformation("[StorageService] Listening on {Host}:{Port}. Root storage: '{RootPath}'",
            _options.Host, actualPort, _storageService.RootPath);

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
            _logger.LogInformation("[StorageService] Listener stopped.");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogDebug("[StorageService] Connection accepted from {RemoteEndpoint}", remoteEndpoint);

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var dispatcherLogger = _loggerFactory.CreateLogger<StorageMessageDispatcher>();
                var dispatcher = new StorageMessageDispatcher(_storageService, dispatcherLogger);

                await dispatcher.ProcessConnectionAsync(stream, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning("[StorageService] Protocol error from {RemoteEndpoint}: {Message}", remoteEndpoint, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _logger.LogDebug("[StorageService] Connection closed with {RemoteEndpoint}: {Message}", remoteEndpoint, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorageService] Error handling client connection from {RemoteEndpoint}", remoteEndpoint);
        }
    }
}
