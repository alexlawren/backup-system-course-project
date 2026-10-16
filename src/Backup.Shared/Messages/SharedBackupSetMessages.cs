namespace Backup.Shared.Messages;

public class JoinSharedBackupSetRequest
{
    public string ShareCode { get; set; } = string.Empty;
    public Guid? ClientId { get; set; }
}

public class JoinSharedBackupSetResponse
{
    public bool Success { get; set; } = true;
    public Guid? BackupSetId { get; set; }
    public string? Name { get; set; }
    public BackupSetAccessMode AccessMode { get; set; } = BackupSetAccessMode.Shared;
    public Guid? OwnerClientId { get; set; }
    public string? ShareCode { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class GetBackupSetMembersRequest
{
    public Guid BackupSetId { get; set; }
    public Guid? ClientId { get; set; }
}

public class BackupSetMemberDto
{
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}

public class GetBackupSetMembersResponse
{
    public Guid BackupSetId { get; set; }
    public List<BackupSetMemberDto> Members { get; set; } = new();
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
