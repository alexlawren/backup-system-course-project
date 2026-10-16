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
    SyncProfile? ActiveProfile { get; }
    string? CurrentLocalRootPath { get; }
    Guid? CurrentBackupSetId { get; }
    string? CurrentBackupSetName { get; }
    bool ReconciliationRequired { get; }
    DateTime? LastReconciliationUtc { get; }
    string? LastSyncResult { get; }
    int PendingEventCount { get; }
    IReadOnlyList<SyncLogEntry> RecentEvents { get; }
    IReadOnlyList<SharedSyncConflict> ActiveConflicts { get; }
    bool HasConflict { get; }

    Task StartAsync(SyncProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SwitchProfileAsync(SyncProfile profile, CancellationToken cancellationToken = default);
    Task ReconcileNowAsync(CancellationToken cancellationToken = default);
    Task<RestoreExecutionResult> RestoreVersionAsync(Guid backupSetId, Guid fileId, int versionNumber, CancellationToken cancellationToken = default);
}

public class SharedSyncConflict
{
    public Guid? FileId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string ConflictType { get; set; } = string.Empty;
    public string? LocalSha256 { get; set; }
    public string? RemoteSha256 { get; set; }
    public int? LocalBaseVersion { get; set; }
    public int RemoteVersion { get; set; }
    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;
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
    private readonly ISyncProfileService? _profileService;
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
    public SyncProfile? ActiveProfile => _activeProfile;
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

    private readonly List<SharedSyncConflict> _activeConflicts = new();
    private readonly object _conflictsLock = new();

    public IReadOnlyList<SharedSyncConflict> ActiveConflicts
    {
        get
        {
            lock (_conflictsLock)
            {
                return _activeConflicts.ToList();
            }
        }
    }

    public bool HasConflict
    {
        get
        {
            lock (_conflictsLock)
            {
                return _activeConflicts.Count > 0;
            }
        }
    }

    private void AddConflict(SharedSyncConflict conflict)
    {
        lock (_conflictsLock)
        {
            _activeConflicts.Add(conflict);
        }
        Log($"[SyncEngine] Conflict detected on '{conflict.RelativePath}' ({conflict.ConflictType}). Deferred to Change-set #3.", "WARN");
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
        IRestoreSuppressionService? suppressionService = null,
        ISyncProfileService? profileService = null)
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
        _profileService = profileService;
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
        profile.AutomaticSyncEnabled = true;
        await SwitchProfileAsync(profile, cancellationToken);
    }

    public async Task SwitchProfileAsync(SyncProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.BackupSetId == Guid.Empty)
        {
            throw new ArgumentException("BackupSetId cannot be empty.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.LocalRootPath))
        {
            throw new ArgumentException("LocalRootPath cannot be empty.", nameof(profile));
        }

        string normalizedPath = Path.GetFullPath(profile.LocalRootPath.Trim());
        profile.LocalRootPath = normalizedPath;
        if (!Directory.Exists(normalizedPath))
        {
            Directory.CreateDirectory(normalizedPath);
        }

        bool shouldRun = profile.AutomaticSyncEnabled || IsRunning;

        // 1. Explicitly stop watcher on old root before anything else
        string? oldRoot = _activeProfile?.LocalRootPath;
        if (!string.IsNullOrEmpty(oldRoot))
        {
            _watcherService.StopWatching(oldRoot);
        }
        await StopAsync(cancellationToken);

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            // 2. Persist new sync profile atomically
            profile.AutomaticSyncEnabled = shouldRun;
            if (_profileService != null)
            {
                await _profileService.SaveProfileAsync(profile, cancellationToken);
            }

            // 3. Update in-memory ActiveProfile
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

            // 4. Reset/reload LocalFileIndex for new BackupSet
            await _indexService.LoadIndexAsync(profile.BackupSetId, cancellationToken);

            // 5. Mark reconciliation required
            ReconciliationRequired = true;

            if (shouldRun)
            {
                Log($"[SyncEngine] Switching profile to '{profile.Name}' ({profile.LocalRootPath})");

                // 6. Start watcher for new root
                _watcherService.Start(profile.LocalRootPath);

                // 7. Run initial reconciliation for new root
                await ExecuteReconciliationInternalAsync(cancellationToken);

                // 8. Start background event processing loop
                _loopCts = new CancellationTokenSource();
                _processingLoopTask = Task.Run(() => RunWatcherProcessingLoopAsync(_loopCts.Token));

                IsRunning = true;
                Log("[SyncEngine] Automatic synchronization running on new profile.");
            }
            else
            {
                IsRunning = false;
                Log($"[SyncEngine] Switched profile to '{profile.Name}' ({profile.LocalRootPath}) (Automatic sync idle).");
            }
        }
        catch (Exception ex)
        {
            Log($"[SyncEngine] Failed during profile switch: {ex.Message}", "ERROR");
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
        // 1. Stop watcher to cease new incoming events unconditionally
        _watcherService.Stop();

        if (!IsRunning && _processingLoopTask == null)
        {
            return;
        }

        Log("[SyncEngine] Stopping automatic synchronization...");

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

        if (_activeProfile.AccessMode == Backup.Shared.Messages.BackupSetAccessMode.Shared)
        {
            await ExecuteSharedReconciliationInternalAsync(cancellationToken);
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
                        Sha256 = sha,
                        FileId = uploadResp.FileId,
                        LastSyncedServerVersion = uploadResp.VersionNumber ?? 1,
                        LastSyncedSha256 = sha,
                        IsDeletedOnServer = false
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

    private async Task ExecuteSharedReconciliationInternalAsync(CancellationToken cancellationToken)
    {
        if (_activeProfile == null) return;

        string root = _activeProfile.LocalRootPath;
        Guid backupSetId = _activeProfile.BackupSetId;

        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }

        Log($"[SyncEngine] Shared bidirectional reconciliation started for BackupSet {backupSetId}");
        IsReconciling = true;

        try
        {
            // PHASE 1: Fetch Server Manifest
            var identity = await _identityService.GetStateAsync(cancellationToken);
            var manifestResp = await _gatewayClient.GetBackupSetManifestAsync(new GetBackupSetManifestRequest
            {
                BackupSetId = backupSetId,
                ClientId = identity.ClientId
            }, cancellationToken);

            if (!manifestResp.Success)
            {
                LastSyncResult = $"Manifest failed: {manifestResp.ErrorMessage}";
                Log($"[SyncEngine] Manifest reconciliation failed: {manifestResp.ErrorMessage}", "ERROR");
                ReconciliationRequired = true;
                return;
            }

            var manifestEntries = manifestResp.Entries ?? new List<BackupSetManifestEntryDto>();
            Log($"[SyncEngine] Server manifest received with {manifestEntries.Count} entries.");

            // PHASE 2: Scan Local Directory
            var localIndex = await _indexService.LoadIndexAsync(backupSetId, cancellationToken);
            var scanResult = await _scanner.ScanDirectoryAsync(root, localIndex, cancellationToken);
            Log($"[SyncEngine] Scanned {scanResult.ManifestEntries.Count} local files.");

            // PHASE 3: Classify changes
            lock (_conflictsLock)
            {
                _activeConflicts.Clear();
            }

            var manifestByPath = manifestEntries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
            var manifestByFileId = manifestEntries.ToDictionary(e => e.FileId);
            var localFiles = scanResult.ScannedIndexEntries; // RelativePath -> LocalFileIndexEntry

            var remoteDownloads = new List<BackupSetManifestEntryDto>();
            var remoteRenames = new List<(string OldRelative, string NewRelative, BackupSetManifestEntryDto Entry)>();
            var remoteDeletes = new List<(string RelativePath, BackupSetManifestEntryDto Entry)>();

            var processedServerFileIds = new HashSet<Guid>();

            foreach (var mEntry in manifestEntries)
            {
                string normPath = mEntry.RelativePath.Replace('\\', '/');
                if (mEntry.IsDeleted)
                {
                    // Server deleted
                    bool existsLocally = localFiles.TryGetValue(mEntry.RelativePath, out var localFile)
                        || localFiles.TryGetValue(normPath, out localFile);

                    if (existsLocally && localFile != null)
                    {
                        // File exists locally. Did it change locally since last sync?
                        bool hasIdx = localIndex.Entries.TryGetValue(mEntry.RelativePath, out var idx)
                            || localIndex.Entries.TryGetValue(normPath, out idx);

                        if (hasIdx && idx != null && !string.IsNullOrEmpty(idx.LastSyncedSha256)
                            && string.Equals(idx.LastSyncedSha256, localFile.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            // Not modified locally. Safe to delete locally.
                            remoteDeletes.Add((mEntry.RelativePath, mEntry));
                        }
                        else
                        {
                            // Modified locally or untracked -> CONFLICT
                            AddConflict(new SharedSyncConflict
                            {
                                FileId = mEntry.FileId,
                                RelativePath = normPath,
                                ConflictType = "RemoteDeleteLocalModify",
                                LocalSha256 = localFile.Sha256,
                                RemoteVersion = mEntry.CurrentVersionNumber
                            });
                        }
                    }
                    else
                    {
                        localIndex.Entries.Remove(mEntry.RelativePath);
                        localIndex.Entries.Remove(normPath);
                    }
                    processedServerFileIds.Add(mEntry.FileId);
                    continue;
                }

                // Server active file
                // Check if we have this FileId recorded under a different local path (Remote Rename)
                var existingIndexRename = localIndex.Entries.Values.FirstOrDefault(i =>
                    i.FileId == mEntry.FileId
                    && !string.Equals(i.RelativePath, mEntry.RelativePath, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(i.RelativePath.Replace('\\', '/'), normPath, StringComparison.OrdinalIgnoreCase));

                if (existingIndexRename != null && (localFiles.TryGetValue(existingIndexRename.RelativePath, out var oldLocalFile)
                    || localFiles.TryGetValue(existingIndexRename.RelativePath.Replace('\\', '/'), out oldLocalFile)))
                {
                    // Source exists locally. Is destination occupied?
                    if (localFiles.ContainsKey(mEntry.RelativePath) || localFiles.ContainsKey(normPath))
                    {
                        AddConflict(new SharedSyncConflict
                        {
                            FileId = mEntry.FileId,
                            RelativePath = normPath,
                            ConflictType = "RemoteRenameDestinationOccupied",
                            RemoteVersion = mEntry.CurrentVersionNumber
                        });
                    }
                    else if (!string.IsNullOrEmpty(existingIndexRename.LastSyncedSha256)
                        && !string.Equals(existingIndexRename.LastSyncedSha256, oldLocalFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        // Source file was locally modified -> CONFLICT
                        AddConflict(new SharedSyncConflict
                        {
                            FileId = mEntry.FileId,
                            RelativePath = existingIndexRename.RelativePath,
                            ConflictType = "RemoteRenameSourceModified",
                            LocalSha256 = oldLocalFile.Sha256,
                            RemoteVersion = mEntry.CurrentVersionNumber
                        });
                    }
                    else
                    {
                        // Safe remote rename
                        remoteRenames.Add((existingIndexRename.RelativePath, mEntry.RelativePath, mEntry));
                    }
                    processedServerFileIds.Add(mEntry.FileId);
                    continue;
                }

                // Not a rename. Check by relative path.
                bool hasCurrentLocal = localFiles.TryGetValue(mEntry.RelativePath, out var currentLocal)
                    || localFiles.TryGetValue(normPath, out currentLocal);

                if (!hasCurrentLocal || currentLocal == null)
                {
                    // File does not exist locally.
                    // Did this client previously have this file synced at the server's current version?
                    bool hasIdx = localIndex.Entries.TryGetValue(mEntry.RelativePath, out var idx)
                        || localIndex.Entries.TryGetValue(normPath, out idx);

                    if (hasIdx && idx != null && idx.LastSyncedServerVersion > 0 && idx.LastSyncedServerVersion == mEntry.CurrentVersionNumber)
                    {
                        // The client already had this file at the current server version, and now it is missing locally.
                        // That is a LOCAL DELETE! Do NOT redownload it!
                        // It will be processed in Phase 6 as a local delete push to server.
                    }
                    else if (hasIdx && idx != null && idx.LastSyncedServerVersion > 0 && mEntry.CurrentVersionNumber > idx.LastSyncedServerVersion)
                    {
                        // Concurrently modified remotely while deleted locally -> CONFLICT
                        AddConflict(new SharedSyncConflict
                        {
                            FileId = mEntry.FileId,
                            RelativePath = normPath,
                            ConflictType = "RemoteModifyLocalDelete",
                            RemoteVersion = mEntry.CurrentVersionNumber
                        });
                    }
                    else
                    {
                        // Genuinely missing file or remote create -> Safe remote download
                        remoteDownloads.Add(mEntry);
                    }
                }
                else
                {
                    // File exists locally. Compare hashes & versions.
                    if (string.Equals(currentLocal.Sha256, mEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        // Hashes match! Update index with authoritative server info without redownload.
                        localIndex.Entries[normPath] = new LocalFileIndexEntry
                        {
                            RelativePath = normPath,
                            Size = currentLocal.Size,
                            LastWriteTimeUtc = currentLocal.LastWriteTimeUtc,
                            Sha256 = currentLocal.Sha256,
                            FileId = mEntry.FileId,
                            LastSyncedServerVersion = mEntry.CurrentVersionNumber,
                            LastSyncedSha256 = mEntry.Sha256,
                            IsDeletedOnServer = false
                        };
                    }
                    else
                    {
                        // Hashes differ!
                        bool hasIdx = localIndex.Entries.TryGetValue(mEntry.RelativePath, out var idx)
                            || localIndex.Entries.TryGetValue(normPath, out idx);

                        if (hasIdx && idx != null
                            && idx.LastSyncedServerVersion > 0
                            && mEntry.CurrentVersionNumber > idx.LastSyncedServerVersion)
                        {
                            // Server advanced to a newer version.
                            // Did local file change since last sync?
                            if (string.Equals(currentLocal.Sha256, idx.LastSyncedSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                // Local file was not modified. Safe to replace with remote version!
                                remoteDownloads.Add(mEntry);
                            }
                            else
                            {
                                // Both sides changed -> CONFLICT
                                AddConflict(new SharedSyncConflict
                                {
                                    FileId = mEntry.FileId,
                                    RelativePath = normPath,
                                    ConflictType = "RemoteModifyLocalModify",
                                    LocalSha256 = currentLocal.Sha256,
                                    RemoteSha256 = mEntry.Sha256,
                                    LocalBaseVersion = idx.LastSyncedServerVersion,
                                    RemoteVersion = mEntry.CurrentVersionNumber
                                });
                            }
                        }
                        else if (hasIdx && idx != null && idx.LastSyncedServerVersion == mEntry.CurrentVersionNumber)
                        {
                            // Server did not change. Local modified! Will be pushed in Phase 6.
                        }
                        else
                        {
                            // Collision between untracked local file and server file with different hash -> CONFLICT
                            AddConflict(new SharedSyncConflict
                            {
                                FileId = mEntry.FileId,
                                RelativePath = normPath,
                                ConflictType = "RemoteCreateCollision",
                                LocalSha256 = currentLocal.Sha256,
                                RemoteSha256 = mEntry.Sha256,
                                RemoteVersion = mEntry.CurrentVersionNumber
                            });
                        }
                    }
                }
                processedServerFileIds.Add(mEntry.FileId);
            }

            // PHASE 4: Apply safe REMOTE changes
            int remoteAppliedCount = 0;

            // 4a. Apply Renames
            foreach (var (oldRel, newRel, mEntry) in remoteRenames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string oldFull = Path.Combine(root, oldRel);
                string newFull = Path.Combine(root, newRel);

                if (File.Exists(oldFull))
                {
                    string? dir = Path.GetDirectoryName(newFull);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    _suppressionService.RegisterSuppression(oldRel, mEntry.Sha256, TimeSpan.FromSeconds(30));
                    _suppressionService.RegisterSuppression(newRel, mEntry.Sha256, TimeSpan.FromSeconds(30));

                    if (File.Exists(newFull))
                    {
                        File.Delete(newFull);
                    }
                    File.Move(oldFull, newFull);

                    localIndex.Entries.Remove(oldRel);
                    localIndex.Entries[newRel] = new LocalFileIndexEntry
                    {
                        RelativePath = newRel,
                        Size = mEntry.Size,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(newFull),
                        Sha256 = mEntry.Sha256,
                        FileId = mEntry.FileId,
                        LastSyncedServerVersion = mEntry.CurrentVersionNumber,
                        LastSyncedSha256 = mEntry.Sha256,
                        IsDeletedOnServer = false
                    };

                    remoteAppliedCount++;
                    Log($"[SyncEngine] Applied remote rename: {oldRel} -> {newRel}");
                }
            }

            // 4b. Apply Deletes
            foreach (var (relPath, mEntry) in remoteDeletes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normDel = relPath.Replace('\\', '/');
                string fullPath = Path.Combine(root, normDel);
                if (File.Exists(fullPath))
                {
                    _suppressionService.RegisterSuppression(relPath, mEntry.Sha256, TimeSpan.FromSeconds(30));
                    _suppressionService.RegisterSuppression(normDel, mEntry.Sha256, TimeSpan.FromSeconds(30));
                    File.Delete(fullPath);
                    localIndex.Entries.Remove(relPath);
                    localIndex.Entries.Remove(normDel);
                    remoteAppliedCount++;
                    Log($"[SyncEngine] Applied remote delete: {normDel}");
                }
                else
                {
                    localIndex.Entries.Remove(relPath);
                    localIndex.Entries.Remove(normDel);
                }
            }

            // 4c. Apply Downloads
            foreach (var mEntry in remoteDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Suppress watcher echo before writing target
                _suppressionService.RegisterSuppression(mEntry.RelativePath, mEntry.Sha256, TimeSpan.FromSeconds(30));

                var dlResult = await _gatewayClient.DownloadSharedFileVersionAsync(new DownloadFileVersionRequest
                {
                    BackupSetId = backupSetId,
                    FileId = mEntry.FileId,
                    VersionNumber = mEntry.CurrentVersionNumber,
                    ClientId = identity.ClientId
                }, root, cancellationToken);

                if (dlResult.Success)
                {
                    // Atomic move temp file to target
                    if (File.Exists(dlResult.TargetFilePath))
                    {
                        File.Delete(dlResult.TargetFilePath);
                    }
                    File.Move(dlResult.TempFilePath, dlResult.TargetFilePath);

                    localIndex.Entries[mEntry.RelativePath] = new LocalFileIndexEntry
                    {
                        RelativePath = mEntry.RelativePath,
                        Size = dlResult.Size,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(dlResult.TargetFilePath),
                        Sha256 = dlResult.Sha256 ?? mEntry.Sha256,
                        FileId = mEntry.FileId,
                        LastSyncedServerVersion = mEntry.CurrentVersionNumber,
                        LastSyncedSha256 = dlResult.Sha256 ?? mEntry.Sha256,
                        IsDeletedOnServer = false
                    };

                    remoteAppliedCount++;
                    Log($"[SyncEngine] Downloaded remote file {mEntry.RelativePath} (v{mEntry.CurrentVersionNumber})");
                }
                else
                {
                    _suppressionService.RemoveSuppression(mEntry.RelativePath);
                    Log($"[SyncEngine] Failed to download {mEntry.RelativePath}: {dlResult.ErrorMessage}", "WARN");
                    ReconciliationRequired = true;
                }
            }

            // PHASE 5: Rescan local directory
            var postScan = await _scanner.ScanDirectoryAsync(root, localIndex, cancellationToken);
            var currentLocalFiles = postScan.ScannedIndexEntries;

            // PHASE 6: Push safe LOCAL changes
            int localUploadedCount = 0;
            int localDeletedCount = 0;

            var conflictPaths = new HashSet<string>(ActiveConflicts.Select(c => c.RelativePath), StringComparer.OrdinalIgnoreCase);

            foreach (var (relPath, localFile) in currentLocalFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (conflictPaths.Contains(relPath))
                {
                    continue; // Skip conflicted files
                }

                bool isLocalCreate = !manifestByPath.TryGetValue(relPath, out var serverEntry) || serverEntry.IsDeleted;
                bool isLocalModify = !isLocalCreate && serverEntry != null
                    && serverEntry.CurrentVersionNumber == (localIndex.Entries.TryGetValue(relPath, out var idx) ? idx.LastSyncedServerVersion : 0)
                    && !string.Equals(serverEntry.Sha256, localFile.Sha256, StringComparison.OrdinalIgnoreCase);

                if (isLocalCreate || isLocalModify)
                {
                    string fullPath = Path.Combine(root, relPath);
                    bool stable = await _stabilityHelper.WaitForFileStabilityAsync(fullPath, cancellationToken);
                    if (!stable)
                    {
                        Log($"[SyncEngine] Local file '{relPath}' locked. Scheduling retry.", "WARN");
                        ReconciliationRequired = true;
                        continue;
                    }

                    var uploadResp = await _gatewayClient.BackupFileAsync(
                        backupSetId,
                        relPath,
                        fullPath,
                        identity.ClientId,
                        cancellationToken,
                        localFile.Sha256,
                        localFile.Size);

                    if (uploadResp.Success)
                    {
                        localUploadedCount++;
                        int v = uploadResp.VersionNumber ?? 1;
                        localIndex.Entries[relPath] = new LocalFileIndexEntry
                        {
                            RelativePath = relPath,
                            Size = localFile.Size,
                            LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath),
                            Sha256 = localFile.Sha256,
                            FileId = uploadResp.FileId,
                            LastSyncedServerVersion = v,
                            LastSyncedSha256 = localFile.Sha256,
                            IsDeletedOnServer = false
                        };
                        Log($"[SyncEngine] Uploaded local change for {relPath} (v{v})");
                    }
                    else
                    {
                        Log($"[SyncEngine] Local upload failed for '{relPath}': {uploadResp.ErrorMessage}", "WARN");
                        ReconciliationRequired = true;
                    }
                }
            }

            // Local Deletions: in localIndex but not on disk, where server has not advanced
            var locallyDeletedKeys = localIndex.Entries.Keys
                .Where(k => !currentLocalFiles.ContainsKey(k)
                         && !currentLocalFiles.ContainsKey(k.Replace('\\', '/'))
                         && !conflictPaths.Contains(k))
                .ToList();

            foreach (var delPath in locallyDeletedKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var idx = localIndex.Entries[delPath];
                string normDelPath = delPath.Replace('\\', '/');

                if ((manifestByPath.TryGetValue(delPath, out var mEntry) || manifestByPath.TryGetValue(normDelPath, out mEntry)) && !mEntry.IsDeleted)
                {
                    if (mEntry.CurrentVersionNumber == idx.LastSyncedServerVersion)
                    {
                        // Server did not change -> Safe LOCAL DELETE!
                        var delResp = await _gatewayClient.DeleteFileAsync(new FileDeletedRequest
                        {
                            ClientId = identity.ClientId ?? Guid.Empty,
                            BackupSetId = backupSetId,
                            RelativePath = normDelPath
                        }, cancellationToken);

                        if (delResp.Success)
                        {
                            localDeletedCount++;
                            localIndex.Entries.Remove(delPath);
                            localIndex.Entries.Remove(normDelPath);
                            Log($"[SyncEngine] Pushed local delete to server: {normDelPath}");
                        }
                    }
                    else
                    {
                        // Server modified while locally deleted -> CONFLICT
                        AddConflict(new SharedSyncConflict
                        {
                            FileId = mEntry.FileId,
                            RelativePath = normDelPath,
                            ConflictType = "RemoteModifyLocalDelete",
                            RemoteVersion = mEntry.CurrentVersionNumber
                        });
                    }
                }
                else
                {
                    localIndex.Entries.Remove(delPath);
                    localIndex.Entries.Remove(normDelPath);
                }
            }

            // PHASE 7: Persist final LocalFileIndex
            await _indexService.SaveIndexAsync(localIndex, cancellationToken);

            LastReconciliationUtc = DateTime.UtcNow;
            if (ActiveConflicts.Count > 0)
            {
                ReconciliationRequired = true;
                LastSyncResult = $"Conflicts detected: {ActiveConflicts.Count} conflict(s) pending resolution.";
                Log($"[SyncEngine] Reconciliation completed with {ActiveConflicts.Count} conflict(s).", "WARN");
            }
            else
            {
                ReconciliationRequired = false;
                LastSyncResult = $"Success. Applied: {remoteAppliedCount} remote, Uploaded: {localUploadedCount} local, Deleted: {localDeletedCount} local.";
                Log($"[SyncEngine] Reconciliation completed cleanly. {LastSyncResult}");
            }
        }
        catch (Exception ex)
        {
            ReconciliationRequired = true;
            LastSyncResult = $"Shared reconciliation error: {ex.Message}";
            Log($"[SyncEngine] Shared reconciliation error: {ex.Message}", "ERROR");
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
                                Sha256 = sha,
                                FileId = uploadResp.FileId,
                                LastSyncedServerVersion = uploadResp.VersionNumber ?? 1,
                                LastSyncedSha256 = sha,
                                IsDeletedOnServer = false
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
                    if (_suppressionService.IsSuppressed(item.Key))
                    {
                        _suppressionService.RemoveSuppression(item.Key);
                        Log($"[SyncEngine] Watcher echo suppressed for remote delete: {item.Key}");
                        return;
                    }

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

                    if (_suppressionService.IsSuppressed(item.Key) || (!string.IsNullOrEmpty(oldRelative) && _suppressionService.IsSuppressed(oldRelative)))
                    {
                        _suppressionService.RemoveSuppression(item.Key);
                        if (!string.IsNullOrEmpty(oldRelative)) _suppressionService.RemoveSuppression(oldRelative);
                        Log($"[SyncEngine] Watcher echo suppressed for remote rename: {oldRelative} -> {item.Key}");
                        return;
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
        UploadCompletedResponse reBackupResp = null!;
        for (int uploadAttempt = 0; uploadAttempt < 5; uploadAttempt++)
        {
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

                if (reBackupResp.Success || reBackupResp.ErrorCode != BackupErrorCodes.UploadInProgress)
                {
                    break;
                }

                await Task.Delay(100 * (uploadAttempt + 1), cancellationToken);
            }
            catch (Exception ex)
            {
                if (uploadAttempt == 4)
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
                await Task.Delay(100 * (uploadAttempt + 1), cancellationToken);
            }
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
