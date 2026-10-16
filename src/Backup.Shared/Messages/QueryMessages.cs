using Backup.Shared.DTOs;

namespace Backup.Shared.Messages;

public class GetFilesRequest
{
    public Guid BackupSetId { get; set; }
    public bool IncludeDeleted { get; set; } = false;
    public Guid? ClientId { get; set; }
}

public class GetFilesResponse
{
    public Guid BackupSetId { get; set; }
    public List<FileMetadataDto> Files { get; set; } = new();
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class GetVersionsRequest
{
    public Guid FileId { get; set; }
    public Guid? ClientId { get; set; }
}

public class GetVersionsResponse
{
    public Guid FileId { get; set; }
    public List<FileVersionDto> Versions { get; set; } = new();
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ResolveVersionRequest
{
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
    public Guid? ClientId { get; set; }
}

public class ResolveVersionResponse
{
    public Guid FileId { get; set; }
    public Guid BackupSetId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public FileVersionDto? Version { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class GetChangeLogRequest
{
    public Guid BackupSetId { get; set; }
    public int Limit { get; set; } = 100;
    public Guid? ClientId { get; set; }
}

public class ChangeLogEntryDto
{
    public Guid Id { get; set; }
    public Guid BackupSetId { get; set; }
    public Guid? FileId { get; set; }
    public string? RelativePath { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid? ActorClientId { get; set; }
    public string? ActorClientName { get; set; }
}

public class GetChangeLogResponse
{
    public Guid BackupSetId { get; set; }
    public List<ChangeLogEntryDto> Entries { get; set; } = new();
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
