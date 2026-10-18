using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Backup.Client.Web.Services;
using Backup.Shared.DTOs;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class GatewayOfflineResilienceTests : IAsyncDisposable
{
    private readonly List<string> _tempDirs = new();

    private string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "gw_offline_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch { }
        }
        await Task.CompletedTask;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private class MockGatewayServer : IAsyncDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        public int Port { get; }

        public MockGatewayServer(int port)
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
                                    await reader.ReadJsonBodyAsync<HelloRequest>(stream, header.BodyLength, token);
                                    await writer.WriteJsonFrameAsync(stream, MessageType.HelloAck, new HelloResponse
                                    {
                                        Success = true,
                                        ServerName = "MockGateway"
                                    }, header.CorrelationId, token);
                                }
                                else if (header.MessageType == MessageType.Ping)
                                {
                                    await writer.WriteControlFrameAsync(stream, MessageType.Pong, header.CorrelationId, token);
                                }
                                else if (header.MessageType == MessageType.GetFiles)
                                {
                                    await reader.ReadJsonBodyAsync<GetFilesRequest>(stream, header.BodyLength, token);
                                    await writer.WriteJsonFrameAsync(stream, MessageType.FilesResponse, new GetFilesResponse
                                    {
                                        Success = true,
                                        Files = new List<FileMetadataDto>
                                        {
                                            new() { FileId = Guid.NewGuid(), RelativePath = "test.txt", CurrentVersionNumber = 1 }
                                        }
                                    }, header.CorrelationId, token);
                                }
                                else if (header.MessageType == MessageType.GetChangeLog)
                                {
                                    await reader.ReadJsonBodyAsync<GetChangeLogRequest>(stream, header.BodyLength, token);
                                    await writer.WriteJsonFrameAsync(stream, MessageType.ChangeLogResponse, new GetChangeLogResponse
                                    {
                                        Success = true,
                                        Entries = new List<ChangeLogEntryDto>
                                        {
                                            new() { Action = "BACKUP", RelativePath = "test.txt", Status = "SUCCESS", CreatedAtUtc = DateTime.UtcNow }
                                        }
                                    }, header.CorrelationId, token);
                                }
                                else if (header.MessageType == MessageType.GetVersions)
                                {
                                    await reader.ReadJsonBodyAsync<GetVersionsRequest>(stream, header.BodyLength, token);
                                    await writer.WriteJsonFrameAsync(stream, MessageType.VersionsResponse, new GetVersionsResponse
                                    {
                                        Success = true,
                                        Versions = new List<FileVersionDto>
                                        {
                                            new() { VersionNumber = 1, Sha256 = "abc", Size = 100, CreatedAtUtc = DateTime.UtcNow }
                                        }
                                    }, header.CorrelationId, token);
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

    [Fact]
    public async Task GatewayOffline_HomeRemainsResponsive()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);
        string tempDir = CreateTempDir();
        var identityService = new ClientIdentityService(Path.Combine(tempDir, "state.json"));
        var backupService = new BackupClientService(client, identityService);

        var sw = Stopwatch.StartNew();
        bool connected = await backupService.PingGatewayAsync();
        sw.Stop();

        Assert.False(connected);
        Assert.False(client.IsConnected);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"Ping took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GatewayOffline_FilesPageReturnsErrorWithoutHanging()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);
        string tempDir = CreateTempDir();
        var identityService = new ClientIdentityService(Path.Combine(tempDir, "state.json"));
        var backupService = new BackupClientService(client, identityService);

        var sw = Stopwatch.StartNew();
        var response = await backupService.GetFilesAsync(Guid.NewGuid());
        sw.Stop();

        Assert.False(response.Success);
        Assert.Contains("шлюз недоступен", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"GetFiles took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GatewayOffline_HistoryPageReturnsErrorWithoutHanging()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);
        string tempDir = CreateTempDir();
        var identityService = new ClientIdentityService(Path.Combine(tempDir, "state.json"));
        var backupService = new BackupClientService(client, identityService);

        var sw = Stopwatch.StartNew();
        var response = await backupService.GetChangeLogsAsync(Guid.NewGuid());
        sw.Stop();

        Assert.False(response.Success);
        Assert.Contains("шлюз недоступен", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"GetChangeLogs took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GatewayOffline_FileDetailsReturnsErrorWithoutHanging()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);
        string tempDir = CreateTempDir();
        var identityService = new ClientIdentityService(Path.Combine(tempDir, "state.json"));
        var backupService = new BackupClientService(client, identityService);

        var sw = Stopwatch.StartNew();
        var response = await backupService.GetVersionsAsync(Guid.NewGuid());
        sw.Stop();

        Assert.False(response.Success);
        Assert.Contains("шлюз недоступен", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"GetVersions took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GatewayOffline_NavigationRemainsUsable()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);
        string tempDir = CreateTempDir();
        var identityService = new ClientIdentityService(Path.Combine(tempDir, "state.json"));
        var backupService = new BackupClientService(client, identityService);

        var totalSw = Stopwatch.StartNew();

        // 1. Home
        bool ping1 = await backupService.PingGatewayAsync();
        Assert.False(ping1);

        // 2. Files
        var files = await backupService.GetFilesAsync(Guid.NewGuid());
        Assert.False(files.Success);

        // 3. History
        var history = await backupService.GetChangeLogsAsync(Guid.NewGuid());
        Assert.False(history.Success);

        // 4. FileDetails
        var versions = await backupService.GetVersionsAsync(Guid.NewGuid());
        Assert.False(versions.Success);

        // 5. Back to Home
        bool ping2 = await backupService.PingGatewayAsync();
        Assert.False(ping2);

        totalSw.Stop();
        Assert.True(totalSw.Elapsed < TimeSpan.FromSeconds(6.0), $"Navigation loop took too long: {totalSw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GatewayOffline_ReconciliationReleasesSyncLock()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);
        string tempDir = CreateTempDir();
        var identityService = new ClientIdentityService(Path.Combine(tempDir, "state.json"));
        var indexService = new LocalFileIndexService(Path.Combine(tempDir, "index"));
        var scanner = new DirectoryScanner();
        var watcher = new FileSystemWatcherService();
        var stability = new FileStabilityHelper();
        var profileService = new SyncProfileService(tempDir);

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "OfflineSet",
            LocalRootPath = tempDir,
            AutomaticSyncEnabled = true,
            AccessMode = BackupSetAccessMode.Private
        };

        var engine = new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcher,
            stability,
            NullLogger<SyncEngine>.Instance,
            debounceDelayMs: 200,
            profileService: profileService);

        await using (engine)
        {
            // Initial reconciliation will fail because gateway is offline
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await engine.StartAsync(profile);
            });

            // The lock must be released! Verify we can immediately reconcile again or switch profile without locking up
            Assert.False(engine.IsReconciling);

            var sw = Stopwatch.StartNew();
            // Attempting another operation that requires _syncLock should not hang indefinitely
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await engine.StopAsync(cts.Token);
            }
            catch { }
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.0), $"StopAsync after reconciliation failure took too long: {sw.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public async Task GatewayOffline_DoesNotStartDuplicateReconnectLoops()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);

        // Trigger multiple background reconnect starts
        for (int i = 0; i < 10; i++)
        {
            client.EnsureBackgroundReconnectRunning();
        }

        // Active reconnect loops must strictly be 1
        Assert.Equal(1, client.ActiveReconnectLoopCount);
        Assert.True(client.IsReconnecting);
    }

    [Fact]
    public async Task GatewayOffline_RequestCompletesWithinBoundedTimeout()
    {
        int unusedPort = GetAvailablePort();
        await using var client = new BackupGatewayClient("127.0.0.1", unusedPort);

        var sw = Stopwatch.StartNew();
        bool result = await client.PingAsync();
        sw.Stop();

        Assert.False(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"Request exceeded bounded timeout: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GatewayReturns_AutomaticReconnectRestoresOnlineState()
    {
        int port = GetAvailablePort();
        await using var server = new MockGatewayServer(port);
        server.Start();

        await using var client = new BackupGatewayClient("127.0.0.1", port);
        client.SetActiveClientId(Guid.NewGuid());

        bool reconnectedFired = false;
        client.Reconnected += () => { reconnectedFired = true; };

        // 1. Initial connect
        bool ping1 = await client.PingAsync();
        Assert.True(ping1);
        Assert.True(client.IsConnected);

        // 2. Gateway goes down
        await server.StopAsync();
        bool pingOffline = await client.PingAsync();
        Assert.False(pingOffline);
        Assert.False(client.IsConnected);

        // 3. Gateway comes back up
        server.Start();

        // 4. Client reconnects
        bool pingOnline = await client.PingAsync();
        Assert.True(pingOnline);
        Assert.True(client.IsConnected);
        Assert.True(reconnectedFired);
    }

    [Fact]
    public async Task GatewayReturns_PagesLoadNormallyAgain()
    {
        int port = GetAvailablePort();
        await using var server = new MockGatewayServer(port);
        server.Start();

        await using var client = new BackupGatewayClient("127.0.0.1", port);
        var clientId = Guid.NewGuid();
        client.SetActiveClientId(clientId);
        string tempDir = CreateTempDir();
        var identityService = new ClientIdentityService(Path.Combine(tempDir, "state.json"));
        var backupService = new BackupClientService(client, identityService);

        // 1. Initial success
        var resp1 = await backupService.GetFilesAsync(Guid.NewGuid());
        Assert.True(resp1.Success);
        Assert.NotNull(resp1.Files);

        // 2. Gateway goes down
        await server.StopAsync();
        var respOffline = await backupService.GetFilesAsync(Guid.NewGuid());
        Assert.False(respOffline.Success);
        Assert.Contains("шлюз недоступен", respOffline.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // 3. Gateway restarts
        server.Start();

        // 4. Next call restores normal operations without restarting client
        var respRecovered = await backupService.GetFilesAsync(Guid.NewGuid());
        Assert.True(respRecovered.Success);
        Assert.NotNull(respRecovered.Files);
        Assert.Single(respRecovered.Files);
    }
}
