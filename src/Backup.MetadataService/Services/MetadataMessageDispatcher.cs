using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace Backup.MetadataService.Services;

public class MetadataMessageDispatcher
{
    private readonly IMetadataApplicationService _appService;
    private readonly ProtocolFrameReader _reader;
    private readonly ProtocolFrameWriter _writer;
    private readonly ILogger<MetadataMessageDispatcher> _logger;

    public MetadataMessageDispatcher(
        IMetadataApplicationService appService,
        ProtocolFrameReader reader,
        ProtocolFrameWriter writer,
        ILogger<MetadataMessageDispatcher> logger)
    {
        _appService = appService;
        _reader = reader;
        _writer = writer;
        _logger = logger;
    }

    public async Task DispatchAsync(ProtocolHeader header, Stream stream, CancellationToken cancellationToken)
    {
        switch (header.MessageType)
        {
            case MessageType.Hello:
                var helloReq = await _reader.ReadJsonBodyAsync<HelloRequest>(stream, header.BodyLength, cancellationToken);
                _logger.LogInformation("[MetadataService] Received Hello from '{ClientName}'", helloReq?.ClientName ?? "Unknown");
                var helloAck = new HelloResponse
                {
                    ServerName = "Backup.MetadataService",
                    Version = "1.0",
                    Success = true,
                    Message = "MetadataService is ready."
                };
                await _writer.WriteJsonFrameAsync(stream, MessageType.HelloAck, helloAck, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.Ping:
                var pingMsg = await _reader.ReadJsonBodyAsync<PingMessage>(stream, header.BodyLength, cancellationToken);
                _logger.LogInformation("[MetadataService] Received Ping (MsgId: {MessageId})", header.MessageId);
                var pongMsg = new PingMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                await _writer.WriteJsonFrameAsync(stream, MessageType.Pong, pongMsg, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.RegisterClient:
                var regReq = await _reader.ReadJsonBodyAsync<RegisterClientRequest>(stream, header.BodyLength, cancellationToken);
                var regResp = await _appService.RegisterClientAsync(regReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.RegisterClientResult, regResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.CreateBackupSet:
                var bsReq = await _reader.ReadJsonBodyAsync<CreateBackupSetRequest>(stream, header.BodyLength, cancellationToken);
                var bsResp = await _appService.CreateBackupSetAsync(bsReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.CreateBackupSetResult, bsResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.PrepareVersion:
                var prepReq = await _reader.ReadJsonBodyAsync<PrepareVersionRequest>(stream, header.BodyLength, cancellationToken);
                var prepResp = await _appService.PrepareVersionAsync(prepReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.PrepareVersionResult, prepResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.CommitVersion:
                var commitReq = await _reader.ReadJsonBodyAsync<CommitVersionRequest>(stream, header.BodyLength, cancellationToken);
                var commitResp = await _appService.CommitVersionAsync(commitReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.CommitVersionResult, commitResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.DeleteFileMetadata:
                var delReq = await _reader.ReadJsonBodyAsync<DeleteFileMetadataRequest>(stream, header.BodyLength, cancellationToken);
                var delResp = await _appService.DeleteFileMetadataAsync(delReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.DeleteFileMetadataResult, delResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.RenameFileMetadata:
                var renReq = await _reader.ReadJsonBodyAsync<RenameFileMetadataRequest>(stream, header.BodyLength, cancellationToken);
                var renResp = await _appService.RenameFileMetadataAsync(renReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.RenameFileMetadataResult, renResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.GetFiles:
                var gfReq = await _reader.ReadJsonBodyAsync<GetFilesRequest>(stream, header.BodyLength, cancellationToken);
                var gfResp = await _appService.GetFilesAsync(gfReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.FilesResponse, gfResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.GetVersions:
                var gvReq = await _reader.ReadJsonBodyAsync<GetVersionsRequest>(stream, header.BodyLength, cancellationToken);
                var gvResp = await _appService.GetVersionsAsync(gvReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.VersionsResponse, gvResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.ResolveVersion:
                var rvReq = await _reader.ReadJsonBodyAsync<ResolveVersionRequest>(stream, header.BodyLength, cancellationToken);
                var rvResp = await _appService.ResolveVersionAsync(rvReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.ResolveVersionResult, rvResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.GetChangeLog:
                var clReq = await _reader.ReadJsonBodyAsync<GetChangeLogRequest>(stream, header.BodyLength, cancellationToken);
                var clResp = await _appService.GetChangeLogsAsync(clReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.ChangeLogResponse, clResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.JoinSharedBackupSet:
                var joinReq = await _reader.ReadJsonBodyAsync<JoinSharedBackupSetRequest>(stream, header.BodyLength, cancellationToken);
                var joinResp = await _appService.JoinSharedBackupSetAsync(joinReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.JoinSharedBackupSetResult, joinResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            case MessageType.GetBackupSetMembers:
                var memReq = await _reader.ReadJsonBodyAsync<GetBackupSetMembersRequest>(stream, header.BodyLength, cancellationToken);
                var memResp = await _appService.GetBackupSetMembersAsync(memReq ?? new(), cancellationToken);
                await _writer.WriteJsonFrameAsync(stream, MessageType.GetBackupSetMembersResult, memResp, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;

            default:
                _logger.LogWarning("[MetadataService] Unhandled MessageType: {MessageType}", header.MessageType);
                if (header.BodyLength > 0)
                {
                    await _reader.ReadBodyBytesAsync(stream, header.BodyLength, cancellationToken);
                }
                var errorMsg = new ErrorMessage
                {
                    Code = "NOT_IMPLEMENTED",
                    Message = $"MessageType '{header.MessageType}' is not handled in Change-set #2."
                };
                await _writer.WriteJsonFrameAsync(stream, MessageType.Error, errorMsg, correlationId: header.MessageId, cancellationToken: cancellationToken);
                break;
        }
    }
}
