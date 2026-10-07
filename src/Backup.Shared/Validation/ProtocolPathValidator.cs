using Backup.Shared.Protocol;

namespace Backup.Shared.Validation;

public static class ProtocolPathValidator
{
    private static readonly char[] InvalidChars = { '\0', '<', '>', '|', '"', '*', '?' };

    /// <summary>
    /// Validates and normalizes a client-provided relative path for Protocol v1.
    /// Replaces '\' with '/', trims whitespace and slashes, rejects null-bytes,
    /// absolute paths, drive-rooted paths, and directory traversal ('..').
    /// </summary>
    public static bool TryNormalizeAndValidate(string? relativePath, out string normalizedPath, out string? errorMessage)
    {
        normalizedPath = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            errorMessage = "Path cannot be null, empty, or whitespace.";
            return false;
        }

        if (relativePath.IndexOfAny(InvalidChars) >= 0)
        {
            errorMessage = "Path contains forbidden control or wildcard characters.";
            return false;
        }

        // Replace backslashes with forward slashes for uniform protocol representation
        string cleaned = relativePath.Replace('\\', '/').Trim();

        // Check for Windows drive letter root (e.g. "C:", "c:/")
        if (cleaned.Length >= 2 && char.IsLetter(cleaned[0]) && cleaned[1] == ':')
        {
            errorMessage = "Drive-rooted absolute paths are forbidden.";
            return false;
        }

        // Check for UNC path ("//server/share") or leading slash ("/root/file")
        if (cleaned.StartsWith('/'))
        {
            errorMessage = "Absolute paths are forbidden.";
            return false;
        }

        string[] segments = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            errorMessage = "Path must contain at least one valid path segment.";
            return false;
        }

        foreach (string segment in segments)
        {
            string trimmedSegment = segment.Trim();
            if (trimmedSegment == ".." || trimmedSegment == ".")
            {
                errorMessage = "Directory traversal segments ('.' or '..') are forbidden.";
                return false;
            }
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// Validates and normalizes path, throwing <see cref="ProtocolException"/> on failure.
    /// </summary>
    public static string NormalizeAndValidate(string? relativePath)
    {
        if (!TryNormalizeAndValidate(relativePath, out var normalizedPath, out var errorMessage))
        {
            throw new ProtocolException($"Invalid relative path: {errorMessage}");
        }
        return normalizedPath;
    }
}
