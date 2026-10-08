namespace Backup.Shared.Protocol;

public enum MessageType
{
    Hello,
    HelloAck,
    Ping,
    Pong,
    Error,

    // Client & BackupSet registration
    RegisterClient,
    RegisterClientResult,
    CreateBackupSet,
    CreateBackupSetResult,

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
    GetChangeLog,
    ChangeLogResponse,

    RestoreRequest,
    RestoreReady,
    RestoreFile,
    RestoreCompleted,

    PrepareVersion,
    PrepareVersionResult,
    CommitVersion,
    CommitVersionResult,

    DeleteFileMetadata,
    DeleteFileMetadataResult,
    RenameFileMetadata,
    RenameFileMetadataResult,

    ResolveVersion,
    ResolveVersionResult,

    StoreFile,
    StoreFileReady,
    StoreFileResult,
    GetStoredFile,
    GetStoredFileResult,
    StoredFile
}
