namespace Backup.SyncGateway.Orchestration;

public class ActiveUploadContext
{
    public Guid OperationId { get; set; }
    public Guid ClientId { get; set; }
    public Guid BackupSetId { get; set; }
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public long ExpectedSize { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
    public Guid PrepareMessageId { get; set; }
    public Guid? ActorClientId { get; set; }
    public Backup.Shared.Messages.BackupChangeOrigin ChangeOrigin { get; set; } = Backup.Shared.Messages.BackupChangeOrigin.Normal;
    public string? ConflictOriginalPath { get; set; }
    public int? ConflictOriginalVersion { get; set; }
}

/// <summary>
/// Per-connection session state for an active client connection on SyncGateway.
/// Strictly scoped to one TCP connection to prevent global state leaks or interleaving.
/// </summary>
public class GatewayClientSession
{
    public Guid? ClientId { get; set; }
    public string? MachineName { get; set; }
    public ActiveUploadContext? ActiveUpload { get; set; }

    public void ClearActiveUpload()
    {
        ActiveUpload = null;
    }
}
