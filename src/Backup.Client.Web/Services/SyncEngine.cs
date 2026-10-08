using System.Collections.Concurrent;
using Backup.Shared.Errors;
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
    bool IsReconciling { get; }
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
    Task<RestoreExecutionResult> RestoreVersionAsync(Guid backupSetId, Guid fileId, int versionNumber, CancellationToken cancellationToken = default);
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
    private readonly IRestoreSuppressionService _suppressionService;
    private readonly AsyncPathLock _pathLocks = new();
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
    public bool IsReconciling { get; private set; }
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
        int debounceDelayMs = 750,
        IRestoreSuppressionService? suppressionService = null)
    {
        _gatewayClient = gatewayClient;
        _identityService = identityService;
        _indexService = indexService;
        _scanner = scanner;
        _watcherService = watcherService;
        _stabilityHelper = stabilityHelper;
        _logger = logger;
        _debounceDelayMs = debounceDelayMs;
        _suppressionService = suppressionService ?? new RestoreSuppressionService();
        _watcherService.OverflowOrErrorOccurred += OnWatcherOverflowOrError;
        _gatewayClient.Reconnected += OnGatewayReconnected;
    }

    private void OnGatewayReconnected()
    {
        Log("[SyncEngine] Gateway connection re-established. Full reconciliation required.", "WARN");
        ReconciliationRequired = true;
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

            var activeIdentity = await _identityService.GetStateAsync(cancellationToken);
            _gatewayClient.SetActiveClientId(activeIdentity.ClientId);

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

        IsReconciling = true;
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
        finally
        {
            IsReconciling = false;
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
                    using var pathLock = await _pathLocks.LockAsync(item.Key, cancellationToken);

                    if (!File.Exists(item.FullPath))
                    {
                        // File disappeared before debounce finished
                        return;
                    }

                    // Check suppression echo (Section 16)
                    if (_suppressionService.IsSuppressed(item.Key))
                    {
                        string currentSha = await FileStreamingHelper.ComputeFileSha256Async(item.FullPath, cancellationToken);
                        if (_suppressionService.ShouldSuppress(item.Key, currentSha))
                        {
                            Log($"[Restore] Watcher echo suppressed: {item.Key}");
                            return;
                        }
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
                    using var pathLock = await _pathLocks.LockAsync(item.Key, cancellationToken);
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
                    using var pathLock = await _pathLocks.LockAsync(item.Key, cancellationToken);
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

    public async Task<RestoreExecutionResult> RestoreVersionAsync(
        Guid backupSetId,
        Guid fileId,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        if (_activeProfile == null)
        {
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                ErrorCode = "NO_ACTIVE_PROFILE",
                ErrorMessage = "Cannot restore file: no active backup set profile."
            };
        }

        if (_activeProfile.BackupSetId != backupSetId)
        {
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                ErrorCode = "BACKUP_SET_MISMATCH",
                ErrorMessage = "BackupSetId does not match current active profile."
            };
        }

        var identity = await _identityService.GetStateAsync(cancellationToken);
        if (identity.ClientId == null)
        {
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                ErrorCode = "CLIENT_NOT_REGISTERED",
                ErrorMessage = "Client station is not registered."
            };
        }

        // 1. Resolve relative path for this file from server
        var filesResp = await _gatewayClient.GetFilesAsync(new GetFilesRequest
        {
            BackupSetId = backupSetId,
            IncludeDeleted = true
        }, cancellationToken);

        if (!filesResp.Success || filesResp.Files == null)
        {
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                ErrorCode = "METADATA_UNAVAILABLE",
                ErrorMessage = filesResp.ErrorMessage ?? "Failed to query files from server."
            };
        }

        var fileMeta = filesResp.Files.FirstOrDefault(f => f.FileId == fileId);
        if (fileMeta == null)
        {
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                ErrorCode = "FILE_NOT_FOUND",
                ErrorMessage = $"File {fileId} was not found in active backup set."
            };
        }

        string relativePath = fileMeta.RelativePath;

        // 2. Acquire per-path lock
        using var pathLock = await _pathLocks.LockAsync(relativePath, cancellationToken);

        string root = Path.GetFullPath(_activeProfile.LocalRootPath);
        string targetFullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string fullRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!targetFullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                RelativePath = relativePath,
                TargetLocalPath = targetFullPath,
                SourceVersionNumber = versionNumber,
                ErrorCode = BackupErrorCodes.InvalidRelativePath,
                ErrorMessage = "Target file path escapes backup set root directory."
            };
        }

        // 3. Current file pre-backup before overwrite (Section 3 & 28)
        if (File.Exists(targetFullPath))
        {
            Log($"[Restore] Pre-backup: checking current local file '{relativePath}' before restore.");
            var preBackupResp = await _gatewayClient.BackupFileAsync(
                backupSetId,
                relativePath,
                targetFullPath,
                identity.ClientId,
                cancellationToken);

            if (!preBackupResp.Success)
            {
                Log($"[Restore] Pre-backup of current local content failed: {preBackupResp.ErrorMessage}. In-place restore aborted.", "ERROR");
                return new RestoreExecutionResult
                {
                    Success = false,
                    Status = "FAILED",
                    RelativePath = relativePath,
                    TargetLocalPath = targetFullPath,
                    SourceVersionNumber = versionNumber,
                    ErrorCode = "PRE_BACKUP_FAILED",
                    ErrorMessage = $"Cannot safely restore: pre-backup of current local content failed: {preBackupResp.ErrorMessage}"
                };
            }

            Log($"[Restore] Current local content backed up");
        }

        // 4. Download and verify restore file
        Log($"[Restore] Requested file '{relativePath}' version {versionNumber}");
        var restoreReq = new RestoreRequest
        {
            ClientId = identity.ClientId.Value,
            BackupSetId = backupSetId,
            FileId = fileId,
            VersionNumber = versionNumber
        };

        var downloadResult = await _gatewayClient.DownloadAndVerifyRestoreFileAsync(restoreReq, root, cancellationToken);
        if (!downloadResult.Success)
        {
            Log($"[Restore] Download failed: {downloadResult.ErrorMessage}", "ERROR");
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                RelativePath = relativePath,
                TargetLocalPath = targetFullPath,
                SourceVersionNumber = versionNumber,
                ErrorCode = downloadResult.ErrorCode,
                ErrorMessage = downloadResult.ErrorMessage
            };
        }

        Log($"[Restore] SHA-256 verified");

        // 5. Watcher suppression and atomic local replace (Section 16, 17, 26)
        _suppressionService.RegisterSuppression(relativePath, downloadResult.Sha256, TimeSpan.FromSeconds(30));

        bool replaced = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(targetFullPath))
                {
                    File.Move(downloadResult.TempFilePath, targetFullPath, overwrite: true);
                }
                else
                {
                    string? pDir = Path.GetDirectoryName(targetFullPath);
                    if (!string.IsNullOrEmpty(pDir) && !Directory.Exists(pDir))
                    {
                        Directory.CreateDirectory(pDir);
                    }
                    File.Move(downloadResult.TempFilePath, targetFullPath);
                }
                replaced = true;
                break;
            }
            catch (IOException ioEx)
            {
                if (attempt < 4)
                {
                    await Task.Delay(100 * (attempt + 1), cancellationToken);
                }
                else
                {
                    _logger.LogWarning(ioEx, "[Restore] Target locked: could not replace file '{Path}'", targetFullPath);
                }
            }
        }

        if (!replaced)
        {
            if (File.Exists(downloadResult.TempFilePath))
            {
                try { File.Delete(downloadResult.TempFilePath); } catch { }
            }
            _suppressionService.RemoveSuppression(relativePath);
            return new RestoreExecutionResult
            {
                Success = false,
                Status = "FAILED",
                RelativePath = relativePath,
                TargetLocalPath = targetFullPath,
                SourceVersionNumber = versionNumber,
                ErrorCode = "RESTORE_TARGET_LOCKED",
                ErrorMessage = "Target local file is locked by another process."
            };
        }

        Log($"[Restore] Local file replaced");

        // 6. Update local index (Section 18)
        var index = await _indexService.LoadIndexAsync(backupSetId, cancellationToken);
        long restoredLen = new FileInfo(targetFullPath).Length;
        DateTime lastWrite = new FileInfo(targetFullPath).LastWriteTimeUtc;
        index.Entries[relativePath] = new LocalFileIndexEntry
        {
            RelativePath = relativePath,
            Size = restoredLen,
            LastWriteTimeUtc = lastWrite,
            Sha256 = downloadResult.Sha256
        };
        await _indexService.SaveIndexAsync(index, cancellationToken);

        // 7. Explicit re-backup with RESTORE change origin (Section 19, 20, 21, 22)
        UploadCompletedResponse reBackupResp;
        try
        {
            reBackupResp = await _gatewayClient.BackupFileAsync(
                backupSetId,
                relativePath,
                targetFullPath,
                identity.ClientId,
                cancellationToken,
                precomputedSha256: downloadResult.Sha256,
                precomputedSize: downloadResult.Size,
                changeOrigin: BackupChangeOrigin.Restore);
        }
        catch (Exception ex)
        {
            Log($"[Restore] Server sync failed after local replace: {ex.Message}", "WARN");
            ReconciliationRequired = true;
            _suppressionService.RemoveSuppression(relativePath);
            return new RestoreExecutionResult
            {
                Success = true,
                Status = "LOCAL_RESTORED_SYNC_PENDING",
                RelativePath = relativePath,
                TargetLocalPath = targetFullPath,
                SourceVersionNumber = versionNumber,
                Sha256Verified = downloadResult.Sha256,
                Size = downloadResult.Size,
                ErrorMessage = $"File restored locally, server synchronization pending: {ex}"
            };
        }

        if (!reBackupResp.Success)
        {
            Log($"[Restore] Server sync returned error after local replace: {reBackupResp.ErrorMessage}", "WARN");
            ReconciliationRequired = true;
            _suppressionService.RemoveSuppression(relativePath);
            return new RestoreExecutionResult
            {
                Success = true,
                Status = "LOCAL_RESTORED_SYNC_PENDING",
                RelativePath = relativePath,
                TargetLocalPath = targetFullPath,
                SourceVersionNumber = versionNumber,
                Sha256Verified = downloadResult.Sha256,
                Size = downloadResult.Size,
                ErrorMessage = $"File restored locally, server synchronization pending: {reBackupResp.ErrorCode}: {reBackupResp.ErrorMessage}"
            };
        }

        int newVersion = reBackupResp.VersionNumber ?? versionNumber;
        Log($"[Restore] Restored content committed as version {newVersion}");

        // Notify gateway RestoreCompleted
        try
        {
            await _gatewayClient.SendRestoreCompletedAsync(new RestoreCompletedNotification
            {
                ClientId = identity.ClientId.Value,
                BackupSetId = backupSetId,
                FileId = fileId,
                RestoredFromVersion = versionNumber,
                NewVersionNumber = newVersion,
                Success = true,
                Status = "SUCCESS"
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Restore] Non-critical: Failed to send RestoreCompleted ack to gateway");
        }

        return new RestoreExecutionResult
        {
            Success = true,
            Status = "SUCCESS",
            RelativePath = relativePath,
            TargetLocalPath = targetFullPath,
            SourceVersionNumber = versionNumber,
            NewCurrentVersionNumber = newVersion,
            Sha256Verified = downloadResult.Sha256,
            Size = downloadResult.Size
        };
    }

    public void Dispose()
    {
        _watcherService.OverflowOrErrorOccurred -= OnWatcherOverflowOrError;
        _gatewayClient.Reconnected -= OnGatewayReconnected;
        _watcherService.Stop();
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _syncLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _watcherService.OverflowOrErrorOccurred -= OnWatcherOverflowOrError;
        _gatewayClient.Reconnected -= OnGatewayReconnected;
        _watcherService.Stop();
        if (_loopCts != null)
        {
            _loopCts.Cancel();
            if (_processingLoopTask != null)
            {
                try
                {
                    await _processingLoopTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception)
                {
                    // Ignore cancellation or shutdown timeout
                }
            }
            _loopCts.Dispose();
            _loopCts = null;
        }
        _syncLock.Dispose();
    }
}
