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

public class GetBackupSetManifestRequest
{
    public Guid BackupSetId { get; set; }
    public Guid? ClientId { get; set; }
}

public class BackupSetManifestEntryDto
{
    public Guid FileId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public int CurrentVersionNumber { get; set; }
    public bool IsDeleted { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public Guid? LastActorClientId { get; set; }
    public string? LastActorClientName { get; set; }
}

public class GetBackupSetManifestResponse
{
    public bool Success { get; set; } = true;
    public Guid BackupSetId { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<BackupSetManifestEntryDto> Entries { get; set; } = new();
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DownloadFileVersionRequest
{
    public Guid BackupSetId { get; set; }
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public Guid? ClientId { get; set; }
}

public class DownloadFileVersionReadyResponse
{
    public bool Success { get; set; } = true;
    public Guid BackupSetId { get; set; }
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SharedFileDownloadResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string TempFilePath { get; set; } = string.Empty;
    public string TargetFilePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public Guid FileId { get; set; }
}
