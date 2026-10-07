using System.Security.Cryptography;

namespace Backup.Client.Web.Services;

public static class FileStreamingHelper
{
    private const int BufferSize = 65536; // 64 KiB

    /// <summary>
    /// Computes the SHA-256 hash of a file using asynchronous streaming with bounded memory buffering.
    /// </summary>
    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);

        byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the exact file size in bytes.
    /// </summary>
    public static long GetFileLength(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        return new FileInfo(filePath).Length;
    }
}
