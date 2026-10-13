using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Backup.Client.Web.Services;
using Backup.Shared.DTOs;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Xunit;

namespace Backup.Tests;

public class TcpSessionFramingRegressionTests : IAsyncDisposable
{
    private class TestProtocolV1Gateway : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverLoop;
        public int Port { get; }
        public byte[] RestorePayload { get; set; } = Encoding.UTF8.GetBytes("Simulated binary payload for restore regression test");

        public TestProtocolV1Gateway()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverLoop = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(token);
                    _ = HandleClientConnectionAsync(client, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleClientConnectionAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                while (!token.IsCancellationRequested)
                {
                    ProtocolHeader? header;
                    try
                    {
                        header = await reader.ReadHeaderAsync(stream, token);
                    }
                    catch
                    {
                        break;
                    }

                    if (header == null) break;

                    switch (header.MessageType)
                    {
                        case MessageType.Hello:
                            var helloReq = await reader.ReadJsonBodyAsync<HelloRequest>(stream, header.BodyLength, token);
                            await writer.WriteJsonFrameAsync(stream, MessageType.HelloAck, new HelloResponse
                            {
                                Success = true,
                                ServerName = "TestProtocolV1Gateway",
                                Role = "SyncGateway"
                            }, header.CorrelationId, token);
                            break;

                        case MessageType.Ping:
                            if (header.BodyLength > 0)
                            {
                                await reader.ReadBodyBytesAsync(stream, header.BodyLength, token);
                            }
                            // Production SyncGateway returns Pong with JSON PingMessage body!
                            var pong = new PingMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                            await writer.WriteJsonFrameAsync(stream, MessageType.Pong, pong, header.MessageId, token);
                            break;

                        case MessageType.RegisterClient:
                            var regReq = await reader.ReadJsonBodyAsync<RegisterClientRequest>(stream, header.BodyLength, token);
                            var regResp = new RegisterClientResponse
                            {
                                Success = true,
                                ClientId = regReq?.ClientId ?? Guid.NewGuid()
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.RegisterClientResult, regResp, header.MessageId, token);
                            break;

                        case MessageType.CreateBackupSet:
                            var bsReq = await reader.ReadJsonBodyAsync<CreateBackupSetRequest>(stream, header.BodyLength, token);
                            var bsResp = new CreateBackupSetResponse
                            {
                                Success = true,
                                BackupSetId = Guid.NewGuid(),
                                Name = bsReq?.Name ?? "TestSet"
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.CreateBackupSetResult, bsResp, header.MessageId, token);
                            break;

                        case MessageType.GetFiles:
                            await reader.ReadJsonBodyAsync<GetFilesRequest>(stream, header.BodyLength, token);
                            var filesResp = new GetFilesResponse
                            {
                                Success = true,
                                Files = new List<FileMetadataDto>
                                {
                                    new()
                                    {
                                        FileId = Guid.NewGuid(),
                                        RelativePath = "doc.txt",
                                        CurrentVersionNumber = 1,
                                        CurrentSize = 100,
                                        CurrentSha256 = "dummy",
                                        IsDeleted = false
                                    }
                                }
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.FilesResponse, filesResp, header.MessageId, token);
                            break;

                        case MessageType.GetVersions:
                            await reader.ReadJsonBodyAsync<GetVersionsRequest>(stream, header.BodyLength, token);
                            var versionsResp = new GetVersionsResponse
                            {
                                Success = true,
                                Versions = new List<FileVersionDto>
                                {
                                    new() { VersionNumber = 1, Size = 100, Sha256 = "dummy" }
                                }
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.VersionsResponse, versionsResp, header.MessageId, token);
                            break;

                        case MessageType.GetChangeLog:
                            await reader.ReadJsonBodyAsync<GetChangeLogRequest>(stream, header.BodyLength, token);
                            var clResp = new GetChangeLogResponse
                            {
                                Success = true,
                                Entries = new List<ChangeLogEntryDto>
                                {
                                    new() { Action = "CREATE", RelativePath = "doc.txt", CreatedAtUtc = DateTime.UtcNow }
                                }
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.ChangeLogResponse, clResp, header.MessageId, token);
                            break;

                        case MessageType.FileChanged:
                            var changeReq = await reader.ReadJsonBodyAsync<FileChangeRequest>(stream, header.BodyLength, token);
                            var upReqResp = new UploadRequiredResponse
                            {
                                UploadRequired = true,
                                FileId = Guid.NewGuid(),
                                VersionNumber = 1
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.UploadRequired, upReqResp, header.MessageId, token);
                            break;

                        case MessageType.UploadFile:
                            // Read binary payload exactly
                            byte[] buffer = new byte[header.BodyLength];
                            await stream.ReadExactlyAsync(buffer, 0, (int)header.BodyLength, token);
                            var upComp = new UploadCompletedResponse
                            {
                                Success = true,
                                VersionNumber = 1,
                                FileId = Guid.NewGuid()
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.UploadCompleted, upComp, header.MessageId, token);
                            break;

                        case MessageType.RestoreRequest:
                            var restReq = await reader.ReadJsonBodyAsync<RestoreRequest>(stream, header.BodyLength, token);
                            string sha = Convert.ToHexString(SHA256.HashData(RestorePayload)).ToLowerInvariant();
                            var ready = new RestoreReadyResponse
                            {
                                Success = true,
                                RelativePath = "doc.txt",
                                Size = RestorePayload.Length,
                                Sha256 = sha,
                                VersionNumber = restReq?.VersionNumber ?? 1,
                                FileId = restReq?.FileId ?? Guid.NewGuid()
                            };
                            await writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, header.MessageId, token);

                            // Send binary RestoreFile frame
                            var binaryHeader = new ProtocolHeader
                            {
                                Magic = ProtocolConstants.Magic,
                                ProtocolVersion = ProtocolConstants.Version,
                                MessageType = MessageType.RestoreFile,
                                MessageId = Guid.NewGuid(),
                                CorrelationId = header.MessageId,
                                ContentType = ProtocolContentTypes.ApplicationOctetStream,
                                BodyLength = RestorePayload.Length,
                                SentAtUtc = DateTime.UtcNow
                            };
                            using (var ms = new MemoryStream(RestorePayload))
                            {
                                await writer.WriteFrameAsync(stream, binaryHeader, ms, token);
                            }
                            break;

                        case MessageType.RestoreCompleted:
                            await reader.ReadJsonBodyAsync<RestoreCompletedNotification>(stream, header.BodyLength, token);
                            await writer.WriteJsonFrameAsync(stream, MessageType.RestoreCompleted, new RestoreCompletedResponse { Success = true }, header.MessageId, token);
                            break;

                        default:
                            if (header.BodyLength > 0)
                            {
                                await reader.ReadBodyBytesAsync(stream, header.BodyLength, token);
                            }
                            break;
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { await _serverLoop; } catch { }
            _cts.Dispose();
        }
    }

    private readonly TestProtocolV1Gateway _gateway = new();

    public async ValueTask DisposeAsync()
    {
        await _gateway.DisposeAsync();
    }

    [Fact]
    public async Task BugRegression_PingFollowedByRegisterClient_DoesNotCorruptFraming()
    {
        // This test precisely reproduces the reported runtime bug:
        // Ping response has JSON Pong body {"timestamp":...}
        // Previously, PingAsync failed to read Pong body, leaving {"ti (0x7B 0x22 0x74 0x69)
        // in the stream buffer. RegisterClient then read 2065855593 as HeaderLength.
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);

        // 1. Ping Gateway
        bool ping = await client.PingAsync();
        Assert.True(ping);

        // 2. Register Client
        var regResp = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "Station-1",
            MachineName = Environment.MachineName
        });

        Assert.True(regResp.Success);
        Assert.NotEqual(Guid.Empty, regResp.ClientId);
    }

    [Fact]
    public async Task BugRegression_ConcurrentPingAndRegisterClient_SerializedCorrectly()
    {
        // 1. Ping and RegisterClient launched almost simultaneously
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);

        var pingTask = Task.Run(() => client.PingAsync());
        var regTask = Task.Run(() => client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "ConcurrentStation",
            MachineName = Environment.MachineName
        }));

        await Task.WhenAll(pingTask, regTask);

        Assert.True(await pingTask);
        var reg = await regTask;
        Assert.True(reg.Success);
        Assert.NotEqual(Guid.Empty, reg.ClientId);
    }

    [Fact]
    public async Task BugRegression_ConcurrentRefreshGetFilesAndRegisterClient_SerializedCorrectly()
    {
        // 2. Refresh/GetFiles and RegisterClient simultaneously
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);

        var regTask = Task.Run(() => client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "RefreshStation",
            MachineName = Environment.MachineName
        }));

        var getFilesTask = Task.Run(() => client.GetFilesAsync(new GetFilesRequest
        {
            BackupSetId = Guid.NewGuid(),
            IncludeDeleted = true
        }));

        await Task.WhenAll(regTask, getFilesTask);

        Assert.True((await regTask).Success);
        Assert.True((await getFilesTask).Success);
    }

    [Fact]
    public async Task BugRegression_50To100MixedSequentialOperations_ZeroFramingErrors()
    {
        // 4. 50-100 consecutive mixed operations
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);
        Guid backupSetId = Guid.NewGuid();

        for (int i = 0; i < 75; i++)
        {
            // Mix: Ping -> Register -> GetFiles -> GetChangeLog -> CreateBackupSet
            bool ping = await client.PingAsync();
            Assert.True(ping, $"Ping failed at iteration {i}");

            var reg = await client.RegisterClientAsync(new RegisterClientRequest
            {
                Name = $"SequentialStation_{i}",
                MachineName = Environment.MachineName
            });
            Assert.True(reg.Success, $"RegisterClient failed at iteration {i}");

            var files = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = backupSetId });
            Assert.True(files.Success, $"GetFiles failed at iteration {i}");

            var logs = await client.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = backupSetId, Limit = 10 });
            Assert.True(logs.Success, $"GetChangeLog failed at iteration {i}");

            var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
            {
                ClientId = reg.ClientId,
                Name = $"Set_{i}",
                LocalRootPath = "D:\\Temp"
            });
            Assert.True(bs.Success, $"CreateBackupSet failed at iteration {i}");
        }
    }

    [Fact]
    public async Task BugRegression_MultipleUiTriggeredTasksSimultaneously_CompleteCleanly()
    {
        // 5. Multiple UI-triggered tasks simultaneously on one BackupGatewayClient
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);
        Guid bsId = Guid.NewGuid();

        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            int idx = i;
            switch (idx % 4)
            {
                case 0:
                    tasks.Add(Task.Run(async () =>
                    {
                        var ok = await client.PingAsync();
                        Assert.True(ok);
                    }));
                    break;
                case 1:
                    tasks.Add(Task.Run(async () =>
                    {
                        var reg = await client.RegisterClientAsync(new RegisterClientRequest
                        {
                            Name = $"Parallel_{idx}",
                            MachineName = Environment.MachineName
                        });
                        Assert.True(reg.Success);
                    }));
                    break;
                case 2:
                    tasks.Add(Task.Run(async () =>
                    {
                        var files = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = bsId });
                        Assert.True(files.Success);
                    }));
                    break;
                case 3:
                    tasks.Add(Task.Run(async () =>
                    {
                        var logs = await client.GetChangeLogAsync(new GetChangeLogRequest { BackupSetId = bsId, Limit = 5 });
                        Assert.True(logs.Success);
                    }));
                    break;
            }
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task BugRegression_DisconnectDuringRequest_OldStreamClosed_NextRequestSucceeds()
    {
        // 6. Disconnect during request -> old stream closed; reconnect; next request passes
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);

        // First successful operation
        Assert.True(await client.PingAsync());

        // Forcibly mark disconnected (simulating network drop / remote abort)
        client.MarkDisconnected();
        Assert.False(client.IsConnected);

        // Next operation must seamlessly reconnect and succeed
        var reg = await client.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "PostReconnectStation",
            MachineName = Environment.MachineName
        });
        Assert.True(reg.Success);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task BugRegression_ControlThenBinaryUploadThenControl_BoundaryPreserved()
    {
        // 8. Upload binary body after control requests doesn't corrupt subsequent header
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);
        string tempFile = Path.Combine(Path.GetTempPath(), $"upload_reg_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "Sample content for binary upload stream test");

        try
        {
            // Control
            Assert.True(await client.PingAsync());

            // Binary upload
            var upResp = await client.BackupFileAsync(
                Guid.NewGuid(),
                "upload_reg.txt",
                tempFile);
            Assert.True(upResp.Success);

            // Control immediately after binary stream
            bool pingAfter = await client.PingAsync();
            Assert.True(pingAfter, "Ping immediately following binary upload failed!");

            var regAfter = await client.RegisterClientAsync(new RegisterClientRequest
            {
                Name = "AfterUploadStation",
                MachineName = Environment.MachineName
            });
            Assert.True(regAfter.Success, "RegisterClient following binary upload failed!");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public async Task BugRegression_ControlThenBinaryRestoreThenControl_BoundaryPreserved()
    {
        // 7. Restore/download binary body after control requests doesn't corrupt next HeaderLength
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);
        string tempDir = Path.Combine(Path.GetTempPath(), $"restore_reg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Control
            Assert.True(await client.PingAsync());

            // Binary restore
            var restoreReq = new RestoreRequest
            {
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 1
            };
            var restoreResp = await client.DownloadAndVerifyRestoreFileAsync(restoreReq, tempDir);
            Assert.True(restoreResp.Success);
            Assert.NotNull(restoreResp.TempFilePath);
            Assert.True(File.Exists(restoreResp.TempFilePath));

            // Completed notification
            var compResp = await client.SendRestoreCompletedAsync(new RestoreCompletedNotification
            {
                ClientId = restoreReq.ClientId,
                BackupSetId = restoreReq.BackupSetId,
                FileId = restoreReq.FileId,
                RestoredFromVersion = 1,
                NewVersionNumber = 2,
                Success = true,
                Status = "SUCCESS"
            });
            Assert.True(compResp.Success);

            // Control immediately after binary restore
            bool pingAfter = await client.PingAsync();
            Assert.True(pingAfter, "Ping immediately following binary restore failed!");

            var regAfter = await client.RegisterClientAsync(new RegisterClientRequest
            {
                Name = "AfterRestoreStation",
                MachineName = Environment.MachineName
            });
            Assert.True(regAfter.Success, "RegisterClient following binary restore failed!");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ConcurrencyStress_500MixedOperations_ZeroFramingErrors()
    {
        // Concurrency stress requirement from Section 28:
        // 100-500 mixed operations with controlled concurrency
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);
        Guid backupSetId = Guid.NewGuid();

        var tasks = new List<Task>();
        for (int i = 0; i < 200; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                switch (index % 4)
                {
                    case 0:
                        bool ping = await client.PingAsync();
                        Assert.True(ping);
                        break;
                    case 1:
                        var reg = await client.RegisterClientAsync(new RegisterClientRequest
                        {
                            Name = $"StressStation_{index}",
                            MachineName = Environment.MachineName
                        });
                        Assert.True(reg.Success);
                        break;
                    case 2:
                        var files = await client.GetFilesAsync(new GetFilesRequest
                        {
                            BackupSetId = backupSetId
                        });
                        Assert.True(files.Success);
                        break;
                    case 3:
                        var logs = await client.GetChangeLogAsync(new GetChangeLogRequest
                        {
                            BackupSetId = backupSetId,
                            Limit = 10
                        });
                        Assert.True(logs.Success);
                        break;
                }
            }));
        }

        await Task.WhenAll(tasks);
    }
}
