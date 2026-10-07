using Backup.Shared.Protocol;

namespace Backup.Shared.Storage;

public static class StorageKeyHelper
{
    private static readonly char[] InvalidChars = { '\0', '<', '>', '|', '"', '*', '?' };

    /// <summary>
    /// Constructs a deterministic set-based StorageKey:
    /// clients/{clientId}/backup-sets/{backupSetId}/files/{fileId}/versions/{versionNumber:D10}.bin
    /// </summary>
    public static string BuildStorageKey(Guid clientId, Guid backupSetId, Guid fileId, int versionNumber)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId cannot be empty.", nameof(clientId));
        }

        if (backupSetId == Guid.Empty)
        {
            throw new ArgumentException("BackupSetId cannot be empty.", nameof(backupSetId));
        }

        if (fileId == Guid.Empty)
        {
            throw new ArgumentException("FileId cannot be empty.", nameof(fileId));
        }

        if (versionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(versionNumber), "VersionNumber must be greater than zero.");
        }

        return $"clients/{clientId}/backup-sets/{backupSetId}/files/{fileId}/versions/{versionNumber:D10}.bin";
    }

    /// <summary>
    /// Safely resolves a relative StorageKey to an absolute physical OS path inside the specified storage root.
    /// Strictly protects against path traversal, absolute paths, drive roots, UNC paths, and forbidden characters.
    /// </summary>
    public static bool TryResolvePhysicalPath(string rootPath, string? storageKey, out string physicalPath, out string? errorMessage)
    {
        physicalPath = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            errorMessage = "Storage root path cannot be null or whitespace.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(storageKey))
        {
            errorMessage = "StorageKey cannot be null or whitespace.";
            return false;
        }

        if (storageKey.IndexOfAny(InvalidChars) >= 0)
        {
            errorMessage = "StorageKey contains forbidden control or wildcard characters.";
            return false;
        }

        string cleaned = storageKey.Replace('\\', '/').Trim();

        // Check Windows drive letter
        if (cleaned.Length >= 2 && char.IsLetter(cleaned[0]) && cleaned[1] == ':')
        {
            errorMessage = "Drive-rooted paths are forbidden in StorageKey.";
            return false;
        }

        // Check leading slash / UNC
        if (cleaned.StartsWith('/'))
        {
            errorMessage = "Absolute paths are forbidden in StorageKey.";
            return false;
        }

        string[] segments = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            errorMessage = "StorageKey must contain at least one valid path segment.";
            return false;
        }

        foreach (var segment in segments)
        {
            string trimmed = segment.Trim();
            if (trimmed == ".." || trimmed == ".")
            {
                errorMessage = "Directory traversal segments ('.' or '..') are forbidden in StorageKey.";
                return false;
            }
        }

        string fullRoot = Path.GetFullPath(rootPath);
        string relativeOsPath = string.Join(Path.DirectorySeparatorChar, segments);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, relativeOsPath));

        string normalizedRootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(normalizedRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Path traversal detected: resolved path escapes the storage root.";
            return false;
        }

        physicalPath = combined;
        return true;
    }

    /// <summary>
    /// Resolves physical path or throws <see cref="ProtocolException"/>.
    /// </summary>
    public static string ResolvePhysicalPath(string rootPath, string? storageKey)
    {
        if (!TryResolvePhysicalPath(rootPath, storageKey, out var physicalPath, out var errorMessage))
        {
            throw new ProtocolException($"Invalid StorageKey resolution: {errorMessage}");
        }

        return physicalPath;
    }

    /// <summary>
    /// Validates and normalizes SHA-256 hexadecimal hash string.
    /// </summary>
    public static bool TryValidateAndNormalizeSha256(string? hash, out string normalizedHash, out string? errorMessage)
    {
        normalizedHash = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(hash))
        {
            errorMessage = "SHA-256 hash cannot be null or whitespace.";
            return false;
        }

        string trimmed = hash.Trim();
        if (trimmed.Length != 64)
        {
            errorMessage = $"SHA-256 hash must be exactly 64 hexadecimal characters, got {trimmed.Length}.";
            return false;
        }

        foreach (char c in trimmed)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                errorMessage = "SHA-256 hash contains invalid non-hexadecimal character(s).";
                return false;
            }
        }

        normalizedHash = trimmed.ToLowerInvariant();
        return true;
    }
}
