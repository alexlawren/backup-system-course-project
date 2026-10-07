namespace Backup.Shared.Storage;

public static class StorageErrorCodes
{
    public const string InvalidStorageRequest = "INVALID_STORAGE_REQUEST";
    public const string InvalidHash = "INVALID_HASH";
    public const string SizeMismatch = "SIZE_MISMATCH";
    public const string HashMismatch = "HASH_MISMATCH";
    public const string TransferInterrupted = "TRANSFER_INTERRUPTED";
    public const string StorageConflict = "STORAGE_CONFLICT";
    public const string StorageNotFound = "STORAGE_NOT_FOUND";
    public const string StorageIoError = "STORAGE_IO_ERROR";
}
