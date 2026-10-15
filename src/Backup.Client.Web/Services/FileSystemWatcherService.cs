using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Backup.Client.Web.Services;

public enum FileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

public class InternalFileChangeEvent
{
    public FileChangeKind Kind { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string? OldFullPath { get; init; }
    public DateTime ObservedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface IFileSystemWatcherService : IDisposable
{
    bool IsRunning { get; }
    string? WatchedPath { get; }
    string? CurrentWatchedDirectory => WatchedPath;
    ChannelReader<InternalFileChangeEvent> Reader { get; }
    event Action? OverflowOrErrorOccurred;

    void Start(string rootPath);
    void Stop();
    void StopWatching(string? expectedPath = null) => Stop();
    void StartWatching(string rootPath) => Start(rootPath);
}

public class FileSystemWatcherService : IFileSystemWatcherService
{
    private readonly ILogger<FileSystemWatcherService>? _logger;
    private FileSystemWatcher? _watcher;
    private Channel<InternalFileChangeEvent> _channel;
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public string? WatchedPath { get; private set; }
    public string? CurrentWatchedDirectory => IsRunning ? WatchedPath : null;
    public ChannelReader<InternalFileChangeEvent> Reader => _channel.Reader;
    public event Action? OverflowOrErrorOccurred;

    public FileSystemWatcherService(ILogger<FileSystemWatcherService>? logger = null)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<InternalFileChangeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void StartWatching(string rootPath) => Start(rootPath);

    public void StopWatching(string? expectedPath = null)
    {
        lock (_lock)
        {
            if (expectedPath == null || string.Equals(WatchedPath, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                Stop();
            }
        }
    }

    public void Start(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        lock (_lock)
        {
            Stop();

            if (!Directory.Exists(rootPath))
            {
                Directory.CreateDirectory(rootPath);
            }

            WatchedPath = Path.GetFullPath(rootPath);
            _channel = Channel.CreateUnbounded<InternalFileChangeEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _watcher = new FileSystemWatcher(WatchedPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size,
                InternalBufferSize = 65536 // 64 KiB moderate buffer
            };

            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;

            _watcher.EnableRaisingEvents = true;
            IsRunning = true;
            _logger?.LogInformation("[FileWatcher] Started watching directory '{Path}'", WatchedPath);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnCreated;
                _watcher.Changed -= OnChanged;
                _watcher.Deleted -= OnDeleted;
                _watcher.Renamed -= OnRenamed;
                _watcher.Error -= OnError;
                _watcher.Dispose();
                _watcher = null;
            }

            string? oldWatched = WatchedPath;
            WatchedPath = null;
            IsRunning = false;
            _channel.Writer.TryComplete();
            _logger?.LogInformation("[FileWatcher] Stopped watching directory '{Path}'", oldWatched);
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        // Don't enqueue directory changes, only potential file changes
        _channel.Writer.TryWrite(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Created,
            FullPath = e.FullPath,
            ObservedAtUtc = DateTime.UtcNow
        });
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _channel.Writer.TryWrite(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Changed,
            FullPath = e.FullPath,
            ObservedAtUtc = DateTime.UtcNow
        });
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        _channel.Writer.TryWrite(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Deleted,
            FullPath = e.FullPath,
            ObservedAtUtc = DateTime.UtcNow
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        _channel.Writer.TryWrite(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Renamed,
            FullPath = e.FullPath,
            OldFullPath = e.OldFullPath,
            ObservedAtUtc = DateTime.UtcNow
        });
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger?.LogWarning(e.GetException(), "[FileWatcher] FileSystemWatcher error or internal buffer overflow: {Message}", e.GetException().Message);
        OverflowOrErrorOccurred?.Invoke();
    }

    public void Dispose()
    {
        Stop();
    }
}
