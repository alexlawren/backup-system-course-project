using System.Net;
using System.Net.Sockets;
using Backup.Client.Web.Services;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.SyncGateway.Clients;
using Backup.SyncGateway.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class ClientReconnectTests
{
    private class RestartableGateway : IAsyncDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        public int Port { get; }

        public RestartableGateway(int port)
        {
            Port = port;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();

            var token = _cts.Token;
            _serverTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var socket = await _listener.AcceptTcpClientAsync(token);
                        _ = Task.Run(async () =>
                        {
                            using var s = socket;
                            using var stream = s.GetStream();
                            var reader = new ProtocolFrameReader();
                            var writer = new ProtocolFrameWriter();

                            while (!token.IsCancellationRequested)
                            {
                                var header = await reader.ReadHeaderAsync(stream, token);
                                if (header == null) break;

                                if (header.MessageType == MessageType.Hello)
                                {
                                    var hello = await reader.ReadJsonBodyAsync<HelloRequest>(stream, header.BodyLength, token);
                                    await writer.WriteJsonFrameAsync(stream, MessageType.HelloAck, new HelloResponse
                                    {
                                        Success = true,
                                        ServerName = "RestartableGateway"
                                    }, header.CorrelationId, token);
                                }
                                else if (header.MessageType == MessageType.Ping)
                                {
                                    await writer.WriteControlFrameAsync(stream, MessageType.Pong, header.CorrelationId, token);
                                }
                            }
                        }, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            }, token);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            if (_serverTask != null)
            {
                try { await _serverTask; } catch { }
            }
            _cts?.Dispose();
            _cts = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task BackupGatewayClient_DetectsDisconnect_AndReconnectsSuccessfully_FiringEvent()
    {
        int port = GetAvailablePort();
        await using var gateway = new RestartableGateway(port);
        gateway.Start();

        var client = new BackupGatewayClient("127.0.0.1", port);
        var expectedClientId = Guid.NewGuid();
        client.SetActiveClientId(expectedClientId);

        bool reconnectedFired = false;
        client.Reconnected += () => { reconnectedFired = true; };

        // 1. Initial connect & ping
        bool firstPing = await client.PingAsync();
        Assert.True(firstPing);
        Assert.True(client.IsConnected);
        Assert.False(reconnectedFired); // First connection is not a reconnect

        // 2. Stop server to simulate connection drop / gateway restart
        await gateway.StopAsync();

        // 3. Next ping detects server down
        bool downPing = await client.PingAsync();
        Assert.False(downPing);
        Assert.False(client.IsConnected);

        // 4. Restart server on the same port
        gateway.Start();

        // 5. Next call triggers reconnect with backoff & re-Hello
        bool recoveredPing = await client.PingAsync();
        Assert.True(recoveredPing);
        Assert.True(client.IsConnected);
        Assert.True(reconnectedFired); // Reconnected event fired!
    }
}
