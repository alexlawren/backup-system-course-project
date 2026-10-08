using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Backup.Client.Web.Services;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
using Backup.MetadataService.Services;
using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.Shared.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class RestoreDownloadTests
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    [Fact]
    public async Task ResolveExistingVersion_Succeeds_AndReturnsStorageKey()
    {
        var options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var factory = new TestDbContextFactory(options);
        var appService = new MetadataApplicationService(factory, NullLogger<MetadataApplicationService>.Instance);

        // Create client, backup set, and file
        var clientResp = await appService.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "ResolveTestClient",
            MachineName = Environment.MachineName
        });
        var bsResp = await appService.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = clientResp.ClientId,
            Name = "ResolveTestBS",
            LocalRootPath = "C:/data"
        });

        string testContent = "Hello restore version!";
        byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(testContent);
        string sha = Convert.ToHexStringLower(SHA256.HashData(contentBytes));

        var prepResp = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = bsResp.BackupSetId,
            RelativePath = "resolve/test.txt",
            Size = contentBytes.Length,
            Sha256 = sha
        });

        string storageKey = $"blobs/{Guid.NewGuid():N}";
        await appService.CommitVersionAsync(new CommitVersionRequest
        {
            OperationId = prepResp.OperationId!.Value,
            ActualSize = contentBytes.Length,
            ActualSha256 = sha,
            StorageKey = storageKey
        });

        // Resolve existing version
        var resolveResp = await appService.ResolveVersionAsync(new ResolveVersionRequest
        {
            FileId = prepResp.FileId,
            VersionNumber = 1
        });

        Assert.True(resolveResp.Success);
        Assert.NotNull(resolveResp.Version);
        Assert.Equal(1, resolveResp.Version.VersionNumber);
        Assert.Equal(contentBytes.Length, resolveResp.Version.Size);
        Assert.Equal(sha, resolveResp.Version.Sha256);
        Assert.Equal(storageKey, resolveResp.Version.StorageKey);
        Assert.Equal("resolve/test.txt", resolveResp.RelativePath);
    }

    [Fact]
    public async Task MissingVersion_ReturnsVersionNotFound()
    {
        var options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var factory = new TestDbContextFactory(options);
        var appService = new MetadataApplicationService(factory, NullLogger<MetadataApplicationService>.Instance);

        var clientResp = await appService.RegisterClientAsync(new RegisterClientRequest
        {
            Name = "MissingVerClient",
            MachineName = Environment.MachineName
        });
        var bsResp = await appService.CreateBackupSetAsync(new CreateBackupSetRequest
        {
            ClientId = clientResp.ClientId,
            Name = "MissingVerBS",
            LocalRootPath = "C:/data"
        });

        var prepResp = await appService.PrepareVersionAsync(new PrepareVersionRequest
        {
            BackupSetId = bsResp.BackupSetId,
            RelativePath = "missing/file.txt",
            Size = 10,
            Sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
        });

        // Request non-existent version 999
        var resolveResp = await appService.ResolveVersionAsync(new ResolveVersionRequest
        {
            FileId = prepResp.FileId,
            VersionNumber = 999
        });

        Assert.False(resolveResp.Success);
        Assert.Equal("VERSION_NOT_FOUND", resolveResp.ErrorCode);
    }

    [Fact]
    public async Task RestoreStream_ExactBinaryRoundTrip()
    {
        // Set up mock TCP listener that sends RestoreReady and RestoreFile
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Exact binary payload for restore test 12345");
        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(payload));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string tempDir = Path.Combine(Path.GetTempPath(), "restore_dl_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                var header = await reader.ReadHeaderAsync(stream);
                var req = await reader.ReadJsonBodyAsync<RestoreRequest>(stream, header!.BodyLength);

                // Send RestoreReady
                var ready = new RestoreReadyResponse
                {
                    FileId = req!.FileId,
                    RelativePath = "data/payload.bin",
                    VersionNumber = req.VersionNumber,
                    Size = payload.Length,
                    Sha256 = expectedSha,
                    Success = true
                };
                await writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, correlationId: header.MessageId);

                // Send RestoreFile binary frame
                var fileHeader = new ProtocolHeader
                {
                    Magic = ProtocolConstants.Magic,
                    ProtocolVersion = ProtocolConstants.Version,
                    MessageType = MessageType.RestoreFile,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = header.MessageId,
                    ContentType = ProtocolContentTypes.ApplicationOctetStream,
                    BodyLength = payload.Length,
                    SentAtUtc = DateTime.UtcNow
                };
                await writer.WriteFrameAsync(stream, fileHeader, new MemoryStream(payload));
            });

            var gatewayClient = new BackupGatewayClient("127.0.0.1", port);
            var result = await gatewayClient.DownloadAndVerifyRestoreFileAsync(new RestoreRequest
            {
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 2
            }, tempDir);

            await serverTask;

            Assert.True(result.Success);
            Assert.True(File.Exists(result.TempFilePath));
            byte[] downloaded = await File.ReadAllBytesAsync(result.TempFilePath);
            Assert.Equal(payload, downloaded);
            Assert.Equal(expectedSha, result.Sha256);

            File.Delete(result.TempFilePath);
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreLargeFile_WorksStreaming()
    {
        // 5 MB random data stream
        int size = 5 * 1024 * 1024;
        byte[] payload = new byte[size];
        Random.Shared.NextBytes(payload);
        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(payload));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string tempDir = Path.Combine(Path.GetTempPath(), "restore_large_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                var header = await reader.ReadHeaderAsync(stream);
                var req = await reader.ReadJsonBodyAsync<RestoreRequest>(stream, header!.BodyLength);

                var ready = new RestoreReadyResponse
                {
                    FileId = req!.FileId,
                    RelativePath = "large/file.dat",
                    VersionNumber = req.VersionNumber,
                    Size = size,
                    Sha256 = expectedSha,
                    Success = true
                };
                await writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, correlationId: header.MessageId);

                var fileHeader = new ProtocolHeader
                {
                    Magic = ProtocolConstants.Magic,
                    ProtocolVersion = ProtocolConstants.Version,
                    MessageType = MessageType.RestoreFile,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = header.MessageId,
                    ContentType = ProtocolContentTypes.ApplicationOctetStream,
                    BodyLength = size,
                    SentAtUtc = DateTime.UtcNow
                };
                await writer.WriteFrameAsync(stream, fileHeader, new MemoryStream(payload));
            });

            var gatewayClient = new BackupGatewayClient("127.0.0.1", port);
            var result = await gatewayClient.DownloadAndVerifyRestoreFileAsync(new RestoreRequest
            {
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 1
            }, tempDir);

            await serverTask;

            Assert.True(result.Success);
            Assert.Equal(size, result.Size);
            Assert.Equal(expectedSha, result.Sha256);
            Assert.True(File.Exists(result.TempFilePath));
            Assert.Equal(size, new FileInfo(result.TempFilePath).Length);

            File.Delete(result.TempFilePath);
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreHashMismatch_DoesNotReplaceLocalFile()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Payload content");
        string corruptedSha = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string tempDir = Path.Combine(Path.GetTempPath(), "restore_mismatch_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string targetFile = Path.Combine(tempDir, "sample.txt");
        await File.WriteAllTextAsync(targetFile, "Original local content that must NOT be replaced!");

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                var header = await reader.ReadHeaderAsync(stream);
                _ = await reader.ReadJsonBodyAsync<RestoreRequest>(stream, header!.BodyLength);

                var ready = new RestoreReadyResponse
                {
                    FileId = Guid.NewGuid(),
                    RelativePath = "sample.txt",
                    VersionNumber = 1,
                    Size = payload.Length,
                    Sha256 = corruptedSha, // Declares wrong SHA
                    Success = true
                };
                await writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, correlationId: header.MessageId);

                var fileHeader = new ProtocolHeader
                {
                    Magic = ProtocolConstants.Magic,
                    ProtocolVersion = ProtocolConstants.Version,
                    MessageType = MessageType.RestoreFile,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = header.MessageId,
                    ContentType = ProtocolContentTypes.ApplicationOctetStream,
                    BodyLength = payload.Length,
                    SentAtUtc = DateTime.UtcNow
                };
                await writer.WriteFrameAsync(stream, fileHeader, new MemoryStream(payload));
            });

            var gatewayClient = new BackupGatewayClient("127.0.0.1", port);
            var result = await gatewayClient.DownloadAndVerifyRestoreFileAsync(new RestoreRequest
            {
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 1
            }, tempDir);

            await serverTask;

            Assert.False(result.Success);
            Assert.Equal(BackupErrorCodes.HashMismatch, result.ErrorCode);

            // Verify original file is strictly untouched!
            string currentContent = await File.ReadAllTextAsync(targetFile);
            Assert.Equal("Original local content that must NOT be replaced!", currentContent);

            // Verify temporary restore file was deleted
            var tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(tmpFiles);
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreSizeMismatch_DoesNotReplaceLocalFile()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Actual size is 19");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string tempDir = Path.Combine(Path.GetTempPath(), "restore_size_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string targetFile = Path.Combine(tempDir, "size_test.txt");
        await File.WriteAllTextAsync(targetFile, "Original size test content");

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                var header = await reader.ReadHeaderAsync(stream);
                _ = await reader.ReadJsonBodyAsync<RestoreRequest>(stream, header!.BodyLength);

                var ready = new RestoreReadyResponse
                {
                    FileId = Guid.NewGuid(),
                    RelativePath = "size_test.txt",
                    VersionNumber = 1,
                    Size = 9999, // Mismatched declared size!
                    Sha256 = "1111111111111111111111111111111111111111111111111111111111111111",
                    Success = true
                };
                await writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, correlationId: header.MessageId);

                var fileHeader = new ProtocolHeader
                {
                    Magic = ProtocolConstants.Magic,
                    ProtocolVersion = ProtocolConstants.Version,
                    MessageType = MessageType.RestoreFile,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = header.MessageId,
                    ContentType = ProtocolContentTypes.ApplicationOctetStream,
                    BodyLength = 9999,
                    SentAtUtc = DateTime.UtcNow
                };
                // Server prematurely sends less data and closes
                await writer.WriteHeaderAsync(stream, fileHeader);
                await stream.WriteAsync(payload);
            });

            var gatewayClient = new BackupGatewayClient("127.0.0.1", port);
            var result = await gatewayClient.DownloadAndVerifyRestoreFileAsync(new RestoreRequest
            {
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 1
            }, tempDir);

            await serverTask;

            Assert.False(result.Success);

            // Verify original file untouched
            Assert.Equal("Original size test content", await File.ReadAllTextAsync(targetFile));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestorePathTraversal_Rejected()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Dangerous payload");
        string sha = Convert.ToHexStringLower(SHA256.HashData(payload));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string tempDir = Path.Combine(Path.GetTempPath(), "restore_traversal_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                var header = await reader.ReadHeaderAsync(stream);
                _ = await reader.ReadJsonBodyAsync<RestoreRequest>(stream, header!.BodyLength);

                // Path traversal attempt: ../escaped.txt
                var ready = new RestoreReadyResponse
                {
                    FileId = Guid.NewGuid(),
                    RelativePath = "../escaped.txt",
                    VersionNumber = 1,
                    Size = payload.Length,
                    Sha256 = sha,
                    Success = true
                };
                await writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, correlationId: header.MessageId);

                var fileHeader = new ProtocolHeader
                {
                    Magic = ProtocolConstants.Magic,
                    ProtocolVersion = ProtocolConstants.Version,
                    MessageType = MessageType.RestoreFile,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = header.MessageId,
                    ContentType = ProtocolContentTypes.ApplicationOctetStream,
                    BodyLength = payload.Length,
                    SentAtUtc = DateTime.UtcNow
                };
                await writer.WriteFrameAsync(stream, fileHeader, new MemoryStream(payload));
            });

            var gatewayClient = new BackupGatewayClient("127.0.0.1", port);
            var result = await gatewayClient.DownloadAndVerifyRestoreFileAsync(new RestoreRequest
            {
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 1
            }, tempDir);

            await serverTask;

            Assert.False(result.Success);
            Assert.Equal(BackupErrorCodes.InvalidRelativePath, result.ErrorCode);
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeletedParentDirectories_RecreatedSafely()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Nested file content");
        string sha = Convert.ToHexStringLower(SHA256.HashData(payload));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string tempDir = Path.Combine(Path.GetTempPath(), "restore_dirs_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var reader = new ProtocolFrameReader();
                var writer = new ProtocolFrameWriter();

                var header = await reader.ReadHeaderAsync(stream);
                _ = await reader.ReadJsonBodyAsync<RestoreRequest>(stream, header!.BodyLength);

                // Deeply nested relative path whose parent folders do not exist locally
                var ready = new RestoreReadyResponse
                {
                    FileId = Guid.NewGuid(),
                    RelativePath = "sub1/sub2/sub3/file.txt",
                    VersionNumber = 1,
                    Size = payload.Length,
                    Sha256 = sha,
                    Success = true
                };
                await writer.WriteJsonFrameAsync(stream, MessageType.RestoreReady, ready, correlationId: header.MessageId);

                var fileHeader = new ProtocolHeader
                {
                    Magic = ProtocolConstants.Magic,
                    ProtocolVersion = ProtocolConstants.Version,
                    MessageType = MessageType.RestoreFile,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = header.MessageId,
                    ContentType = ProtocolContentTypes.ApplicationOctetStream,
                    BodyLength = payload.Length,
                    SentAtUtc = DateTime.UtcNow
                };
                await writer.WriteFrameAsync(stream, fileHeader, new MemoryStream(payload));
            });

            var gatewayClient = new BackupGatewayClient("127.0.0.1", port);
            var result = await gatewayClient.DownloadAndVerifyRestoreFileAsync(new RestoreRequest
            {
                ClientId = Guid.NewGuid(),
                BackupSetId = Guid.NewGuid(),
                FileId = Guid.NewGuid(),
                VersionNumber = 1
            }, tempDir);

            await serverTask;

            Assert.True(result.Success);
            string parentDir = Path.GetDirectoryName(result.TargetFilePath)!;
            Assert.True(Directory.Exists(parentDir), "Parent directory must be created before file write.");
            Assert.True(File.Exists(result.TempFilePath));

            File.Delete(result.TempFilePath);
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
