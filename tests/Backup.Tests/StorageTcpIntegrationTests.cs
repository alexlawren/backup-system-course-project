using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Storage;
using Backup.StorageService;
using Backup.StorageService.Configuration;
using Backup.StorageService.Storage;
using Backup.SyncGateway.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backup.Tests;

public class StorageTcpIntegrationTests : IAsyncLifetime
{
    private readonly string _testStorageRoot;
    private readonly StorageServerWorker _worker;
    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private int _port;

    public StorageTcpIntegrationTests()
    {
        _testStorageRoot = Path.Combine(Path.GetTempPath(), "backup_tcp_storage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageRoot);

        var serviceOptions = Options.Create(new StorageServiceOptions
        {
            Host = "127.0.0.1",
            Port = 0 // Ephemeral port
        });

        var loggerFactory = NullLoggerFactory.Instance;
        var storageLogger = NullLogger<FileStorageService>.Instance;
        var storageService = new FileStorageService(storageLogger, _testStorageRoot);

        var workerLogger = NullLogger<StorageServerWorker>.Instance;
        _worker = new StorageServerWorker(workerLogger, loggerFactory, serviceOptions, storageService);
    }

    public async Task InitializeAsync()
    {
        _workerTask = _worker.StartAsync(_cts.Token);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_worker.BoundPort == null || _worker.BoundPort == 0)
        {
            if (sw.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException("StorageServerWorker failed to bind port within 5s.");
            }
            await Task.Delay(20);
        }

        _port = _worker.BoundPort.Value;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _worker.StopAsync(CancellationToken.None);
        }
        catch
        {
            // Ignore
        }
        _cts.Dispose();

        try
        {
            if (Directory.Exists(_testStorageRoot))
            {
                Directory.Delete(_testStorageRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private StorageServiceClient CreateClient() => new("127.0.0.1", _port);

    [Fact]
    public async Task Hello_Handshake_Succeeds()
    {
        await using var client = CreateClient();
        var response = await client.HelloAsync("TestClient");

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Equal("Backup.StorageService", response.ServerName);
    }

    [Fact]
    public async Task BinaryFile_RoundTrip_ThroughStorageService()
    {
        await using var client = CreateClient();

        byte[] originalBytes = Encoding.UTF8.GetBytes("Hello, this is a binary payload for round-trip testing!");
        string originalSha = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = originalBytes.Length,
            ExpectedSha256 = originalSha
        };

        // 1. Store file
        using var uploadStream = new MemoryStream(originalBytes);
        var storeResult = await client.StoreFileAsync(request, uploadStream);

        Assert.True(storeResult.Success);
        Assert.NotNull(storeResult.StorageKey);
        Assert.Equal(originalBytes.Length, storeResult.ActualSize);
        Assert.Equal(originalSha, storeResult.ActualSha256);

        // Verify StorageKey convention
        string expectedKey = StorageKeyHelper.BuildStorageKey(request.ClientId, request.BackupSetId, request.FileId, 1);
        Assert.Equal(expectedKey, storeResult.StorageKey);

        // 2. Retrieve file
        using var downloadStream = new MemoryStream();
        var getResult = await client.GetStoredFileAsync(new GetStoredFileRequest { StorageKey = storeResult.StorageKey }, downloadStream);

        Assert.True(getResult.Success);
        Assert.Equal(originalBytes.Length, getResult.Size);
        Assert.Equal(originalSha, getResult.Sha256);

        byte[] downloadedBytes = downloadStream.ToArray();
        Assert.Equal(originalBytes, downloadedBytes);

        // 3. Ensure no temporary files remained
        string tempDir = Path.Combine(_testStorageRoot, ".tmp");
        Assert.Empty(Directory.GetFiles(tempDir));
    }

    [Fact]
    public async Task ZeroByteFile_RoundTrip_ThroughStorageService()
    {
        await using var client = CreateClient();

        byte[] emptyBytes = Array.Empty<byte>();
        string emptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 0,
            ExpectedSha256 = emptySha
        };

        using var uploadStream = new MemoryStream(emptyBytes);
        var storeResult = await client.StoreFileAsync(request, uploadStream);

        Assert.True(storeResult.Success);
        Assert.Equal(0, storeResult.ActualSize);
        Assert.Equal(emptySha, storeResult.ActualSha256);

        using var downloadStream = new MemoryStream();
        var getResult = await client.GetStoredFileAsync(new GetStoredFileRequest { StorageKey = storeResult.StorageKey! }, downloadStream);

        Assert.True(getResult.Success);
        Assert.Equal(0, getResult.Size);
        Assert.Equal(emptySha, getResult.Sha256);
        Assert.Empty(downloadStream.ToArray());
    }

    [Fact]
    public async Task LargeFile_Streaming_RoundTrip()
    {
        await using var client = CreateClient();

        // 8 MiB payload generated deterministically
        int size = 8 * 1024 * 1024;
        byte[] chunk = new byte[65536];
        new Random(42).NextBytes(chunk);

        // Compute expected SHA256 without loading entire 8 MiB in memory array
        using var hashStream = new RepeatingChunkStream(chunk, size);
        byte[] expectedHashBytes = await SHA256.HashDataAsync(hashStream);
        string expectedSha = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = size,
            ExpectedSha256 = expectedSha
        };

        using (var uploadStream = new RepeatingChunkStream(chunk, size))
        {
            var storeResult = await client.StoreFileAsync(request, uploadStream);
            Assert.True(storeResult.Success);
            Assert.Equal(size, storeResult.ActualSize);
            Assert.Equal(expectedSha, storeResult.ActualSha256);
        }

        // Download and verify hash on the fly
        string tempOut = Path.Combine(Path.GetTempPath(), "test_large_out_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fileOut = new FileStream(tempOut, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var getResult = await client.GetStoredFileAsync(
                    new GetStoredFileRequest { StorageKey = StorageKeyHelper.BuildStorageKey(request.ClientId, request.BackupSetId, request.FileId, 1) },
                    fileOut);

                Assert.True(getResult.Success);
                Assert.Equal(size, getResult.Size);
                Assert.Equal(expectedSha, getResult.Sha256);
            }

            // Verify file length and hash
            Assert.Equal(size, new FileInfo(tempOut).Length);
            await using (var fileIn = new FileStream(tempOut, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            {
                byte[] downloadedHash = await SHA256.HashDataAsync(fileIn);
                Assert.Equal(expectedSha, Convert.ToHexString(downloadedHash).ToLowerInvariant());
            }
        }
        finally
        {
            if (File.Exists(tempOut))
            {
                File.Delete(tempOut);
            }
        }
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task StoreFile_EmptyOperationId_Rejected(string guidStr)
    {
        await using var client = CreateClient();

        var request = new StoreFileRequest
        {
            OperationId = Guid.Parse(guidStr),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 10,
            ExpectedSha256 = new string('0', 64)
        };

        using var ms = new MemoryStream(new byte[10]);
        var result = await client.StoreFileAsync(request, ms);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.InvalidStorageRequest, result.ErrorCode);
    }

    [Fact]
    public async Task StoreFile_NegativeVersion_Rejected()
    {
        await using var client = CreateClient();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = -1,
            ExpectedSize = 10,
            ExpectedSha256 = new string('0', 64)
        };

        using var ms = new MemoryStream(new byte[10]);
        var result = await client.StoreFileAsync(request, ms);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.InvalidStorageRequest, result.ErrorCode);
    }

    [Fact]
    public async Task StoreFile_NegativeSize_Rejected()
    {
        await using var client = CreateClient();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = -5,
            ExpectedSha256 = new string('0', 64)
        };

        using var ms = new MemoryStream(new byte[10]);
        var result = await client.StoreFileAsync(request, ms);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.InvalidStorageRequest, result.ErrorCode);
    }

    [Fact]
    public async Task StoreFile_InvalidSha256_Rejected()
    {
        await using var client = CreateClient();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 10,
            ExpectedSha256 = "invalid_hash"
        };

        using var ms = new MemoryStream(new byte[10]);
        var result = await client.StoreFileAsync(request, ms);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.InvalidHash, result.ErrorCode);
    }

    [Fact]
    public async Task OneSession_CannotInterleave_TwoActiveUploads()
    {
        // Use raw TCP client to send two StoreFile requests sequentially without sending UploadFile
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", _port);
        await using var stream = tcpClient.GetStream();

        var reader = new ProtocolFrameReader();
        var writer = new ProtocolFrameWriter();

        var req1 = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 100,
            ExpectedSha256 = new string('a', 64)
        };

        await writer.WriteJsonFrameAsync(stream, MessageType.StoreFile, req1);
        var header1 = await reader.ReadHeaderAsync(stream);
        Assert.NotNull(header1);
        Assert.Equal(MessageType.StoreFileReady, header1.MessageType);
        var ready1 = await reader.ReadJsonBodyAsync<StoreFileReadyResponse>(stream, header1.BodyLength);
        Assert.True(ready1!.Accepted);

        // Send SECOND StoreFile before uploading the first file
        var req2 = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            BackupSetId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 100,
            ExpectedSha256 = new string('b', 64)
        };

        await writer.WriteJsonFrameAsync(stream, MessageType.StoreFile, req2);
        var header2 = await reader.ReadHeaderAsync(stream);
        Assert.NotNull(header2);
        Assert.Equal(MessageType.StoreFileReady, header2.MessageType);
        var ready2 = await reader.ReadJsonBodyAsync<StoreFileReadyResponse>(stream, header2.BodyLength);
        Assert.False(ready2!.Accepted);
        Assert.Equal(StorageErrorCodes.InvalidStorageRequest, ready2.ErrorCode);
    }

    [Fact]
    public async Task MultipleIndependentClients_CanStoreFilesConcurrently()
    {
        int clientCount = 5;
        var tasks = Enumerable.Range(0, clientCount).Select(async i =>
        {
            await using var client = CreateClient();

            byte[] data = Encoding.UTF8.GetBytes($"Concurrent content payload {i}");
            string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

            var request = new StoreFileRequest
            {
                OperationId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 1,
                ExpectedSize = data.Length,
                ExpectedSha256 = sha
            };

            using var uploadStream = new MemoryStream(data);
            var result = await client.StoreFileAsync(request, uploadStream);

            Assert.True(result.Success);
            Assert.Equal(sha, result.ActualSha256);

            using var downloadStream = new MemoryStream();
            var getResult = await client.GetStoredFileAsync(new GetStoredFileRequest { StorageKey = result.StorageKey! }, downloadStream);

            Assert.True(getResult.Success);
            Assert.Equal(data, downloadStream.ToArray());
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task PrematureTcpDisconnect_CleansUpTemp_AndDoesNotCreateFinalFile()
    {
        var opId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var backupSetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync("127.0.0.1", _port);
            var stream = tcpClient.GetStream();
            var reader = new ProtocolFrameReader();
            var writer = new ProtocolFrameWriter();

            var req = new StoreFileRequest
            {
                OperationId = opId,
                ClientId = clientId,
                BackupSetId = backupSetId,
                FileId = fileId,
                VersionNumber = 1,
                ExpectedSize = 100000,
                ExpectedSha256 = new string('c', 64)
            };

            await writer.WriteJsonFrameAsync(stream, MessageType.StoreFile, req);
            var readyHdr = await reader.ReadHeaderAsync(stream);
            var ready = await reader.ReadJsonBodyAsync<StoreFileReadyResponse>(stream, readyHdr!.BodyLength);
            Assert.True(ready!.Accepted);

            // Send UploadFile header announcing 100000 bytes
            var uploadHdr = new ProtocolHeader
            {
                MessageType = MessageType.UploadFile,
                BodyLength = 100000,
                ContentType = ProtocolContentTypes.ApplicationOctetStream
            };
            await writer.WriteHeaderAsync(stream, uploadHdr);

            // Send only 100 bytes and then forcibly close socket
            await stream.WriteAsync(new byte[100]);
            tcpClient.Client.Close(0); // Forcible abort
        }

        // Wait brief moment for server to process socket close
        await Task.Delay(200);

        // Verify temp folder is clean
        string tempDir = Path.Combine(_testStorageRoot, ".tmp");
        Assert.Empty(Directory.GetFiles(tempDir));

        // Verify final file not created
        string expectedKey = StorageKeyHelper.BuildStorageKey(clientId, backupSetId, fileId, 1);
        StorageKeyHelper.TryResolvePhysicalPath(_testStorageRoot, expectedKey, out var physicalPath, out _);
        Assert.False(File.Exists(physicalPath));
    }

    [Fact]
    public async Task PathTraversalInStorageKey_Rejected()
    {
        await using var client = CreateClient();

        using var ms = new MemoryStream();
        var result = await client.GetStoredFileAsync(new GetStoredFileRequest { StorageKey = "../../../boot.ini" }, ms);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.StorageNotFound, result.ErrorCode);
    }

    [Theory]
    [InlineData("ClientId")]
    [InlineData("BackupSetId")]
    [InlineData("FileId")]
    public async Task StoreFile_EmptyGuids_Rejected(string field)
    {
        await using var client = CreateClient();

        var request = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = field == "ClientId" ? Guid.Empty : Guid.NewGuid(),
            BackupSetId = field == "BackupSetId" ? Guid.Empty : Guid.NewGuid(),
            FileId = field == "FileId" ? Guid.Empty : Guid.NewGuid(),
            VersionNumber = 1,
            ExpectedSize = 10,
            ExpectedSha256 = new string('0', 64)
        };

        using var ms = new MemoryStream(new byte[10]);
        var result = await client.StoreFileAsync(request, ms);

        Assert.False(result.Success);
        Assert.Equal(StorageErrorCodes.InvalidStorageRequest, result.ErrorCode);
    }

    [Fact]
    public async Task Tcp_ExistingIdenticalVersion_ReturnsIdempotentSuccess()
    {
        await using var client = CreateClient();

        byte[] payload = Encoding.UTF8.GetBytes("Idempotent TCP test content");
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var clientId = Guid.NewGuid();
        var backupSetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var req1 = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = clientId,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = 1,
            ExpectedSize = payload.Length,
            ExpectedSha256 = hash
        };

        using (var s1 = new MemoryStream(payload))
        {
            var res1 = await client.StoreFileAsync(req1, s1);
            Assert.True(res1.Success);
        }

        // Retry with same file/version and new OperationId
        var req2 = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = clientId,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = 1,
            ExpectedSize = payload.Length,
            ExpectedSha256 = hash
        };

        using (var s2 = new MemoryStream(payload))
        {
            var res2 = await client.StoreFileAsync(req2, s2);
            Assert.True(res2.Success);
            Assert.Equal(hash, res2.ActualSha256);
        }
    }

    [Fact]
    public async Task Tcp_ExistingDifferentFile_ReturnsStorageConflict()
    {
        await using var client = CreateClient();

        byte[] payload1 = Encoding.UTF8.GetBytes("Original content");
        string hash1 = Convert.ToHexString(SHA256.HashData(payload1)).ToLowerInvariant();

        var clientId = Guid.NewGuid();
        var backupSetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var req1 = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = clientId,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = 1,
            ExpectedSize = payload1.Length,
            ExpectedSha256 = hash1
        };

        using (var s1 = new MemoryStream(payload1))
        {
            var res1 = await client.StoreFileAsync(req1, s1);
            Assert.True(res1.Success);
        }

        // Upload different content for same version
        byte[] payload2 = Encoding.UTF8.GetBytes("Conflicting different content");
        string hash2 = Convert.ToHexString(SHA256.HashData(payload2)).ToLowerInvariant();

        var req2 = new StoreFileRequest
        {
            OperationId = Guid.NewGuid(),
            ClientId = clientId,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = 1,
            ExpectedSize = payload2.Length,
            ExpectedSha256 = hash2
        };

        using (var s2 = new MemoryStream(payload2))
        {
            var res2 = await client.StoreFileAsync(req2, s2);
            Assert.False(res2.Success);
            Assert.Equal(StorageErrorCodes.StorageConflict, res2.ErrorCode);
        }
    }

    [Fact]
    public async Task Tcp_MissingStorageKey_ReturnsStorageNotFound()
    {
        await using var client = CreateClient();

        using var ms = new MemoryStream();
        var res = await client.GetStoredFileAsync(
            new GetStoredFileRequest { StorageKey = "clients/11111111-1111-1111-1111-111111111111/backup-sets/22222222-2222-2222-2222-222222222222/files/33333333-3333-3333-3333-333333333333/versions/0000000001.bin" },
            ms);

        Assert.False(res.Success);
        Assert.Equal(StorageErrorCodes.StorageNotFound, res.ErrorCode);
    }

    /// <summary>
    /// Helper stream that simulates an infinite or bounded repeating chunk stream
    /// without keeping the full buffer in memory.
    /// </summary>
    private class RepeatingChunkStream : Stream
    {
        private readonly byte[] _chunk;
        private readonly long _totalLength;
        private long _position;

        public RepeatingChunkStream(byte[] chunk, long totalLength)
        {
            _chunk = chunk;
            _totalLength = totalLength;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _totalLength;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _totalLength)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, _totalLength - _position);
            int chunkOffset = (int)(_position % _chunk.Length);
            int chunkAvailable = _chunk.Length - chunkOffset;
            int take = Math.Min(toRead, chunkAvailable);

            Array.Copy(_chunk, chunkOffset, buffer, offset, take);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _totalLength + offset,
                _ => _position
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
