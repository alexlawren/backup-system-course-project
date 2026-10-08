using Backup.Shared.Messages;
using Backup.Shared.Validation;
using Microsoft.Extensions.Logging;

namespace Backup.Client.Web.Services;

public interface IDirectoryScanner
{
    Task<ScanResult> ScanDirectoryAsync(
        string localRootPath,
        LocalFileIndex? localIndex = null,
        CancellationToken cancellationToken = default);
}

public class ScanResult
{
    public string LocalRootPath { get; set; } = string.Empty;
    public List<ManifestFileEntry> ManifestEntries { get; set; } = new();
    public Dictionary<string, LocalFileIndexEntry> ScannedIndexEntries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int CacheHitCount { get; set; }
    public int HashComputedCount { get; set; }
}

public class DirectoryScanner : IDirectoryScanner
{
    private readonly ILogger<DirectoryScanner>? _logger;

    public DirectoryScanner(ILogger<DirectoryScanner>? logger = null)
    {
        _logger = logger;
    }

    public async Task<ScanResult> ScanDirectoryAsync(
        string localRootPath,
        LocalFileIndex? localIndex = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localRootPath) || !Directory.Exists(localRootPath))
        {
            throw new DirectoryNotFoundException($"Local root directory '{localRootPath}' does not exist.");
        }

        string canonicalRoot = Path.GetFullPath(localRootPath);
        if (!canonicalRoot.EndsWith(Path.DirectorySeparatorChar) && !canonicalRoot.EndsWith(Path.AltDirectorySeparatorChar))
        {
            canonicalRoot += Path.DirectorySeparatorChar;
        }

        var result = new ScanResult
        {
            LocalRootPath = localRootPath
        };

        var rootDirInfo = new DirectoryInfo(canonicalRoot);
        var dirsToVisit = new Queue<DirectoryInfo>();
        dirsToVisit.Enqueue(rootDirInfo);

        while (dirsToVisit.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDir = dirsToVisit.Dequeue();

            // 1. Enumerate subdirectories, skipping reparse points / symlinks to prevent escapes / loops
            IEnumerable<DirectoryInfo> subDirs;
            try
            {
                subDirs = currentDir.EnumerateDirectories();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[DirectoryScanner] Could not enumerate directories in '{Dir}'", currentDir.FullName);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    _logger?.LogInformation("[DirectoryScanner] Skipping reparse point / junction directory '{Dir}'", subDir.FullName);
                    continue;
                }

                // Verify canonical path is still inside root
                string fullSubDirPath = Path.GetFullPath(subDir.FullName);
                if (fullSubDirPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    dirsToVisit.Enqueue(subDir);
                }
                else
                {
                    _logger?.LogWarning("[DirectoryScanner] Skipping directory '{Dir}' outside root", subDir.FullName);
                }
            }

            // 2. Enumerate regular files
            IEnumerable<FileInfo> files;
            try
            {
                files = currentDir.EnumerateFiles();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[DirectoryScanner] Could not enumerate files in '{Dir}'", currentDir.FullName);
                continue;
            }

            foreach (var fileInfo in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fullFilePath = Path.GetFullPath(fileInfo.FullName);
                if (!fullFilePath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("[DirectoryScanner] Skipping file '{File}' outside root", fileInfo.FullName);
                    continue;
                }

                string relativePath = Path.GetRelativePath(canonicalRoot, fullFilePath);
                if (!ProtocolPathValidator.TryNormalizeAndValidate(relativePath, out var normalizedRelative, out var pathError))
                {
                    _logger?.LogWarning("[DirectoryScanner] Skipping invalid file path '{RelPath}': {Error}", relativePath, pathError);
                    continue;
                }

                long size = fileInfo.Length;
                DateTime lastWriteUtc = fileInfo.LastWriteTimeUtc;
                string sha256;

                // Check cache
                if (localIndex != null &&
                    localIndex.Entries.TryGetValue(normalizedRelative, out var cached) &&
                    cached.Size == size &&
                    cached.LastWriteTimeUtc == lastWriteUtc &&
                    !string.IsNullOrWhiteSpace(cached.Sha256))
                {
                    sha256 = cached.Sha256;
                    result.CacheHitCount++;
                }
                else
                {
                    try
                    {
                        sha256 = await FileStreamingHelper.ComputeFileSha256Async(fullFilePath, cancellationToken);
                        result.HashComputedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[DirectoryScanner] Error computing SHA-256 for '{Path}'", fullFilePath);
                        continue;
                    }
                }

                var manifestEntry = new ManifestFileEntry
                {
                    RelativePath = normalizedRelative,
                    Size = size,
                    LastWriteTimeUtc = lastWriteUtc,
                    Sha256 = sha256
                };
                result.ManifestEntries.Add(manifestEntry);

                var indexEntry = new LocalFileIndexEntry
                {
                    RelativePath = normalizedRelative,
                    Size = size,
                    LastWriteTimeUtc = lastWriteUtc,
                    Sha256 = sha256
                };
                result.ScannedIndexEntries[normalizedRelative] = indexEntry;
            }
        }

        _logger?.LogInformation("[DirectoryScanner] Scanned {Count} files in '{Root}' (Cache hits: {Hits}, Hashes computed: {Computed})",
            result.ManifestEntries.Count, localRootPath, result.CacheHitCount, result.HashComputedCount);

        return result;
    }
}
