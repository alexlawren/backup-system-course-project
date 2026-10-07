using System.Net;
using Backup.MetadataService.Configuration;
using Backup.MetadataService.Data;
using Backup.MetadataService.Services;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Backup.SyncGateway.Clients;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backup.Tests;

public class MetadataTcpVerificationTests
{
    private const string PostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=backup_system;Username=backup_app;Password=backup_app_dev_secret_2026";

    [Fact]
    public async Task MetadataService_OverTcpProtocolV1_CompletesFullLifecycle()
    {
        // 1. Setup DbContextFactory and Application Service
        var options = new DbContextOptionsBuilder<MetadataDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        var factory = new TestDbContextFactory(options);
        var appService = new MetadataApplicationService(factory, NullLogger<MetadataApplicationService>.Instance);

        var reader = new ProtocolFrameReader();
        var writer = new ProtocolFrameWriter();
        var dispatcher = new MetadataMessageDispatcher(appService, reader, writer, NullLogger<MetadataMessageDispatcher>.Instance);

        // Pick dynamic port for isolation
        int testPort = 5098;
        var serviceOptions = Options.Create(new MetadataServiceOptions { Host = "127.0.0.1", Port = testPort });
        var worker = new MetadataService.MetadataServerWorker(
            NullLogger<MetadataService.MetadataServerWorker>.Instance,
            serviceOptions,
            dispatcher,
            reader);

        using var cts = new CancellationTokenSource();
        var workerTask = worker.StartAsync(cts.Token);

        // Give the listener 200ms to bind
        await Task.Delay(200);

        try
        {
            // 2. Connect via MetadataServiceClient over TCP Protocol v1
            await using var client = new MetadataServiceClient("127.0.0.1", testPort);
            await client.ConnectAsync();

            // Hello handshake
            var helloAck = await client.HelloAsync("SyncGatewayTester");
            Assert.NotNull(helloAck);
            Assert.True(helloAck.Success);
            Assert.Equal("Backup.MetadataService", helloAck.ServerName);

            // Register client
            string clientName = $"TcpClient-{Guid.NewGuid():N}";
            var reg = await client.RegisterClientAsync(new RegisterClientRequest
            {
                Name = clientName,
                MachineName = "TCP-NODE-1"
            });
            Assert.True(reg.Success);

            // Create backup set
            var bs = await client.CreateBackupSetAsync(new CreateBackupSetRequest
            {
                ClientId = reg.ClientId,
                Name = "TcpBackupSet",
                LocalRootPath = "/data/projects"
            });
            Assert.True(bs.Success);

            // Prepare version
            string testPath = $"tcp_tests/manifest_{Guid.NewGuid():N}.json";
            var prep = await client.PrepareVersionAsync(new PrepareVersionRequest
            {
                BackupSetId = bs.BackupSetId,
                RelativePath = testPath,
                Size = 1024,
                Sha256 = "tcp_sha_256_v1"
            });
            Assert.True(prep.Success);
            Assert.True(prep.UploadRequired);
            Assert.NotNull(prep.OperationId);

            // Commit version
            var commit = await client.CommitVersionAsync(new CommitVersionRequest
            {
                OperationId = prep.OperationId!.Value,
                ActualSize = 1024,
                ActualSha256 = "tcp_sha_256_v1",
                StorageKey = "storage/blocks/tcp1"
            });
            Assert.True(commit.Success);
            Assert.Equal(1, commit.VersionNumber);

            // Query files
            var filesResp = await client.GetFilesAsync(new GetFilesRequest { BackupSetId = bs.BackupSetId });
            Assert.True(filesResp.Success);
            Assert.Contains(filesResp.Files, f => f.RelativePath == testPath);

            // Query versions
            var versionsResp = await client.GetVersionsAsync(new GetVersionsRequest { FileId = commit.FileId });
            Assert.True(versionsResp.Success);
            Assert.Single(versionsResp.Versions);
            Assert.Equal("tcp_sha_256_v1", versionsResp.Versions[0].Sha256);

            // Resolve version
            var resolveResp = await client.ResolveVersionAsync(new ResolveVersionRequest
            {
                FileId = commit.FileId,
                VersionNumber = 1
            });
            Assert.True(resolveResp.Success);
            Assert.NotNull(resolveResp.Version);
            Assert.Equal("storage/blocks/tcp1", resolveResp.Version.StorageKey);
        }
        finally
        {
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
