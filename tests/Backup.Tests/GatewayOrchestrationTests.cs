using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.SyncGateway.Clients;
using Backup.SyncGateway.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class GatewayOrchestrationTests
{
    private readonly ProtocolFrameReader _reader = new();
    private readonly ProtocolFrameWriter _writer = new();
    private static readonly string DefaultSha = new('a', 64);

    private static string ComputeSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private async Task SendBinaryUploadAsync(Stream stream, byte[] bytes)
    {
        var header = new ProtocolHeader
        {
            MessageType = MessageType.UploadFile,
            ContentType = ProtocolContentTypes.ApplicationOctetStream,
            BodyLength = bytes.Length
        };
        await _writer.WriteFrameAsync(stream, header, bytes);
    }

    private async Task SendBinaryUploadWithLengthAsync(Stream stream, byte[] bytes, long declaredLength)
    {
        var header = new ProtocolHeader
        {
            MessageType = MessageType.UploadFile,
            ContentType = ProtocolContentTypes.ApplicationOctetStream,
            BodyLength = declaredLength
        };
        using var ms = new MemoryStream(bytes);
        await _writer.WriteFrameAsync(stream, header, ms);
    }

    [Fact]
    public async Task Test_01_FileChanged_NewFile_RequiresUpload()
    {
        var opId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = opId,
                FileId = fileId,
                ExpectedVersionNumber = 1
            })
        };
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        var changeReq = new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "folder/file1.txt",
            Size = 100,
            Sha256 = DefaultSha
        };

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, changeReq);

        var header = await _reader.ReadHeaderAsync(clientStream);
        Assert.NotNull(header);
        Assert.Equal(MessageType.UploadRequired, header.MessageType);

        var resp = await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, header.BodyLength);
        Assert.NotNull(resp);
        Assert.True(resp.UploadRequired);
        Assert.Equal(opId, resp.OperationId);
        Assert.Equal(fileId, resp.FileId);
        Assert.Equal(1, resp.VersionNumber);
    }

    [Fact]
    public async Task Test_02_FileChanged_SameHash_NoUploadRequired()
    {
        var fileId = Guid.NewGuid();
        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = false, // Same hash, no upload needed!
                FileId = fileId,
                ExpectedVersionNumber = 2
            })
        };
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        var changeReq = new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "folder/file1.txt",
            Size = 100,
            Sha256 = DefaultSha
        };

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, changeReq);

        var header = await _reader.ReadHeaderAsync(clientStream);
        Assert.NotNull(header);
        Assert.Equal(MessageType.UploadCompleted, header.MessageType);

        var resp = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, header.BodyLength);
        Assert.NotNull(resp);
        Assert.True(resp.Success);
        Assert.Equal(fileId, resp.FileId);
        Assert.Equal(2, resp.VersionNumber);
        Assert.Equal(0, fakeStorage.StoreFileCallCount);
    }

    [Fact]
    public async Task Test_03_GatewayNeverCallsStorage_WhenUploadRequiredIsFalse()
    {
        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = false,
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            })
        };
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        var changeReq = new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "unchanged.txt",
            Size = 50,
            Sha256 = DefaultSha
        };

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, changeReq);

        var header = await _reader.ReadHeaderAsync(clientStream);
        Assert.NotNull(header);
        Assert.Equal(0, fakeStorage.StoreFileCallCount);
    }

    [Fact]
    public async Task Test_04_ValidUploadFile_StorageSuccess_CommitSuccess()
    {
        var opId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var fileBytes = Encoding.UTF8.GetBytes("Hello World from E2E Orchestration!");
        var sha = ComputeSha256(fileBytes);

        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = opId,
                FileId = fileId,
                ExpectedVersionNumber = 1
            }),
            CommitVersionFunc = req => Task.FromResult(new CommitVersionResponse
            {
                Success = true,
                FileId = fileId,
                VersionNumber = 1,
                StorageKey = req.StorageKey
            })
        };
        var fakeStorage = new FakeStorageServiceClient
        {
            StoreFileFunc = (desc, stream) =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return Task.FromResult(new StoreFileResultResponse
                {
                    Success = true,
                    StorageKey = "blobs/2026/01/key1",
                    ActualSize = ms.Length,
                    ActualSha256 = sha
                });
            }
        };

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        var changeReq = new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "test.txt",
            Size = fileBytes.Length,
            Sha256 = sha
        };

        // 1. Send FileChanged
        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, changeReq);
        var header1 = await _reader.ReadHeaderAsync(clientStream);
        Assert.Equal(MessageType.UploadRequired, header1!.MessageType);
        await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, header1.BodyLength);

        // 2. Send UploadFile binary frame
        await SendBinaryUploadAsync(clientStream, fileBytes);

        // 3. Receive UploadCompleted
        var header2 = await _reader.ReadHeaderAsync(clientStream);
        Assert.NotNull(header2);
        Assert.Equal(MessageType.UploadCompleted, header2.MessageType);

        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, header2.BodyLength);
        Assert.NotNull(completed);
        Assert.True(completed.Success);
        Assert.Equal(1, completed.VersionNumber);
        Assert.Equal("blobs/2026/01/key1", completed.StorageKey);
        Assert.Equal(1, fakeStorage.StoreFileCallCount);
        Assert.Equal(1, fakeMetadata.CommitCallCount);
    }

    [Fact]
    public async Task Test_05_StorageFailure_CommitVersionNotCalled()
    {
        var opId = Guid.NewGuid();
        var fileBytes = new byte[64];
        var sha = ComputeSha256(fileBytes);

        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = opId,
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            })
        };
        var fakeStorage = new FakeStorageServiceClient
        {
            StoreFileFunc = (desc, stream) => Task.FromResult(new StoreFileResultResponse
            {
                Success = false,
                ErrorCode = "STORAGE_DISK_FULL",
                ErrorMessage = "Disk space exhausted"
            })
        };

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        var changeReq = new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "storage_fail.txt",
            Size = fileBytes.Length,
            Sha256 = sha
        };

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, changeReq);
        var header1 = await _reader.ReadHeaderAsync(clientStream);
        await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, header1!.BodyLength);

        await SendBinaryUploadAsync(clientStream, fileBytes);

        var header2 = await _reader.ReadHeaderAsync(clientStream);
        Assert.Equal(MessageType.UploadCompleted, header2!.MessageType);

        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, header2.BodyLength);
        Assert.NotNull(completed);
        Assert.False(completed.Success);
        Assert.Equal("STORAGE_DISK_FULL", completed.ErrorCode);
        Assert.Equal(0, fakeMetadata.CommitCallCount);
    }

    [Fact]
    public async Task Test_06_SuccessfulStoreAndCommit_ReturnsUploadCompletedSuccess()
    {
        var fileBytes = new byte[10];
        var sha = ComputeSha256(fileBytes);

        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 3
            }),
            CommitVersionFunc = req => Task.FromResult(new CommitVersionResponse
            {
                Success = true,
                FileId = Guid.NewGuid(),
                VersionNumber = 3,
                StorageKey = "my_storage_key"
            })
        };
        var fakeStorage = new FakeStorageServiceClient
        {
            StoreFileFunc = (desc, stream) => Task.FromResult(new StoreFileResultResponse
            {
                Success = true,
                StorageKey = "my_storage_key",
                ActualSize = 10,
                ActualSha256 = sha
            })
        };

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "test3.txt",
            Size = 10,
            Sha256 = sha
        });
        var h1 = await _reader.ReadHeaderAsync(clientStream);
        await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, h1!.BodyLength);

        await SendBinaryUploadAsync(clientStream, fileBytes);

        var h2 = await _reader.ReadHeaderAsync(clientStream);
        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, h2!.BodyLength);

        Assert.True(completed!.Success);
        Assert.Equal(3, completed.VersionNumber);
        Assert.Equal("my_storage_key", completed.StorageKey);
    }

    [Fact]
    public async Task Test_07_CommitTimeoutThenRetrySucceeds()
    {
        var fileBytes = new byte[5];
        var sha = ComputeSha256(fileBytes);
        int commitAttempts = 0;

        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            }),
            CommitVersionFunc = req =>
            {
                commitAttempts++;
                if (commitAttempts == 1)
                {
                    // Fail first attempt with timeout
                    throw new TimeoutException("DB connection timed out");
                }
                return Task.FromResult(new CommitVersionResponse
                {
                    Success = true,
                    VersionNumber = 1,
                    StorageKey = req.StorageKey
                });
            }
        };
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "retry.txt",
            Size = fileBytes.Length,
            Sha256 = sha
        });
        var h1 = await _reader.ReadHeaderAsync(clientStream);
        await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, h1!.BodyLength);

        await SendBinaryUploadAsync(clientStream, fileBytes);

        var h2 = await _reader.ReadHeaderAsync(clientStream);
        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, h2!.BodyLength);

        Assert.True(completed!.Success);
        Assert.Equal(2, commitAttempts); // Verified retry occurred
    }

    [Fact]
    public async Task Test_08_CommitResponseLost_IdempotentRetryDoesNotDuplicateVersion()
    {
        var fileBytes = new byte[8];
        var sha = ComputeSha256(fileBytes);
        int commitAttempts = 0;

        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            }),
            CommitVersionFunc = req =>
            {
                commitAttempts++;
                if (commitAttempts == 1)
                {
                    // Network glitch right after commit
                    throw new IOException("TCP connection reset");
                }
                // Second call returns idempotent success
                return Task.FromResult(new CommitVersionResponse
                {
                    Success = true,
                    AlreadyCommitted = true,
                    VersionNumber = 1,
                    StorageKey = req.StorageKey
                });
            }
        };
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "idempotent_retry.txt",
            Size = fileBytes.Length,
            Sha256 = sha
        });
        var h1 = await _reader.ReadHeaderAsync(clientStream);
        await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, h1!.BodyLength);

        await SendBinaryUploadAsync(clientStream, fileBytes);

        var h2 = await _reader.ReadHeaderAsync(clientStream);
        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, h2!.BodyLength);

        Assert.True(completed!.Success);
        Assert.Equal(1, completed.VersionNumber);
        Assert.Equal(2, commitAttempts);
    }

    [Fact]
    public async Task Test_09_InvalidRelativePath_RejectedBeforeDownstreamCalls()
    {
        var fakeMetadata = new FakeMetadataServiceClient();
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        var changeReq = new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "../escape/attempt.txt",
            Size = 10,
            Sha256 = DefaultSha
        };

        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, changeReq);

        var header = await _reader.ReadHeaderAsync(clientStream);
        Assert.NotNull(header);
        Assert.Equal(MessageType.UploadCompleted, header.MessageType);

        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, header.BodyLength);
        Assert.NotNull(completed);
        Assert.False(completed.Success);
        Assert.Equal(BackupErrorCodes.InvalidRelativePath, completed.ErrorCode);
        Assert.Equal(0, fakeMetadata.PrepareCallCount);
    }

    [Fact]
    public async Task Test_10_UploadFile_WithoutPendingSessionOperation_Rejected()
    {
        var fakeMetadata = new FakeMetadataServiceClient();
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        // Send unexpected binary UploadFile directly
        await SendBinaryUploadAsync(clientStream, new byte[20]);

        var header = await _reader.ReadHeaderAsync(clientStream);
        Assert.NotNull(header);
        Assert.Equal(MessageType.UploadCompleted, header.MessageType);

        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, header.BodyLength);
        Assert.NotNull(completed);
        Assert.False(completed.Success);
        Assert.Equal(BackupErrorCodes.UploadNotExpected, completed.ErrorCode);
    }

    [Fact]
    public async Task Test_11_Upload_BodyLengthMismatch_Rejected()
    {
        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            })
        };
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        // 1. Prepare with Size = 100
        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "mismatch.txt",
            Size = 100,
            Sha256 = DefaultSha
        });
        var h1 = await _reader.ReadHeaderAsync(clientStream);
        await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, h1!.BodyLength);

        // 2. Upload with declared BodyLength = 50 instead of 100
        await SendBinaryUploadWithLengthAsync(clientStream, new byte[50], 50);

        var h2 = await _reader.ReadHeaderAsync(clientStream);
        var completed = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, h2!.BodyLength);

        Assert.NotNull(completed);
        Assert.False(completed.Success);
        Assert.Equal(BackupErrorCodes.SizeMismatch, completed.ErrorCode);
    }

    [Fact]
    public async Task Test_12_MultipleClients_UseIsolatedSessionState()
    {
        var op1 = Guid.NewGuid();
        var op2 = Guid.NewGuid();

        var fakeMetadata1 = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = op1,
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            })
        };
        var fakeMetadata2 = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = op2,
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            })
        };

        await using var harness1 = await GatewayTestHarness.CreateAsync(fakeMetadata1, new FakeStorageServiceClient());
        await using var harness2 = await GatewayTestHarness.CreateAsync(fakeMetadata2, new FakeStorageServiceClient());

        await using var client1 = await harness1.ConnectClientAsync();
        await using var client2 = await harness2.ConnectClientAsync();

        await _writer.WriteJsonFrameAsync(client1, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "client1_file.txt",
            Size = 10,
            Sha256 = DefaultSha
        });

        await _writer.WriteJsonFrameAsync(client2, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "client2_file.txt",
            Size = 20,
            Sha256 = DefaultSha
        });

        var h1 = await _reader.ReadHeaderAsync(client1);
        var resp1 = await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(client1, h1!.BodyLength);

        var h2 = await _reader.ReadHeaderAsync(client2);
        var resp2 = await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(client2, h2!.BodyLength);

        Assert.Equal(op1, resp1!.OperationId);
        Assert.Equal(op2, resp2!.OperationId);
        Assert.NotEqual(resp1.OperationId, resp2.OperationId);
    }

    [Fact]
    public async Task Test_13_OneClientCannotInterleaveTwoFileUploads()
    {
        var fakeMetadata = new FakeMetadataServiceClient
        {
            PrepareVersionFunc = req => Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = true,
                OperationId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                ExpectedVersionNumber = 1
            })
        };
        var fakeStorage = new FakeStorageServiceClient();

        await using var harness = await GatewayTestHarness.CreateAsync(fakeMetadata, fakeStorage);
        await using var clientStream = await harness.ConnectClientAsync();

        // First prepare succeeds
        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "first.txt",
            Size = 10,
            Sha256 = DefaultSha
        });
        var h1 = await _reader.ReadHeaderAsync(clientStream);
        var resp1 = await _reader.ReadJsonBodyAsync<UploadRequiredResponse>(clientStream, h1!.BodyLength);
        Assert.True(resp1!.UploadRequired);

        // Before sending binary body for first, send second FileChanged
        await _writer.WriteJsonFrameAsync(clientStream, MessageType.FileChanged, new FileChangeRequest
        {
            BackupSetId = Guid.NewGuid(),
            RelativePath = "second.txt",
            Size = 20,
            Sha256 = DefaultSha
        });

        var h2 = await _reader.ReadHeaderAsync(clientStream);
        var resp2 = await _reader.ReadJsonBodyAsync<UploadCompletedResponse>(clientStream, h2!.BodyLength);

        Assert.NotNull(resp2);
        Assert.False(resp2.Success);
        Assert.Equal(BackupErrorCodes.UploadInProgress, resp2.ErrorCode);
    }
}

// ==========================================
// TEST HARNESS & FAKE CLIENT DEFINITIONS
// ==========================================

internal class GatewayTestHarness : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;
    private readonly IMetadataServiceClient _metadataClient;
    private readonly IStorageServiceClient _storageClient;
    private readonly List<TcpClient> _clients = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private GatewayTestHarness(IMetadataServiceClient metadataClient, IStorageServiceClient storageClient)
    {
        _metadataClient = metadataClient;
        _storageClient = storageClient;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _serverTask = RunAcceptLoopAsync(_cts.Token);
    }

    public static async Task<GatewayTestHarness> CreateAsync(IMetadataServiceClient metadataClient, IStorageServiceClient storageClient)
    {
        var harness = new GatewayTestHarness(metadataClient, storageClient);
        await Task.Yield();
        return harness;
    }

    public async Task<NetworkStream> ConnectClientAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        _clients.Add(client);
        return client.GetStream();
    }

    private async Task RunAcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(async () =>
                {
                    using var s = socket;
                    await using var handler = new GatewaySessionHandler(
                        _metadataClient,
                        _storageClient,
                        NullLogger<GatewaySessionHandler>.Instance);
                    await handler.ProcessConnectionAsync(s.GetStream(), token);
                }, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (var c in _clients)
        {
            c.Dispose();
        }
        try { await _serverTask; } catch { }
        _cts.Dispose();
    }
}

internal class FakeMetadataServiceClient : IMetadataServiceClient
{
    public int PrepareCallCount { get; private set; }
    public int CommitCallCount { get; private set; }

    public Func<RegisterClientRequest, Task<RegisterClientResponse>>? RegisterClientFunc { get; set; }
    public Func<CreateBackupSetRequest, Task<CreateBackupSetResponse>>? CreateBackupSetFunc { get; set; }
    public Func<PrepareVersionRequest, Task<PrepareVersionResponse>>? PrepareVersionFunc { get; set; }
    public Func<CommitVersionRequest, Task<CommitVersionResponse>>? CommitVersionFunc { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HelloResponse?> HelloAsync(string clientName = "Backup.SyncGateway", CancellationToken cancellationToken = default) =>
        Task.FromResult<HelloResponse?>(new HelloResponse { Success = true, ServerName = "FakeMetadata" });

    public Task<RegisterClientResponse> RegisterClientAsync(RegisterClientRequest request, CancellationToken cancellationToken = default)
    {
        return RegisterClientFunc != null
            ? RegisterClientFunc(request)
            : Task.FromResult(new RegisterClientResponse { Success = true, ClientId = request.ClientId ?? Guid.NewGuid() });
    }

    public Task<CreateBackupSetResponse> CreateBackupSetAsync(CreateBackupSetRequest request, CancellationToken cancellationToken = default)
    {
        return CreateBackupSetFunc != null
            ? CreateBackupSetFunc(request)
            : Task.FromResult(new CreateBackupSetResponse { Success = true, BackupSetId = Guid.NewGuid(), Name = request.Name });
    }

    public Task<PrepareVersionResponse> PrepareVersionAsync(PrepareVersionRequest request, CancellationToken cancellationToken = default)
    {
        PrepareCallCount++;
        return PrepareVersionFunc != null
            ? PrepareVersionFunc(request)
            : Task.FromResult(new PrepareVersionResponse { Success = true, UploadRequired = true, OperationId = Guid.NewGuid(), FileId = Guid.NewGuid(), ExpectedVersionNumber = 1 });
    }

    public Task<CommitVersionResponse> CommitVersionAsync(CommitVersionRequest request, CancellationToken cancellationToken = default)
    {
        CommitCallCount++;
        return CommitVersionFunc != null
            ? CommitVersionFunc(request)
            : Task.FromResult(new CommitVersionResponse { Success = true, VersionNumber = 1, StorageKey = request.StorageKey });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal class FakeStorageServiceClient : IStorageServiceClient
{
    public int StoreFileCallCount { get; private set; }

    public Func<StoreFileRequest, Stream, Task<StoreFileResultResponse>>? StoreFileFunc { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HelloResponse?> HelloAsync(string clientName = "Backup.SyncGateway", CancellationToken cancellationToken = default) =>
        Task.FromResult<HelloResponse?>(new HelloResponse { Success = true, ServerName = "FakeStorage" });

    public Task<StoreFileResultResponse> StoreFileAsync(StoreFileRequest request, Stream content, CancellationToken cancellationToken = default)
    {
        StoreFileCallCount++;
        if (StoreFileFunc != null)
        {
            return StoreFileFunc(request, content);
        }

        // Default: drain content stream and return success
        byte[] buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
        }

        return Task.FromResult(new StoreFileResultResponse
        {
            Success = true,
            StorageKey = $"blobs/{Guid.NewGuid():N}",
            ActualSize = total,
            ActualSha256 = request.ExpectedSha256
        });
    }

    public Task<GetStoredFileResultResponse> GetStoredFileAsync(GetStoredFileRequest request, Stream destinationStream, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GetStoredFileResultResponse { Success = true });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}
