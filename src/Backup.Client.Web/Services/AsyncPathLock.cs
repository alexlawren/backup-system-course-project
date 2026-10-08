using System.Collections.Concurrent;

namespace Backup.Client.Web.Services;

/// <summary>
/// Provides per-path asynchronous locking so that operations on the same
/// relative path are serialized without blocking independent paths.
/// </summary>
public class AsyncPathLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> LockAsync(string path, CancellationToken cancellationToken = default)
    {
        string normalizedKey = path.Trim().Replace('\\', '/').ToLowerInvariant();
        var semaphore = _locks.GetOrAdd(normalizedKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
