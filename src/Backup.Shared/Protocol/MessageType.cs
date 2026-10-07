namespace Backup.Shared.Protocol;

public enum MessageType
{
    Hello,
    HelloAck,
    Ping,
    Pong,
    Error,

    Manifest,
    SyncPlan,

    FileCreated,
    FileChanged,
    FileDeleted,
    FileRenamed,

    UploadRequired,
    UploadFile,
    UploadCompleted,
    SyncCompleted,

    GetFiles,
    FilesResponse,
    GetVersions,
    VersionsResponse,

    RestoreRequest,
    RestoreFile,
    RestoreCompleted,

    PrepareVersion,
    PrepareVersionResult,
    CommitVersion,
    CommitVersionResult,

    DeleteFileMetadata,
    RenameFileMetadata,

    ResolveVersion,
    ResolveVersionResult,

    StoreFile,
    StoreFileResult,
    GetStoredFile,
    StoredFile
}
