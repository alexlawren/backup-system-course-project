using System.Collections.Concurrent;
using Backup.Shared.Messages;
using Backup.Shared.Validation;
using Microsoft.Extensions.Logging;

namespace Backup.Client.Web.Services;

public class SyncLogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = string.Empty;
}

public interface ISyncEngine : IAsyncDisposable, IDisposable
{
    bool IsRunning { get; }
    string? CurrentLocalRootPath { get; }
    Guid? CurrentBackupSetId { get; }
    string? CurrentBackupSetName { get; }
    bool ReconciliationRequired { get; }
    DateTime? LastReconciliationUtc { get; }
    string? LastSyncResult { get; }
    int PendingEventCount { get; }
    IReadOnlyList<SyncLogEntry> RecentEvents { get; }

    Task StartAsync(SyncProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ReconcileNowAsync(CancellationToken cancellationToken = default);
}

public class DebouncedFileEvent
{
    public string Key { get; set; } = string.Empty;
    public FileChangeKind Kind { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string? OldFullPath { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public class SyncEngine : ISyncEngine
{
    private readonly BackupGatewayClient _gatewayClient;
    private readonly IClientIdentityService _identityService;
    private readonly ILocalFileIndexService _indexService;
    private readonly IDirectoryScanner _scanner;
    private readonly IFileSystemWatcherService _watcherService;
    private readonly IFileStabilityHelper _stabilityHelper;
    private readonly ILogger<SyncEngine> _logger;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Dictionary<string, DebouncedFileEvent> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SyncLogEntry> _recentEvents = new();
    private readonly object _logLock = new();

    private CancellationTokenSource? _loopCts;
    private Task? _processingLoopTask;

    private SyncProfile? _activeProfile;
    private int _debounceDelayMs = 750;

    public bool IsRunning { get; private set; }
    public string? CurrentLocalRootPath => _activeProfile?.LocalRootPath;
    public Guid? CurrentBackupSetId => _activeProfile?.BackupSetId;
    public string? CurrentBackupSetName => _activeProfile?.Name;
    public bool ReconciliationRequired { get; private set; }
    public DateTime? LastReconciliationUtc { get; private set; }
    public string? LastSyncResult { get; private set; }
    public int PendingEventCount
    {
        get
        {
            lock (_pendingEvents)
            {
                return _pendingEvents.Count;
            }
        }
    }

    public IReadOnlyList<SyncLogEntry> RecentEvents
    {
        get
        {
            lock (_logLock)
            {
                return _recentEvents.ToList();
            }
        }
    }

    public SyncEngine(
        BackupGatewayClient gatewayClient,
        IClientIdentityService identityService,
        ILocalFileIndexService indexService,
        IDirectoryScanner scanner,
        IFileSystemWatcherService watcherService,
        IFileStabilityHelper stabilityHelper,
        ILogger<SyncEngine> logger,
        int debounceDelayMs = 750)
    {
        _gatewayClient = gatewayClient;
        _identityService = identityService;
        _indexService = indexService;
        _scanner = scanner;
        _watcherService = watcherService;
        _stabilityHelper = stabilityHelper;
        _logger = logger;
        _debounceDelayMs = debounceDelayMs;

        _watcherService.OverflowOrErrorOccurred += OnWatcherOverflowOrError;
    }

    private void Log(string message, string level = "INFO")
    {
        if (level == "ERROR")
        {
            _logger.LogError("{Message}", message);
        }
        else if (level == "WARN")
        {
            _logger.LogWarning("{Message}", message);
        }
        else
        {
            _logger.LogInformation("{Message}", message);
        }

        lock (_logLock)
        {
            _recentEvents.Add(new SyncLogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Level = level,
                Message = message
            });

            if (_recentEvents.Count > 200)
            {
                _recentEvents.RemoveRange(0, _recentEvents.Count - 200);
            }
        }
    }

    private void OnWatcherOverflowOrError()
    {
        Log("[FileWatcher] Watcher overflow or error detected. Full reconciliation required.", "WARN");
        ReconciliationRequired = true;
    }

    public async Task StartAsync(SyncProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.BackupSetId == Guid.Empty)
        {
            throw new ArgumentException("BackupSetId cannot be empty.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.LocalRootPath) || !Directory.Exists(profile.LocalRootPath))
        {
            throw new DirectoryNotFoundException($"LocalRootPath '{profile.LocalRootPath}' does not exist on disk.");
        }

        await StopAsync(cancellationToken);

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            _activeProfile = profile;

            // Ensure client identity is registered
            var identity = await _identityService.GetStateAsync(cancellationToken);
            if (identity.ClientId == null)
            {
                var reg = await _gatewayClient.RegisterClientAsync(new RegisterClientRequest
                {
                    Name = identity.ClientName,
                    MachineName = identity.MachineName
                }, cancellationToken);

                if (!reg.Success || reg.ClientId == Guid.Empty)
                {
                    throw new InvalidOperationException($"Could not register client identity with Gateway: {reg.ErrorMessage}");
                }

                await _identityService.SaveStateAsync(reg.ClientId, identity.MachineName, identity.ClientName, cancellationToken);
            }

            Log($"[SyncEngine] Starting automatic synchronization for '{profile.Name}' ({profile.LocalRootPath})");

            // 1. Start FileSystemWatcher so that events during initial reconciliation are not lost
            _watcherService.Start(profile.LocalRootPath);

            // 2. Perform initial reconciliation
            await ExecuteReconciliationInternalAsync(cancellationToken);

            // 3. Start background event processing loop
            _loopCts = new CancellationTokenSource();
            _processingLoopTask = Task.Run(() => RunWatcherProcessingLoopAsync(_loopCts.Token));

            IsRunning = true;
            Log("[SyncEngine] Automatic synchronization running.");
        }
        catch (Exception ex)
        {
            Log($"[SyncEngine] Failed to start automatic synchronization: {ex.Message}", "ERROR");
            _watcherService.Stop();
            IsRunning = false;
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning && _processingLoopTask == null)
        {
            return;
        }

        Log("[SyncEngine] Stopping automatic synchronization...");

        // 1. Stop watcher to cease new incoming events
        _watcherService.Stop();

        // 2. Cancel loop
        if (_loopCts != null)
        {
            _loopCts.Cancel();
            if (_processingLoopTask != null)
            {
                try
                {
                    await _processingLoopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch
                {
                    // Ignore cancellation or timeout
                }
            }

            _loopCts.Dispose();
            _loopCts = null;
            _processingLoopTask = null;
        }

        lock (_pendingEvents)
        {
            _pendingEvents.Clear();
        }

        IsRunning = false;
        Log("[SyncEngine] Automatic synchronization stopped.");
    }

    public async Task ReconcileNowAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            if (_activeProfile == null)
            {
                throw new InvalidOperationException("No active sync profile to reconcile.");
            }

            await ExecuteReconciliationInternalAsync(cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task ExecuteReconciliationInternalAsync(CancellationToken cancellationToken)
    {
        if (_activeProfile == null)
        {
            return;
        }

        string root = _activeProfile.LocalRootPath;
        Guid backupSetId = _activeProfile.BackupSetId;

        if (!Directory.Exists(root))
        {
            LastSyncResult = $"Local directory '{root}' does not exist.";
            Log($"[SyncEngine] Reconciliation aborted: directory '{root}' missing.", "WARN");
            return;
        }

        Log($"[SyncEngine] Initial reconciliation started for BackupSet {backupSetId}");

        try
        {
            // 1. Load local index cache
            var localIndex = await _indexService.LoadIndexAsync(backupSetId, cancellationToken);

            // 2. Scan directory
            var scanResult = await _scanner.ScanDirectoryAsync(root, localIndex, cancellationToken);
            Log($"[SyncEngine] Scanned {scanResult.ManifestEntries.Count} files in directory (Cache hits: {scanResult.CacheHitCount}, Hashes computed: {scanResult.HashComputedCount})");

            // 3. Build & send Manifest
            var identity = await _identityService.GetStateAsync(cancellationToken);
            var manifestReq = new ManifestRequest
            {
                ClientId = identity.ClientId ?? Guid.Empty,
                BackupSetId = backupSetId,
                Files = scanResult.ManifestEntries
            };

            var syncPlan = await _gatewayClient.SendManifestAsync(manifestReq, cancellationToken);
            if (!syncPlan.Success)
            {
                LastSyncResult = $"Manifest failed: {syncPlan.ErrorMessage}";
                Log($"[SyncEngine] Manifest reconciliation failed: {syncPlan.ErrorMessage}", "ERROR");
                ReconciliationRequired = true;
                return;
            }

            Log($"[SyncEngine] Plan: {syncPlan.Upload.Count} upload, {syncPlan.Delete.Count} delete, {syncPlan.UnchangedCount} unchanged");

            // Fast lookup for precomputed scan items
            var scanLookup = scanResult.ManifestEntries.ToDictionary(m => m.RelativePath, m => m, StringComparer.OrdinalIgnoreCase);

            // 4. Sequentially execute Uploads
            int uploadedCount = 0;
            foreach (var item in syncPlan.Upload)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fullPath = Path.Combine(root, item.RelativePath);
                if (!File.Exists(fullPath))
                {
                    Log($"[SyncEngine] File '{item.RelativePath}' disappeared before upload ({item.Reason}). Skipping.", "WARN");
                    continue;
                }

                // Check stability
                bool stable = await _stabilityHelper.WaitForFileStabilityAsync(fullPath, cancellationToken);
                if (!stable)
                {
                    Log($"[SyncEngine] File '{item.RelativePath}' locked by another process. Will retry later.", "WARN");
                    ReconciliationRequired = true;
                    continue;
                }

                scanLookup.TryGetValue(item.RelativePath, out var scanEntry);

                var uploadResp = await _gatewayClient.BackupFileAsync(
                    backupSetId,
                    item.RelativePath,
                    fullPath,
                    identity.ClientId,
                    cancellationToken,
                    scanEntry?.Sha256,
                    scanEntry?.Size);

                if (uploadResp.Success)
                {
                    uploadedCount++;
                    Log($"[SyncEngine] Uploaded {item.RelativePath} ({item.Reason})");

                    // Update index cache entry
                    long len = FileStreamingHelper.GetFileLength(fullPath);
                    DateTime lastWrite = new FileInfo(fullPath).LastWriteTimeUtc;
                    string sha = scanEntry?.Sha256 ?? await FileStreamingHelper.ComputeFileSha256Async(fullPath, cancellationToken);

                    localIndex.Entries[item.RelativePath] = new LocalFileIndexEntry
                    {
                        RelativePath = item.RelativePath,
                        Size = len,
                        LastWriteTimeUtc = lastWrite,
                        Sha256 = sha
                    };
                }
                else
                {
                    Log($"[SyncEngine] Upload failed for '{item.RelativePath}': {uploadResp.ErrorMessage}", "WARN");
                    ReconciliationRequired = true;
                }
            }

            // 5. Sequentially execute Deletes
            int deletedCount = 0;
            foreach (var delPath in syncPlan.Delete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var delResp = await _gatewayClient.DeleteFileAsync(new FileDeletedRequest
                {
                    ClientId = identity.ClientId ?? Guid.Empty,
                    BackupSetId = backupSetId,
                    RelativePath = delPath
                }, cancellationToken);

                if (delResp.Success)
                {
                    deletedCount++;
                    Log($"[SyncEngine] Server file deleted: {delPath}");
                    localIndex.Entries.Remove(delPath);
                }
                else
                {
                    Log($"[SyncEngine] Delete failed for '{delPath}': {delResp.ErrorMessage}", "WARN");
                    ReconciliationRequired = true;
                }
            }

            // 6. Update unchanged items in index to keep cache perfectly fresh
            foreach (var kvp in scanResult.ScannedIndexEntries)
            {
                if (!localIndex.Entries.ContainsKey(kvp.Key))
                {
                    localIndex.Entries[kvp.Key] = kvp.Value;
                }
            }

            // 7. Remove any local index entries for files that no longer exist locally
            var toRemoveKeys = localIndex.Entries.Keys
                .Where(k => !scanResult.ScannedIndexEntries.ContainsKey(k))
                .ToList();
            foreach (var key in toRemoveKeys)
            {
                localIndex.Entries.Remove(key);
            }

            // 8. Save updated local index
            await _indexService.SaveIndexAsync(localIndex, cancellationToken);

            LastReconciliationUtc = DateTime.UtcNow;
            ReconciliationRequired = false;
            LastSyncResult = $"Success. Uploaded: {uploadedCount}, Deleted: {deletedCount}, Unchanged: {syncPlan.UnchangedCount}.";
            Log($"[SyncEngine] Reconciliation completed. {LastSyncResult}");
        }
        catch (Exception ex)
        {
            ReconciliationRequired = true;
            LastSyncResult = $"Reconciliation error: {ex.Message}";
            Log($"[SyncEngine] Reconciliation error: {ex.Message}", "ERROR");
        }
    }

    private async Task RunWatcherProcessingLoopAsync(CancellationToken cancellationToken)
    {
        Log("[SyncEngine] Background file watcher processing loop started.");

        var debounceDelay = TimeSpan.FromMilliseconds(_debounceDelayMs);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 1. Drain all currently available events from channel into pending dictionary
                while (_watcherService.Reader.TryRead(out var evt))
                {
                    EnqueueWatcherEvent(evt);
                }

                // 2. Check if full reconciliation was flagged by overflow/network issue
                if (ReconciliationRequired)
                {
                    Log("[SyncEngine] ReconciliationRequired flag is set. Triggering reconciliation...", "WARN");
                    await _syncLock.WaitAsync(cancellationToken);
                    try
                    {
                        await ExecuteReconciliationInternalAsync(cancellationToken);
                    }
                    finally
                    {
                        _syncLock.Release();
                    }
                }

                // 3. Find matured debounced items
                var now = DateTime.UtcNow;
                List<DebouncedFileEvent> matureItems = new();

                lock (_pendingEvents)
                {
                    foreach (var (key, item) in _pendingEvents.ToList())
                    {
                        if (now - item.LastSeenUtc >= debounceDelay)
                        {
                            matureItems.Add(item);
                            _pendingEvents.Remove(key);
                        }
                    }
                }

                // 4. Process mature items
                if (matureItems.Count > 0)
                {
                    foreach (var item in matureItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ProcessDebouncedItemAsync(item, cancellationToken);
                    }
                }

                // 5. Calculate sleep / wait duration
                int delayMs;
                lock (_pendingEvents)
                {
                    if (_pendingEvents.Count == 0)
                    {
                        delayMs = 250; // default check interval when idle
                    }
                    else
                    {
                        var earliestDue = _pendingEvents.Values.Min(e => e.LastSeenUtc) + debounceDelay;
                        var diff = (int)(earliestDue - DateTime.UtcNow).TotalMilliseconds;
                        delayMs = Math.Clamp(diff, 20, 300);
                    }
                }

                // Wait for either new incoming channel items or timer expiration
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(delayMs);

                try
                {
                    await _watcherService.Reader.WaitToReadAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timer expired or cancellation requested, continue loop
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"[SyncEngine] Error in watcher processing loop: {ex.Message}", "ERROR");
                ReconciliationRequired = true;
                await Task.Delay(500, cancellationToken);
            }
        }

        Log("[SyncEngine] Background file watcher processing loop exited.");
    }

    private void EnqueueWatcherEvent(InternalFileChangeEvent evt)
    {
        if (_activeProfile == null || string.IsNullOrWhiteSpace(_activeProfile.LocalRootPath))
        {
            return;
        }

        string root = Path.GetFullPath(_activeProfile.LocalRootPath);
        if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        string fullPath = Path.GetFullPath(evt.FullPath);

        // If event is for a directory itself, ignore
        if (Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            return;
        }

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string rel = Path.GetRelativePath(root, fullPath);
        if (!ProtocolPathValidator.TryNormalizeAndValidate(rel, out var normRelative, out _))
        {
            return;
        }

        lock (_pendingEvents)
        {
            if (_pendingEvents.TryGetValue(normRelative, out var existing))
            {
                existing.LastSeenUtc = DateTime.UtcNow;

                // Coalescing logic
                if (existing.Kind == FileChangeKind.Created && evt.Kind == FileChangeKind.Changed)
                {
                    // Created + Changed -> stays Created
                }
                else if (existing.Kind == FileChangeKind.Changed && evt.Kind == FileChangeKind.Changed)
                {
                    // Multiple Changed -> Changed
                }
                else if (existing.Kind == FileChangeKind.Created && evt.Kind == FileChangeKind.Deleted)
                {
                    // Created and then immediately deleted -> remove from pending completely
                    _pendingEvents.Remove(normRelative);
                }
                else if (existing.Kind == FileChangeKind.Renamed && evt.Kind == FileChangeKind.Changed)
                {
                    // Renamed + Changed -> keep as Renamed
                }
                else
                {
                    existing.Kind = evt.Kind;
                    existing.OldFullPath = evt.OldFullPath;
                }
            }
            else
            {
                _pendingEvents[normRelative] = new DebouncedFileEvent
                {
                    Key = normRelative,
                    Kind = evt.Kind,
                    FullPath = fullPath,
                    OldFullPath = evt.OldFullPath,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                };
            }
        }
    }

    private async Task ProcessDebouncedItemAsync(DebouncedFileEvent item, CancellationToken cancellationToken)
    {
        if (_activeProfile == null)
        {
            return;
        }

        Guid backupSetId = _activeProfile.BackupSetId;
        string root = _activeProfile.LocalRootPath;
        var identity = await _identityService.GetStateAsync(cancellationToken);

        try
        {
            switch (item.Kind)
            {
                case FileChangeKind.Created:
                case FileChangeKind.Changed:
                {
                    if (!File.Exists(item.FullPath))
                    {
                        // File disappeared before debounce finished
                        return;
                    }

                    Log($"[FileWatcher] Debounced -> sync ({item.Kind}): {item.Key}");

                    bool stable = await _stabilityHelper.WaitForFileStabilityAsync(item.FullPath, cancellationToken);
                    if (!stable)
                    {
                        Log($"[FileWatcher] File '{item.Key}' still locked after stability check. Scheduling reconciliation.", "WARN");
                        ReconciliationRequired = true;
                        return;
                    }

                    var uploadResp = await _gatewayClient.BackupFileAsync(
                        backupSetId,
                        item.Key,
                        item.FullPath,
                        identity.ClientId,
                        cancellationToken: cancellationToken);

                    if (uploadResp.Success)
                    {
                        int version = uploadResp.VersionNumber ?? 1;
                        Log($"[SyncEngine] Uploaded {item.Key} version {version}");

                        // Update index
                        var index = await _indexService.LoadIndexAsync(backupSetId, cancellationToken);
                        if (File.Exists(item.FullPath))
                        {
                            long len = FileStreamingHelper.GetFileLength(item.FullPath);
                            DateTime lastWrite = new FileInfo(item.FullPath).LastWriteTimeUtc;
                            string sha = await FileStreamingHelper.ComputeFileSha256Async(item.FullPath, cancellationToken);

                            index.Entries[item.Key] = new LocalFileIndexEntry
                            {
                                RelativePath = item.Key,
                                Size = len,
                                LastWriteTimeUtc = lastWrite,
                                Sha256 = sha
                            };
                            await _indexService.SaveIndexAsync(index, cancellationToken);
                        }
                    }
                    else
                    {
                        Log($"[SyncEngine] Failed real-time upload for '{item.Key}': {uploadResp.ErrorMessage}", "WARN");
                        ReconciliationRequired = true;
                    }
                    break;
                }

                case FileChangeKind.Deleted:
                {
                    Log($"[FileWatcher] Debounced -> delete: {item.Key}");

                    var delResp = await _gatewayClient.DeleteFileAsync(new FileDeletedRequest
                    {
                        ClientId = identity.ClientId ?? Guid.Empty,
                        BackupSetId = backupSetId,
                        RelativePath = item.Key
                    }, cancellationToken);

                    if (delResp.Success)
                    {
                        Log($"[SyncEngine] Deleted metadata for '{item.Key}'");
                        var index = await _indexService.LoadIndexAsync(backupSetId, cancellationToken);
                        index.Entries.Remove(item.Key);
                        await _indexService.SaveIndexAsync(index, cancellationToken);
                    }
                    else
                    {
                        Log($"[SyncEngine] Delete failed for '{item.Key}': {delResp.ErrorMessage}", "WARN");
                        ReconciliationRequired = true;
                    }
                    break;
                }

                case FileChangeKind.Renamed:
                {
                    string oldRelative = string.Empty;
                    if (!string.IsNullOrWhiteSpace(item.OldFullPath))
                    {
                        string oldRel = Path.GetRelativePath(root, item.OldFullPath);
                        if (ProtocolPathValidator.TryNormalizeAndValidate(oldRel, out var normOld, out _))
                        {
                            oldRelative = normOld;
                        }
                    }

                    var index = await _indexService.LoadIndexAsync(backupSetId, cancellationToken);

                    // Check if old relative path is known in our index/state
                    bool isKnownRename = !string.IsNullOrWhiteSpace(oldRelative) && index.Entries.ContainsKey(oldRelative);

                    if (isKnownRename)
                    {
                        Log($"[FileWatcher] Renamed: {oldRelative} -> {item.Key}");

                        var renResp = await _gatewayClient.RenameFileAsync(new FileRenamedRequest
                        {
                            ClientId = identity.ClientId ?? Guid.Empty,
                            BackupSetId = backupSetId,
                            OldRelativePath = oldRelative,
                            NewRelativePath = item.Key
                        }, cancellationToken);

                        if (renResp.Success)
                        {
                            Log($"[SyncEngine] Rename metadata updated: {oldRelative} -> {item.Key}");
                            if (index.Entries.TryGetValue(oldRelative, out var entry))
                            {
                                index.Entries.Remove(oldRelative);
                                entry.RelativePath = item.Key;
                                if (File.Exists(item.FullPath))
                                {
                                    entry.LastWriteTimeUtc = new FileInfo(item.FullPath).LastWriteTimeUtc;
                                }
                                index.Entries[item.Key] = entry;
                                await _indexService.SaveIndexAsync(index, cancellationToken);
                            }
                        }
                        else
                        {
                            Log($"[SyncEngine] Rename failed for '{oldRelative}' -> '{item.Key}': {renResp.ErrorMessage}", "WARN");
                            ReconciliationRequired = true;
                        }
                    }
                    else
                    {
                        // Unknown rename (e.g. editor temp file renamed to real file).
                        // Treat new path as Created/Changed
                        Log($"[FileWatcher] Unknown rename detected (temp -> {item.Key}), treating as upsert.");
                        if (File.Exists(item.FullPath))
                        {
                            var uploadResp = await _gatewayClient.BackupFileAsync(
                                backupSetId,
                                item.Key,
                                item.FullPath,
                                identity.ClientId,
                                cancellationToken: cancellationToken);

                            if (uploadResp.Success)
                            {
                                long len = FileStreamingHelper.GetFileLength(item.FullPath);
                                DateTime lastWrite = new FileInfo(item.FullPath).LastWriteTimeUtc;
                                string sha = await FileStreamingHelper.ComputeFileSha256Async(item.FullPath, cancellationToken);

                                index.Entries[item.Key] = new LocalFileIndexEntry
                                {
                                    RelativePath = item.Key,
                                    Size = len,
                                    LastWriteTimeUtc = lastWrite,
                                    Sha256 = sha
                                };
                                await _indexService.SaveIndexAsync(index, cancellationToken);
                            }
                            else
                            {
                                ReconciliationRequired = true;
                            }
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[SyncEngine] Network or processing error for '{item.Key}': {ex.Message}. Full reconciliation will be required.", "WARN");
            ReconciliationRequired = true;
        }
    }

    public void Dispose()
    {
        _watcherService.OverflowOrErrorOccurred -= OnWatcherOverflowOrError;
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _syncLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _watcherService.OverflowOrErrorOccurred -= OnWatcherOverflowOrError;
        if (_loopCts != null)
        {
            _loopCts.Cancel();
            if (_processingLoopTask != null)
            {
                try
                {
                    await _processingLoopTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch { }
            }
            _loopCts.Dispose();
            _loopCts = null;
        }
        _syncLock.Dispose();
    }
}
