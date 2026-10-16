namespace Backup.Shared.Storage;

public static class ConflictFileNameHelper
{
    public static string SanitizeMachineName(string? machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return "CLIENT";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = machineName.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        string sanitized = new string(chars).Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "CLIENT" : sanitized;
    }

    public static string GenerateConflictFileName(string originalFileName, string machineName, DateTime timestampUtc)
    {
        string cleanMachine = SanitizeMachineName(machineName);
        string timeStr = timestampUtc.ToString("yyyy-MM-dd HHmmss");

        string extension;
        string nameWithoutExt;

        if (originalFileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".tar.gz";
            nameWithoutExt = originalFileName[..^7];
        }
        else
        {
            extension = Path.GetExtension(originalFileName);
            nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        }

        if (string.IsNullOrEmpty(extension))
        {
            return $"{originalFileName} (conflict {cleanMachine} {timeStr})";
        }

        return $"{nameWithoutExt} (conflict {cleanMachine} {timeStr}){extension}";
    }

    public static string? FindExistingConflictCopy(
        string localRootPath,
        string originalRelativePath,
        string expectedSha256)
    {
        string normRel = originalRelativePath.Replace('\\', '/');
        string dir = Path.GetDirectoryName(normRel)?.Replace('\\', '/') ?? string.Empty;
        string fileName = Path.GetFileName(normRel);
        string nameWithoutExt = fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^7]
            : Path.GetFileNameWithoutExtension(fileName);

        string fullDir = string.IsNullOrEmpty(dir) ? localRootPath : Path.Combine(localRootPath, dir);
        if (!Directory.Exists(fullDir)) return null;

        string searchPattern = $"{nameWithoutExt} (conflict *";
        foreach (var file in Directory.GetFiles(fullDir, searchPattern))
        {
            try
            {
                using var fs = File.OpenRead(file);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha.ComputeHash(fs);
                string hashStr = Convert.ToHexString(hashBytes);
                if (string.Equals(hashStr, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    string fName = Path.GetFileName(file);
                    return string.IsNullOrEmpty(dir) ? fName : $"{dir}/{fName}";
                }
            }
            catch
            {
                // Ignore read errors
            }
        }

        return null;
    }

    public static string ResolveUniqueConflictRelativePath(
        string localRootPath,
        string originalRelativePath,
        string machineName,
        DateTime timestampUtc,
        string? expectedSha256 = null)
    {
        if (!string.IsNullOrEmpty(expectedSha256))
        {
            var existing = FindExistingConflictCopy(localRootPath, originalRelativePath, expectedSha256);
            if (existing != null)
            {
                return existing;
            }
        }

        string normRel = originalRelativePath.Replace('\\', '/');
        string dir = Path.GetDirectoryName(normRel)?.Replace('\\', '/') ?? string.Empty;
        string fileName = Path.GetFileName(normRel);
        string candidateFileName = GenerateConflictFileName(fileName, machineName, timestampUtc);

        string candidateRel = string.IsNullOrEmpty(dir)
            ? candidateFileName
            : $"{dir}/{candidateFileName}";

        string fullPath = Path.Combine(localRootPath, candidateRel);
        if (!File.Exists(fullPath))
        {
            return candidateRel;
        }

        string ext;
        string nameNoExt;
        if (candidateFileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".tar.gz";
            nameNoExt = candidateFileName[..^7];
        }
        else
        {
            ext = Path.GetExtension(candidateFileName);
            nameNoExt = Path.GetFileNameWithoutExtension(candidateFileName);
        }

        int suffix = 2;
        while (true)
        {
            string suffixedName = string.IsNullOrEmpty(ext)
                ? $"{nameNoExt} ({suffix})"
                : $"{nameNoExt} ({suffix}){ext}";

            string suffixedRel = string.IsNullOrEmpty(dir)
                ? suffixedName
                : $"{dir}/{suffixedName}";

            string suffixedFull = Path.Combine(localRootPath, suffixedRel);
            if (!File.Exists(suffixedFull))
            {
                return suffixedRel;
            }
            suffix++;
        }
    }
}
