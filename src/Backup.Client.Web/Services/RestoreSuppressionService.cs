using System.Collections.Concurrent;

namespace Backup.Client.Web.Services;

public interface IRestoreSuppressionService
{
    void RegisterSuppression(string relativePath, string expectedSha256, TimeSpan? ttl = null);
    bool ShouldSuppress(string relativePath, string actualSha256);
    bool IsSuppressed(string relativePath);
    void RemoveSuppression(string relativePath);
    void Clear();
}

public class RestoreSuppressionService : IRestoreSuppressionService
{
    private class SuppressedEntry
    {
        public string ExpectedSha256 { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, SuppressedEntry> _suppressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<RestoreSuppressionService>? _logger;

    public RestoreSuppressionService(ILogger<RestoreSuppressionService>? logger = null)
    {
        _logger = logger;
    }

    public void RegisterSuppression(string relativePath, string expectedSha256, TimeSpan? ttl = null)
    {
        var duration = ttl ?? TimeSpan.FromSeconds(30);
        var entry = new SuppressedEntry
        {
            ExpectedSha256 = expectedSha256.Trim().ToLowerInvariant(),
            ExpiresAtUtc = DateTime.UtcNow.Add(duration)
        };
        _suppressions[relativePath] = entry;
        _logger?.LogDebug("[RestoreSuppression] Registered suppression for '{Path}' with expected SHA {Sha} (TTL: {TTL}s)",
            relativePath, entry.ExpectedSha256, duration.TotalSeconds);
    }

    public bool IsSuppressed(string relativePath)
    {
        CleanupExpired();
        return _suppressions.ContainsKey(relativePath);
    }

    public bool ShouldSuppress(string relativePath, string actualSha256)
    {
        CleanupExpired();
        if (!_suppressions.TryGetValue(relativePath, out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow > entry.ExpiresAtUtc)
        {
            _suppressions.TryRemove(relativePath, out _);
            return false;
        }

        string normalizedActual = actualSha256.Trim().ToLowerInvariant();
        bool matches = string.Equals(entry.ExpectedSha256, normalizedActual, StringComparison.OrdinalIgnoreCase);

        if (matches)
        {
            _logger?.LogInformation("[Restore] Watcher echo suppressed for '{Path}' (matching SHA {Sha})", relativePath, actualSha256);
            return true;
        }

        _logger?.LogInformation("[Restore] File '{Path}' has new user changes (SHA {ActualSha} != expected {ExpectedSha}). Not suppressing.",
            relativePath, normalizedActual, entry.ExpectedSha256);
        return false;
    }

    public void RemoveSuppression(string relativePath)
    {
        _suppressions.TryRemove(relativePath, out _);
    }

    public void Clear()
    {
        _suppressions.Clear();
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _suppressions)
        {
            if (now > kvp.Value.ExpiresAtUtc)
            {
                _suppressions.TryRemove(kvp.Key, out _);
            }
        }
    }
}
