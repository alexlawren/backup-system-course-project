namespace Backup.Shared.Errors;

public static class BackupErrorCodes
{
    public const string ClientNotRegistered = "CLIENT_NOT_REGISTERED";
    public const string BackupSetNotFound = "BACKUP_SET_NOT_FOUND";
    public const string InvalidLocalFile = "INVALID_LOCAL_FILE";
    public const string FileChangedDuringPrepare = "FILE_CHANGED_DURING_PREPARE";
    public const string UploadNotExpected = "UPLOAD_NOT_EXPECTED";
    public const string UploadInProgress = "UPLOAD_IN_PROGRESS";
    public const string MetadataUnavailable = "METADATA_UNAVAILABLE";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";
    public const string MetadataCommitUncertain = "METADATA_COMMIT_UNCERTAIN";
    public const string InvalidRelativePath = "INVALID_RELATIVE_PATH";
    public const string SizeMismatch = "SIZE_MISMATCH";
    public const string HashMismatch = "HASH_MISMATCH";
    public const string StorageConflict = "STORAGE_CONFLICT";
}
