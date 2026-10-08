using Microsoft.Extensions.Logging;

namespace Backup.Client.Web.Services;

public interface IFileStabilityHelper
{
    Task<bool> WaitForFileStabilityAsync(string filePath, CancellationToken cancellationToken = default);
}

public class FileStabilityHelper : IFileStabilityHelper
{
    private readonly ILogger<FileStabilityHelper>? _logger;
    private const int DefaultMaxRetries = 6;
    private const int DefaultDelayMs = 200;

    public FileStabilityHelper(ILogger<FileStabilityHelper>? logger = null)
    {
        _logger = logger;
    }

    public async Task<bool> WaitForFileStabilityAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        for (int attempt = 1; attempt <= DefaultMaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                return false;
            }

            try
            {
                // Attempt to open the file with read access and read sharing to verify it is readable
                await using (var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous))
                {
                    // Successfully opened stream. Check that file size is stable
                    long initialLength = stream.Length;
                    if (initialLength >= 0)
                    {
                        return true;
                    }
                }
            }
            catch (IOException ioEx)
            {
                _logger?.LogDebug(ioEx, "[FileStability] File '{FilePath}' is locked on attempt {Attempt}/{Max}", filePath, attempt, DefaultMaxRetries);
            }
            catch (UnauthorizedAccessException uEx)
            {
                _logger?.LogDebug(uEx, "[FileStability] File '{FilePath}' access denied on attempt {Attempt}/{Max}", filePath, attempt, DefaultMaxRetries);
            }

            if (attempt < DefaultMaxRetries)
            {
                await Task.Delay(DefaultDelayMs, cancellationToken);
            }
        }

        _logger?.LogWarning("[FileStability] File '{FilePath}' could not be stabilized after {Retries} attempts.", filePath, DefaultMaxRetries);
        return false;
    }
}
