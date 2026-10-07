using Backup.Shared.DTOs;

namespace Backup.Shared.Messages;

public class GetFilesRequest
{
    public Guid BackupSetId { get; set; }
    public bool IncludeDeleted { get; set; } = false;
}

public class GetFilesResponse
{
    public Guid BackupSetId { get; set; }
    public List<FileMetadataDto> Files { get; set; } = new();
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public class GetVersionsRequest
{
    public Guid FileId { get; set; }
}

public class GetVersionsResponse
{
    public Guid FileId { get; set; }
    public List<FileVersionDto> Versions { get; set; } = new();
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public class ResolveVersionRequest
{
    public Guid FileId { get; set; }
    public int VersionNumber { get; set; }
}

public class ResolveVersionResponse
{
    public Guid FileId { get; set; }
    public FileVersionDto? Version { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
