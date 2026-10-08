using System.Net;
using System.Net.Sockets;
using Backup.Client.Web.Services;
using Backup.Shared.Messages;
using Backup.SyncGateway.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backup.Tests;

public class WatcherDebounceTests : IAsyncDisposable
{
    private readonly string _testDir;
    private readonly string _localFolder;
    private readonly string _indexDir;
    private readonly TestGatewayFixture _gateway;

    public WatcherDebounceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "watcher_tests_" + Guid.NewGuid().ToString("N"));
        _localFolder = Path.Combine(_testDir, "source");
        _indexDir = Path.Combine(_testDir, "index");
        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(_localFolder);
        Directory.CreateDirectory(_indexDir);

        _gateway = new TestGatewayFixture();
    }

    public async ValueTask DisposeAsync()
    {
        await _gateway.DisposeAsync();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    private SyncEngine CreateEngine(
        FakeFileSystemWatcherService watcherService,
        int debounceMs = 200)
    {
        var client = new BackupGatewayClient("127.0.0.1", _gateway.Port);
        var identityService = new ClientIdentityService(Path.Combine(_testDir, "client-state.json"));
        var indexService = new LocalFileIndexService(_indexDir);
        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var stability = new FileStabilityHelper(NullLogger<FileStabilityHelper>.Instance);

        return new SyncEngine(
            client,
            identityService,
            indexService,
            scanner,
            watcherService,
            stability,
            NullLogger<SyncEngine>.Instance,
            debounceMs);
    }

    [Fact]
    public async Task Section29_MultipleChangedEvents_CoalesceIntoOneSyncOperation()
    {
        int prepareCount = 0;
        _gateway.Metadata.PrepareVersionFunc = req =>
        {
            Interlocked.Increment(ref prepareCount);
            return Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = false, // quick return
                ExpectedVersionNumber = 1,
                FileId = Guid.NewGuid()
            });
        };

        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 150);

        string filePath = Path.Combine(_localFolder, "test.txt");
        await File.WriteAllTextAsync(filePath, "data");

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "DebounceSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);

        // Reset prepare counter after initial reconciliation
        Interlocked.Exchange(ref prepareCount, 0);

        // Fire 4 rapid Changed events
        for (int i = 0; i < 4; i++)
        {
            watcherService.EnqueueEvent(new InternalFileChangeEvent
            {
                Kind = FileChangeKind.Changed,
                FullPath = filePath,
                ObservedAtUtc = DateTime.UtcNow
            });
            await Task.Delay(20);
        }

        // Wait for debounce window (150ms + margin)
        await Task.Delay(350);

        // Should have coalesced to exactly 1 PrepareVersion call
        Assert.Equal(1, prepareCount);

        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_CreatedPlusChanged_CoalesceIntoOneUpload()
    {
        int prepareCount = 0;
        _gateway.Metadata.PrepareVersionFunc = req =>
        {
            Interlocked.Increment(ref prepareCount);
            return Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = false,
                ExpectedVersionNumber = 1,
                FileId = Guid.NewGuid()
            });
        };

        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 150);

        string filePath = Path.Combine(_localFolder, "newfile.txt");
        await File.WriteAllTextAsync(filePath, "initial");

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "CreateChangeSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);
        Interlocked.Exchange(ref prepareCount, 0);

        // Created followed immediately by Changed
        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Created,
            FullPath = filePath,
            ObservedAtUtc = DateTime.UtcNow
        });
        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Changed,
            FullPath = filePath,
            ObservedAtUtc = DateTime.UtcNow
        });

        await Task.Delay(350);

        Assert.Equal(1, prepareCount);
        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_DeletedEvent_TriggersMetadataDelete()
    {
        int deleteCount = 0;
        _gateway.Metadata.DeleteFileMetadataFunc = req =>
        {
            Interlocked.Increment(ref deleteCount);
            return Task.FromResult(new DeleteFileMetadataResponse { Success = true, FileId = Guid.NewGuid() });
        };

        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 100);

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "DeleteSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);

        string filePath = Path.Combine(_localFolder, "removed.txt");

        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Deleted,
            FullPath = filePath,
            ObservedAtUtc = DateTime.UtcNow
        });

        await Task.Delay(250);

        Assert.Equal(1, deleteCount);
        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_KnownRename_TriggersRenameMetadata()
    {
        int renameCount = 0;
        string? renamedOld = null;
        string? renamedNew = null;

        _gateway.Metadata.RenameFileMetadataFunc = req =>
        {
            Interlocked.Increment(ref renameCount);
            renamedOld = req.OldRelativePath;
            renamedNew = req.NewRelativePath;
            return Task.FromResult(new RenameFileMetadataResponse { Success = true, FileId = Guid.NewGuid() });
        };

        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 100);

        var backupSetId = Guid.NewGuid();
        var indexService = new LocalFileIndexService(_indexDir);
        var initialIndex = new LocalFileIndex { BackupSetId = backupSetId };
        initialIndex.Entries["old_name.txt"] = new LocalFileIndexEntry
        {
            RelativePath = "old_name.txt",
            Size = 10,
            LastWriteTimeUtc = DateTime.UtcNow,
            Sha256 = "dummy"
        };
        await indexService.SaveIndexAsync(initialIndex);

        var profile = new SyncProfile
        {
            BackupSetId = backupSetId,
            Name = "RenameSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        string oldPath = Path.Combine(_localFolder, "old_name.txt");
        string newPath = Path.Combine(_localFolder, "new_name.txt");
        await File.WriteAllTextAsync(oldPath, "content");

        await engine.StartAsync(profile);

        File.Move(oldPath, newPath);

        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Renamed,
            FullPath = newPath,
            OldFullPath = oldPath,
            ObservedAtUtc = DateTime.UtcNow
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (renameCount == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, renameCount);
        Assert.Equal("old_name.txt", renamedOld);
        Assert.Equal("new_name.txt", renamedNew);

        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_UnknownTempRename_TreatedAsUpsert()
    {
        int prepareCount = 0;
        _gateway.Metadata.PrepareVersionFunc = req =>
        {
            Interlocked.Increment(ref prepareCount);
            return Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = false,
                ExpectedVersionNumber = 1,
                FileId = Guid.NewGuid()
            });
        };

        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 100);

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "TempRenameSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);

        string tempPath = Path.Combine(_localFolder, "temp.tmp");
        string finalPath = Path.Combine(_localFolder, "final.docx");
        await File.WriteAllTextAsync(finalPath, "doc content");

        // Old path 'temp.tmp' is NOT in index, so it should be treated as upsert
        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Renamed,
            FullPath = finalPath,
            OldFullPath = tempPath,
            ObservedAtUtc = DateTime.UtcNow
        });

        await Task.Delay(250);

        Assert.Equal(1, prepareCount);
        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_EventsForDifferentFiles_RemainIndependent()
    {
        var processedPaths = new HashSet<string>();
        _gateway.Metadata.PrepareVersionFunc = req =>
        {
            lock (processedPaths)
            {
                processedPaths.Add(req.RelativePath);
            }
            return Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = false,
                ExpectedVersionNumber = 1,
                FileId = Guid.NewGuid()
            });
        };

        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 100);

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "IndependentSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);

        string file1 = Path.Combine(_localFolder, "file1.txt");
        string file2 = Path.Combine(_localFolder, "file2.txt");
        await File.WriteAllTextAsync(file1, "one");
        await File.WriteAllTextAsync(file2, "two");

        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Changed,
            FullPath = file1,
            ObservedAtUtc = DateTime.UtcNow
        });
        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Changed,
            FullPath = file2,
            ObservedAtUtc = DateTime.UtcNow
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            lock (processedPaths)
            {
                if (processedPaths.Count == 2) break;
            }
            await Task.Delay(50);
        }

        lock (processedPaths)
        {
            Assert.Contains("file1.txt", processedPaths);
            Assert.Contains("file2.txt", processedPaths);
        }

        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_WatcherOverflowOrError_RequestsReconciliation()
    {
        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 100);

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "OverflowSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);
        Assert.False(engine.ReconciliationRequired);

        // Simulate watcher buffer overflow / error
        watcherService.TriggerOverflow();

        Assert.True(engine.ReconciliationRequired);
        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_EventGeneratedDuringInitialScan_IsNotLost()
    {
        int prepareCount = 0;
        _gateway.Metadata.PrepareVersionFunc = req =>
        {
            Interlocked.Increment(ref prepareCount);
            return Task.FromResult(new PrepareVersionResponse
            {
                Success = true,
                UploadRequired = false,
                ExpectedVersionNumber = 1,
                FileId = Guid.NewGuid()
            });
        };

        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 100);

        string latePath = Path.Combine(_localFolder, "late_arrival.txt");
        await File.WriteAllTextAsync(latePath, "arrived while scanning");

        // Pre-enqueue event before StartAsync completes
        watcherService.EnqueueEvent(new InternalFileChangeEvent
        {
            Kind = FileChangeKind.Created,
            FullPath = latePath,
            ObservedAtUtc = DateTime.UtcNow
        });

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "InitialRaceSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);

        // Wait for debounce loop to process pre-enqueued event
        await Task.Delay(300);

        Assert.True(prepareCount >= 1, "Event enqueued before/during scan must be processed and not lost.");
        await engine.StopAsync();
    }

    [Fact]
    public async Task Section29_Stop_CancelsWatcherProcessingCleanly()
    {
        var watcherService = new FakeFileSystemWatcherService();
        await using var engine = CreateEngine(watcherService, debounceMs: 100);

        var profile = new SyncProfile
        {
            BackupSetId = Guid.NewGuid(),
            Name = "CleanStopSet",
            LocalRootPath = _localFolder,
            AutomaticSyncEnabled = true
        };

        await engine.StartAsync(profile);
        Assert.True(engine.IsRunning);

        await engine.StopAsync();
        Assert.False(engine.IsRunning);
        Assert.False(watcherService.IsRunning);
    }
}

internal class FakeFileSystemWatcherService : IFileSystemWatcherService
{
    private readonly System.Threading.Channels.Channel<InternalFileChangeEvent> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<InternalFileChangeEvent>();

    public bool IsRunning { get; private set; }
    public string? WatchedPath { get; private set; }
    public System.Threading.Channels.ChannelReader<InternalFileChangeEvent> Reader => _channel.Reader;
    public event Action? OverflowOrErrorOccurred;

    public void Start(string rootPath)
    {
        WatchedPath = rootPath;
        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
    }

    public void EnqueueEvent(InternalFileChangeEvent evt)
    {
        _channel.Writer.TryWrite(evt);
    }

    public void TriggerOverflow()
    {
        OverflowOrErrorOccurred?.Invoke();
    }

    public void Dispose()
    {
        Stop();
    }
}

internal class TestGatewayFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    public FakeMetadataServiceClient Metadata { get; } = new();
    public FakeStorageServiceClient Storage { get; } = new();
    public int Port { get; }

    public TestGatewayFixture()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        await using var handler = new GatewaySessionHandler(Metadata, Storage, NullLogger<GatewaySessionHandler>.Instance);
                        await handler.ProcessConnectionAsync(c.GetStream(), _cts.Token);
                    });
                }
            }
            catch { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
