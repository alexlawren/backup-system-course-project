using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
using Backup.Shared.DTOs;
using Backup.Shared.Errors;
using Backup.Shared.Messages;
using Backup.Shared.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Backup.MetadataService.Services;

public class MetadataApplicationService : IMetadataApplicationService
{
    private readonly IDbContextFactory<MetadataDbContext> _dbContextFactory;
    private readonly ILogger<MetadataApplicationService> _logger;

    public MetadataApplicationService(
        IDbContextFactory<MetadataDbContext> dbContextFactory,
        ILogger<MetadataApplicationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<RegisterClientResponse> RegisterClientAsync(RegisterClientRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new RegisterClientResponse
            {
                Success = false,
                ErrorMessage = "Client name cannot be empty."
            };
        }

        if (request.ClientId.HasValue && request.ClientId.Value != Guid.Empty)
        {
            var existingClient = await dbContext.Clients.FindAsync([request.ClientId.Value], cancellationToken);
            if (existingClient != null)
            {
                existingClient.LastSeenAtUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(request.Name))
                {
                    existingClient.Name = request.Name;
                }
                if (!string.IsNullOrWhiteSpace(request.MachineName))
                {
                    existingClient.MachineName = request.MachineName;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("[MetadataService] Updated LastSeen for existing client {ClientId} ('{Name}')", existingClient.Id, existingClient.Name);

                return new RegisterClientResponse
                {
                    ClientId = existingClient.Id,
                    Created = false,
                    LastSeenAtUtc = existingClient.LastSeenAtUtc,
                    Success = true
                };
            }
        }

        var newClient = new ClientEntity
        {
            Id = (request.ClientId.HasValue && request.ClientId.Value != Guid.Empty) ? request.ClientId.Value : Guid.NewGuid(),
            Name = request.Name,
            MachineName = request.MachineName,
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow
        };

        dbContext.Clients.Add(newClient);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[MetadataService] Registered new client {ClientId} ('{Name}')", newClient.Id, newClient.Name);

        return new RegisterClientResponse
        {
            ClientId = newClient.Id,
            Created = true,
            LastSeenAtUtc = newClient.LastSeenAtUtc,
            Success = true
        };
    }

    public async Task<CreateBackupSetResponse> CreateBackupSetAsync(CreateBackupSetRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (request.ClientId == Guid.Empty)
        {
            return new CreateBackupSetResponse { Success = false, ErrorMessage = "ClientId must be specified." };
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new CreateBackupSetResponse { Success = false, ErrorMessage = "BackupSet name cannot be empty." };
        }

        var client = await dbContext.Clients.FindAsync([request.ClientId], cancellationToken);
        if (client == null)
        {
            return new CreateBackupSetResponse { Success = false, ErrorMessage = $"Client with ID {request.ClientId} was not found." };
        }

        string accessMode = request.AccessMode == BackupSetAccessMode.Shared ? "SHARED" : "PRIVATE";
        string? shareCode = null;

        if (request.AccessMode == BackupSetAccessMode.Shared)
        {
            // Try generating a unique share code with up to 10 attempts
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string candidate = ShareCodeHelper.Generate();
                bool exists = await dbContext.BackupSets.AnyAsync(bs => bs.ShareCode == candidate, cancellationToken);
                if (!exists)
                {
                    shareCode = candidate;
                    break;
                }
            }

            if (shareCode == null)
            {
                return new CreateBackupSetResponse
                {
                    Success = false,
                    ErrorMessage = "Failed to generate a unique share code after multiple attempts. Please try again."
                };
            }
        }

        var backupSet = new BackupSetEntity
        {
            Id = Guid.NewGuid(),
            ClientId = request.ClientId,
            Name = request.Name.Trim(),
            LocalRootPath = string.IsNullOrWhiteSpace(request.LocalRootPath) ? "/" : request.LocalRootPath.Trim(),
            AccessMode = accessMode,
            ShareCode = shareCode,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true
        };

        var ownerMember = new BackupSetMemberEntity
        {
            BackupSetId = backupSet.Id,
            ClientId = request.ClientId,
            Role = "OWNER",
            JoinedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow
        };

        dbContext.BackupSets.Add(backupSet);
        dbContext.BackupSetMembers.Add(ownerMember);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[MetadataService] Created BackupSet {BackupSetId} ('{Name}', Mode: {Mode}, Code: {Code}) for Client {ClientId}",
            backupSet.Id, backupSet.Name, backupSet.AccessMode, backupSet.ShareCode, request.ClientId);

        return new CreateBackupSetResponse
        {
            BackupSetId = backupSet.Id,
            Name = backupSet.Name,
            LocalRootPath = backupSet.LocalRootPath,
            AccessMode = request.AccessMode,
            ShareCode = backupSet.ShareCode,
            Success = true
        };
    }

    private async Task<(bool Allowed, string? ErrorCode, string? ErrorMessage, BackupSetEntity? BackupSet)>
        EnsureBackupSetAccessAsync(MetadataDbContext dbContext, Guid backupSetId, Guid? clientId, CancellationToken cancellationToken)
    {
        var backupSet = await dbContext.BackupSets
            .Include(bs => bs.Members)
            .FirstOrDefaultAsync(bs => bs.Id == backupSetId, cancellationToken);

        if (backupSet == null)
        {
            return (false, "BACKUP_SET_NOT_FOUND", $"BackupSet {backupSetId} was not found.", null);
        }

        if (!clientId.HasValue || clientId.Value == Guid.Empty)
        {
            // For backward compatibility with internal/unauthenticated calls in older tests
            return (true, null, null, backupSet);
        }

        Guid cid = clientId.Value;

        // If caller is the recorded owner, access is always granted
        if (backupSet.ClientId == cid)
        {
            return (true, null, null, backupSet);
        }

        // Check membership table
        var member = backupSet.Members.FirstOrDefault(m => m.ClientId == cid)
            ?? await dbContext.BackupSetMembers.FirstOrDefaultAsync(m => m.BackupSetId == backupSetId && m.ClientId == cid, cancellationToken);

        if (member != null)
        {
            return (true, null, null, backupSet);
        }

        return (false, "ACCESS_DENIED", $"Client {cid} does not have access to BackupSet {backupSetId}.", backupSet);
    }

    public async Task<PrepareVersionResponse> PrepareVersionAsync(PrepareVersionRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.RelativePath, out var normalizedPath, out var pathError))
        {
            return new PrepareVersionResponse { Success = false, ErrorMessage = pathError };
        }

        if (request.Size < 0)
        {
            return new PrepareVersionResponse { Success = false, ErrorMessage = "File size cannot be negative." };
        }

        if (string.IsNullOrWhiteSpace(request.Sha256))
        {
            return new PrepareVersionResponse { Success = false, ErrorMessage = "Sha256 hash must be provided." };
        }

        var access = await EnsureBackupSetAccessAsync(dbContext, request.BackupSetId, request.ClientId ?? request.ActorClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new PrepareVersionResponse { Success = false, ErrorCode = access.ErrorCode, ErrorMessage = access.ErrorMessage };
        }
        var backupSet = access.BackupSet!;

        // 1. Find existing file record
        var file = await dbContext.Files.FirstOrDefaultAsync(
            f => f.BackupSetId == request.BackupSetId && f.RelativePath == normalizedPath,
            cancellationToken);

        // 2. If missing, create File entity with CurrentVersionNumber = 0
        if (file == null)
        {
            file = new FileEntity
            {
                Id = Guid.NewGuid(),
                BackupSetId = request.BackupSetId,
                RelativePath = normalizedPath,
                CurrentVersionNumber = 0,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            dbContext.Files.Add(file);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // 3. If current committed version exists and Sha256 matches, no upload required
        if (!file.IsDeleted && file.CurrentVersionNumber > 0)
        {
            var committedVersion = await dbContext.FileVersions.FirstOrDefaultAsync(
                fv => fv.FileId == file.Id && fv.VersionNumber == file.CurrentVersionNumber,
                cancellationToken);

            if (committedVersion != null && string.Equals(committedVersion.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[MetadataService] PrepareVersion: File '{Path}' is already up-to-date (v{Version}, Sha256 match)", normalizedPath, file.CurrentVersionNumber);
                return new PrepareVersionResponse
                {
                    UploadRequired = false,
                    OperationId = null,
                    FileId = file.Id,
                    ClientId = backupSet.ClientId,
                    ExpectedVersionNumber = file.CurrentVersionNumber,
                    Success = true
                };
            }
        }

        // 4. Content is new or changed -> determine next version
        int expectedVersionNumber = file.CurrentVersionNumber + 1;

        // Cleanup any stale pending operations for this file
        var existingPending = await dbContext.PendingFileOperations
            .Where(p => p.FileId == file.Id)
            .ToListAsync(cancellationToken);
        if (existingPending.Count > 0)
        {
            dbContext.PendingFileOperations.RemoveRange(existingPending);
        }

        var pendingOp = new PendingFileOperationEntity
        {
            OperationId = Guid.NewGuid(),
            FileId = file.Id,
            ExpectedVersionNumber = expectedVersionNumber,
            ExpectedSize = request.Size,
            ExpectedSha256 = request.Sha256.Trim(),
            OperationKind = request.ChangeOrigin switch
            {
                BackupChangeOrigin.Restore => "RESTORE",
                BackupChangeOrigin.ConflictCopy => "CONFLICT_COPY",
                _ => "NORMAL"
            },
            ActorClientId = request.ActorClientId ?? request.ClientId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        };

        dbContext.PendingFileOperations.Add(pendingOp);
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[MetadataService] PrepareVersion: Created pending operation {OperationId} for '{Path}' (Expected v{Version})", pendingOp.OperationId, normalizedPath, expectedVersionNumber);

        return new PrepareVersionResponse
        {
            UploadRequired = true,
            OperationId = pendingOp.OperationId,
            FileId = file.Id,
            ClientId = backupSet.ClientId,
            ExpectedVersionNumber = expectedVersionNumber,
            Success = true
        };
    }

    public async Task<CommitVersionResponse> CommitVersionAsync(CommitVersionRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // 1. Check if this OperationId was already committed (Idempotency check)
        var alreadyCommitted = await dbContext.FileVersions
            .FirstOrDefaultAsync(fv => fv.OperationId == request.OperationId, cancellationToken);

        if (alreadyCommitted != null)
        {
            bool matches = alreadyCommitted.Size == request.ActualSize &&
                           string.Equals(alreadyCommitted.Sha256, request.ActualSha256, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(alreadyCommitted.StorageKey, request.StorageKey, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                _logger.LogInformation("[MetadataService] CommitVersion: Operation {OperationId} was already committed. Returning idempotent success.", request.OperationId);
                return new CommitVersionResponse
                {
                    FileId = alreadyCommitted.FileId,
                    VersionNumber = alreadyCommitted.VersionNumber,
                    StorageKey = alreadyCommitted.StorageKey,
                    Success = true,
                    AlreadyCommitted = true
                };
            }

            _logger.LogWarning("[MetadataService] CommitVersion: Operation {OperationId} conflict with already committed version.", request.OperationId);
            return new CommitVersionResponse
            {
                FileId = alreadyCommitted.FileId,
                VersionNumber = alreadyCommitted.VersionNumber,
                Success = false,
                AlreadyCommitted = true,
                ErrorCode = "COMMIT_CONFLICT",
                ErrorMessage = $"Operation {request.OperationId} was already committed with different parameters."
            };
        }

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var pending = await dbContext.PendingFileOperations
                .Include(p => p.File)
                .ThenInclude(f => f.BackupSet)
                .FirstOrDefaultAsync(p => p.OperationId == request.OperationId, cancellationToken);

            if (pending == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return new CommitVersionResponse
                {
                    Success = false,
                    ErrorCode = "OPERATION_NOT_FOUND",
                    ErrorMessage = $"Pending operation {request.OperationId} was not found."
                };
            }

            if (request.ActualSize != pending.ExpectedSize)
            {
                await tx.RollbackAsync(cancellationToken);
                return new CommitVersionResponse
                {
                    Success = false,
                    ErrorCode = "SIZE_MISMATCH",
                    ErrorMessage = $"Size mismatch for operation {request.OperationId}. Expected: {pending.ExpectedSize}, Actual: {request.ActualSize}."
                };
            }

            if (!string.Equals(request.ActualSha256, pending.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return new CommitVersionResponse
                {
                    Success = false,
                    ErrorCode = "HASH_MISMATCH",
                    ErrorMessage = $"Sha256 mismatch for operation {request.OperationId}. Expected: {pending.ExpectedSha256}, Actual: {request.ActualSha256}."
                };
            }

            // Guard against duplicate version
            bool versionExists = await dbContext.FileVersions.AnyAsync(
                fv => fv.FileId == pending.FileId && fv.VersionNumber == pending.ExpectedVersionNumber,
                cancellationToken);

            if (versionExists)
            {
                await tx.RollbackAsync(cancellationToken);
                return new CommitVersionResponse
                {
                    Success = false,
                    ErrorCode = "VERSION_EXISTS",
                    ErrorMessage = $"Version {pending.ExpectedVersionNumber} already exists for file {pending.FileId}."
                };
            }

            // Create FileVersion
            var fileVersion = new FileVersionEntity
            {
                Id = Guid.NewGuid(),
                OperationId = pending.OperationId,
                FileId = pending.FileId,
                VersionNumber = pending.ExpectedVersionNumber,
                Size = request.ActualSize,
                Sha256 = request.ActualSha256.Trim(),
                StorageKey = request.StorageKey,
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.FileVersions.Add(fileVersion);

            // Update FileEntity
            var file = pending.File;
            file.CurrentVersionNumber = pending.ExpectedVersionNumber;
            file.IsDeleted = false;
            file.UpdatedAtUtc = DateTime.UtcNow;

            // Create ChangeLog entry
            string action = string.Equals(pending.OperationKind, "RESTORE", StringComparison.OrdinalIgnoreCase)
                ? "RESTORE"
                : (string.Equals(pending.OperationKind, "CONFLICT_COPY", StringComparison.OrdinalIgnoreCase)
                    ? "CONFLICT_COPY"
                    : (pending.ExpectedVersionNumber == 1 ? "CREATE" : "MODIFY"));

            string details;
            if (string.Equals(pending.OperationKind, "RESTORE", StringComparison.OrdinalIgnoreCase))
            {
                details = $"Restored version {pending.ExpectedVersionNumber} with Sha256 {request.ActualSha256}";
            }
            else if (string.Equals(pending.OperationKind, "CONFLICT_COPY", StringComparison.OrdinalIgnoreCase))
            {
                string orig = request.ConflictOriginalPath ?? "original file";
                string ver = request.ConflictOriginalVersion.HasValue ? $" v{request.ConflictOriginalVersion.Value}" : "";
                details = $"Conflict with {orig}{ver}; local content preserved as '{file.RelativePath}'";
            }
            else
            {
                details = $"Version {pending.ExpectedVersionNumber} committed with Sha256 {request.ActualSha256}";
            }

            var changeLog = new ChangeLogEntity
            {
                Id = Guid.NewGuid(),
                ClientId = file.BackupSet.ClientId,
                BackupSetId = file.BackupSetId,
                FileId = file.Id,
                Action = action,
                Status = "COMMITTED",
                Details = details,
                ActorClientId = request.ActorClientId ?? pending.ActorClientId ?? file.BackupSet.ClientId,
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.ChangeLogs.Add(changeLog);

            // Remove PendingFileOperation
            dbContext.PendingFileOperations.Remove(pending);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("[MetadataService] CommitVersion: Committed v{Version} for File {FileId} ('{Path}')", fileVersion.VersionNumber, file.Id, file.RelativePath);

            return new CommitVersionResponse
            {
                FileId = file.Id,
                VersionNumber = fileVersion.VersionNumber,
                StorageKey = fileVersion.StorageKey,
                Success = true,
                AlreadyCommitted = false
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "[MetadataService] CommitVersion failed for OperationId {OperationId}", request.OperationId);
            return new CommitVersionResponse
            {
                Success = false,
                ErrorMessage = $"Commit failed: {ex.Message}"
            };
        }
    }

    public async Task<DeleteFileMetadataResponse> DeleteFileMetadataAsync(DeleteFileMetadataRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.RelativePath, out var normalizedPath, out var pathError))
        {
            return new DeleteFileMetadataResponse { Success = false, ErrorMessage = pathError };
        }

        var access = await EnsureBackupSetAccessAsync(dbContext, request.BackupSetId, request.ClientId ?? request.ActorClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new DeleteFileMetadataResponse { Success = false, ErrorCode = access.ErrorCode, ErrorMessage = access.ErrorMessage };
        }

        var file = await dbContext.Files
            .Include(f => f.BackupSet)
            .FirstOrDefaultAsync(f => f.BackupSetId == request.BackupSetId && f.RelativePath == normalizedPath, cancellationToken);

        if (file == null)
        {
            return new DeleteFileMetadataResponse { Success = false, ErrorMessage = $"File '{normalizedPath}' was not found in BackupSet {request.BackupSetId}." };
        }

        if (file.IsDeleted)
        {
            _logger.LogInformation("[MetadataService] DeleteFileMetadata: File '{Path}' is already marked as deleted (Idempotent)", normalizedPath);
            return new DeleteFileMetadataResponse { FileId = file.Id, Success = true };
        }

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            file.IsDeleted = true;
            file.UpdatedAtUtc = DateTime.UtcNow;

            var changeLog = new ChangeLogEntity
            {
                Id = Guid.NewGuid(),
                ClientId = file.BackupSet.ClientId,
                BackupSetId = file.BackupSetId,
                FileId = file.Id,
                Action = "DELETE",
                Status = "COMMITTED",
                Details = $"File '{normalizedPath}' marked as deleted",
                ActorClientId = request.ActorClientId ?? request.ClientId ?? file.BackupSet.ClientId,
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.ChangeLogs.Add(changeLog);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("[MetadataService] DeleteFileMetadata: File '{Path}' marked as deleted", normalizedPath);

            return new DeleteFileMetadataResponse { FileId = file.Id, Success = true };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "[MetadataService] DeleteFileMetadata failed for '{Path}'", normalizedPath);
            return new DeleteFileMetadataResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<RenameFileMetadataResponse> RenameFileMetadataAsync(RenameFileMetadataRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.OldRelativePath, out var oldNormalized, out var oldError))
        {
            return new RenameFileMetadataResponse { Success = false, ErrorMessage = $"Old path error: {oldError}" };
        }

        if (!ProtocolPathValidator.TryNormalizeAndValidate(request.NewRelativePath, out var newNormalized, out var newError))
        {
            return new RenameFileMetadataResponse { Success = false, ErrorMessage = $"New path error: {newError}" };
        }

        if (oldNormalized == newNormalized)
        {
            return new RenameFileMetadataResponse { Success = true };
        }

        var access = await EnsureBackupSetAccessAsync(dbContext, request.BackupSetId, request.ClientId ?? request.ActorClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new RenameFileMetadataResponse { Success = false, ErrorCode = access.ErrorCode, ErrorMessage = access.ErrorMessage };
        }

        // Check if destination path already exists and is active
        var destinationConflict = await dbContext.Files.FirstOrDefaultAsync(
            f => f.BackupSetId == request.BackupSetId && f.RelativePath == newNormalized,
            cancellationToken);

        if (destinationConflict != null && !destinationConflict.IsDeleted)
        {
            return new RenameFileMetadataResponse
            {
                Success = false,
                ErrorMessage = $"Destination path '{newNormalized}' already exists."
            };
        }

        var sourceFile = await dbContext.Files
            .Include(f => f.BackupSet)
            .FirstOrDefaultAsync(f => f.BackupSetId == request.BackupSetId && f.RelativePath == oldNormalized, cancellationToken);

        if (sourceFile == null)
        {
            return new RenameFileMetadataResponse
            {
                Success = false,
                ErrorMessage = $"Source file '{oldNormalized}' was not found."
            };
        }

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // If destination exists but was previously deleted, remove or update it to avoid unique constraint clash
            if (destinationConflict != null && destinationConflict.IsDeleted)
            {
                dbContext.Files.Remove(destinationConflict);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            sourceFile.RelativePath = newNormalized;
            sourceFile.UpdatedAtUtc = DateTime.UtcNow;

            var changeLog = new ChangeLogEntity
            {
                Id = Guid.NewGuid(),
                ClientId = sourceFile.BackupSet.ClientId,
                BackupSetId = sourceFile.BackupSetId,
                FileId = sourceFile.Id,
                Action = "RENAME",
                Status = "COMMITTED",
                Details = $"Renamed from '{oldNormalized}' to '{newNormalized}'",
                ActorClientId = request.ActorClientId ?? request.ClientId ?? sourceFile.BackupSet.ClientId,
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.ChangeLogs.Add(changeLog);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("[MetadataService] RenameFileMetadata: Renamed '{OldPath}' -> '{NewPath}'", oldNormalized, newNormalized);

            return new RenameFileMetadataResponse { FileId = sourceFile.Id, Success = true };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "[MetadataService] RenameFileMetadata failed for '{OldPath}' -> '{NewPath}'", oldNormalized, newNormalized);
            return new RenameFileMetadataResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<GetFilesResponse> GetFilesAsync(GetFilesRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var access = await EnsureBackupSetAccessAsync(dbContext, request.BackupSetId, request.ClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new GetFilesResponse
            {
                BackupSetId = request.BackupSetId,
                Success = false,
                ErrorCode = access.ErrorCode ?? "ACCESS_DENIED",
                ErrorMessage = access.ErrorMessage
            };
        }

        var query = dbContext.Files.Where(f => f.BackupSetId == request.BackupSetId);
        if (!request.IncludeDeleted)
        {
            query = query.Where(f => !f.IsDeleted);
        }

        var files = await query
            .OrderBy(f => f.RelativePath)
            .Select(f => new FileMetadataDto
            {
                FileId = f.Id,
                RelativePath = f.RelativePath,
                CurrentVersionNumber = f.CurrentVersionNumber,
                IsDeleted = f.IsDeleted,
                UpdatedAtUtc = f.UpdatedAtUtc,
                CurrentSize = f.Versions
                    .Where(v => v.VersionNumber == f.CurrentVersionNumber)
                    .Select(v => (long?)v.Size)
                    .FirstOrDefault(),
                CurrentSha256 = f.Versions
                    .Where(v => v.VersionNumber == f.CurrentVersionNumber)
                    .Select(v => v.Sha256)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return new GetFilesResponse
        {
            BackupSetId = request.BackupSetId,
            Files = files,
            Success = true
        };
    }

    public async Task<GetVersionsResponse> GetVersionsAsync(GetVersionsRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var file = await dbContext.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == request.FileId, cancellationToken);

        if (file == null)
        {
            return new GetVersionsResponse
            {
                FileId = request.FileId,
                Success = false,
                ErrorCode = "FILE_NOT_FOUND",
                ErrorMessage = $"File {request.FileId} not found."
            };
        }

        var access = await EnsureBackupSetAccessAsync(dbContext, file.BackupSetId, request.ClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new GetVersionsResponse
            {
                FileId = request.FileId,
                Success = false,
                ErrorCode = access.ErrorCode ?? "ACCESS_DENIED",
                ErrorMessage = access.ErrorMessage
            };
        }

        var versions = await dbContext.FileVersions
            .Where(fv => fv.FileId == request.FileId)
            .OrderByDescending(fv => fv.VersionNumber)
            .Select(fv => new FileVersionDto
            {
                VersionNumber = fv.VersionNumber,
                Size = fv.Size,
                Sha256 = fv.Sha256,
                StorageKey = fv.StorageKey,
                CreatedAtUtc = fv.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new GetVersionsResponse
        {
            FileId = request.FileId,
            Versions = versions,
            Success = true
        };
    }

    public async Task<ResolveVersionResponse> ResolveVersionAsync(ResolveVersionRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var file = await dbContext.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == request.FileId, cancellationToken);

        if (file == null)
        {
            return new ResolveVersionResponse
            {
                FileId = request.FileId,
                Success = false,
                ErrorCode = "FILE_NOT_FOUND",
                ErrorMessage = $"File {request.FileId} not found."
            };
        }

        var access = await EnsureBackupSetAccessAsync(dbContext, file.BackupSetId, request.ClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new ResolveVersionResponse
            {
                FileId = request.FileId,
                BackupSetId = file.BackupSetId,
                Success = false,
                ErrorCode = access.ErrorCode ?? "ACCESS_DENIED",
                ErrorMessage = access.ErrorMessage
            };
        }

        var fv = await dbContext.FileVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.FileId == request.FileId && v.VersionNumber == request.VersionNumber, cancellationToken);

        if (fv == null)
        {
            return new ResolveVersionResponse
            {
                FileId = request.FileId,
                BackupSetId = file.BackupSetId,
                RelativePath = file.RelativePath,
                Version = null,
                Success = false,
                ErrorCode = "VERSION_NOT_FOUND",
                ErrorMessage = $"Version {request.VersionNumber} not found for file {request.FileId}."
            };
        }

        if (string.IsNullOrWhiteSpace(fv.StorageKey))
        {
            return new ResolveVersionResponse
            {
                FileId = request.FileId,
                BackupSetId = file.BackupSetId,
                RelativePath = file.RelativePath,
                Version = null,
                Success = false,
                ErrorCode = "RESTORE_STORAGE_MISSING",
                ErrorMessage = $"StorageKey is missing for version {request.VersionNumber} of file {request.FileId}."
            };
        }

        return new ResolveVersionResponse
        {
            FileId = request.FileId,
            BackupSetId = file.BackupSetId,
            RelativePath = file.RelativePath,
            Version = new FileVersionDto
            {
                VersionNumber = fv.VersionNumber,
                Size = fv.Size,
                Sha256 = fv.Sha256,
                StorageKey = fv.StorageKey,
                CreatedAtUtc = fv.CreatedAtUtc
            },
            Success = true
        };
    }

    public async Task<GetChangeLogResponse> GetChangeLogsAsync(GetChangeLogRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var access = await EnsureBackupSetAccessAsync(dbContext, request.BackupSetId, request.ClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new GetChangeLogResponse
            {
                BackupSetId = request.BackupSetId,
                Success = false,
                ErrorCode = access.ErrorCode ?? "ACCESS_DENIED",
                ErrorMessage = access.ErrorMessage
            };
        }

        int limit = Math.Clamp(request.Limit, 1, 500);
        var entries = await dbContext.ChangeLogs
            .Include(cl => cl.File)
            .Include(cl => cl.ActorClient)
            .Where(cl => cl.BackupSetId == request.BackupSetId)
            .OrderByDescending(cl => cl.CreatedAtUtc)
            .Take(limit)
            .Select(cl => new ChangeLogEntryDto
            {
                Id = cl.Id,
                BackupSetId = cl.BackupSetId,
                FileId = cl.FileId,
                RelativePath = cl.File != null ? cl.File.RelativePath : null,
                Action = cl.Action,
                Status = cl.Status,
                Details = cl.Details,
                CreatedAtUtc = cl.CreatedAtUtc,
                ActorClientId = cl.ActorClientId,
                ActorClientName = cl.ActorClient != null ? cl.ActorClient.Name : null,
                ActorMachineName = cl.ActorClient != null ? cl.ActorClient.MachineName : null
            })
            .ToListAsync(cancellationToken);

        return new GetChangeLogResponse
        {
            BackupSetId = request.BackupSetId,
            Entries = entries,
            Success = true
        };
    }

    public async Task<JoinSharedBackupSetResponse> JoinSharedBackupSetAsync(JoinSharedBackupSetRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (!request.ClientId.HasValue || request.ClientId.Value == Guid.Empty)
        {
            return new JoinSharedBackupSetResponse
            {
                Success = false,
                ErrorCode = "CLIENT_NOT_REGISTERED",
                ErrorMessage = "Client must be registered before joining a shared backup set."
            };
        }

        if (!ShareCodeHelper.TryNormalize(request.ShareCode, out var normalizedCode, out var codeError))
        {
            return new JoinSharedBackupSetResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.InvalidShareCode,
                ErrorMessage = codeError ?? "Invalid share code format."
            };
        }

        var backupSet = await dbContext.BackupSets
            .Include(bs => bs.Members)
            .FirstOrDefaultAsync(bs => bs.ShareCode == normalizedCode, cancellationToken);

        if (backupSet == null)
        {
            return new JoinSharedBackupSetResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.BackupSetNotFound,
                ErrorMessage = $"Shared backup set with code '{normalizedCode}' was not found."
            };
        }

        if (!string.Equals(backupSet.AccessMode, "SHARED", StringComparison.OrdinalIgnoreCase))
        {
            return new JoinSharedBackupSetResponse
            {
                Success = false,
                ErrorCode = BackupErrorCodes.NotShared,
                ErrorMessage = "The specified backup set is not in SHARED access mode."
            };
        }

        Guid clientId = request.ClientId.Value;
        var client = await dbContext.Clients.FindAsync([clientId], cancellationToken);
        if (client == null)
        {
            return new JoinSharedBackupSetResponse
            {
                Success = false,
                ErrorCode = "CLIENT_NOT_REGISTERED",
                ErrorMessage = $"Client {clientId} was not found."
            };
        }

        var existingMember = backupSet.Members.FirstOrDefault(m => m.ClientId == clientId)
            ?? await dbContext.BackupSetMembers.FirstOrDefaultAsync(m => m.BackupSetId == backupSet.Id && m.ClientId == clientId, cancellationToken);

        if (existingMember == null)
        {
            var newMember = new BackupSetMemberEntity
            {
                BackupSetId = backupSet.Id,
                ClientId = clientId,
                Role = "MEMBER",
                JoinedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow
            };
            dbContext.BackupSetMembers.Add(newMember);
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[MetadataService] Added Client {ClientId} ('{Name}') as MEMBER to Shared BackupSet {BackupSetId} ('{SetName}')",
                clientId, client.Name, backupSet.Id, backupSet.Name);
        }
        else
        {
            existingMember.LastSeenAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[MetadataService] Client {ClientId} is already member of Shared BackupSet {BackupSetId}. Returning idempotent success.",
                clientId, backupSet.Id);
        }

        return new JoinSharedBackupSetResponse
        {
            Success = true,
            BackupSetId = backupSet.Id,
            Name = backupSet.Name,
            AccessMode = BackupSetAccessMode.Shared,
            OwnerClientId = backupSet.ClientId,
            ShareCode = backupSet.ShareCode
        };
    }

    public async Task<GetBackupSetMembersResponse> GetBackupSetMembersAsync(GetBackupSetMembersRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var access = await EnsureBackupSetAccessAsync(dbContext, request.BackupSetId, request.ClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new GetBackupSetMembersResponse
            {
                BackupSetId = request.BackupSetId,
                Success = false,
                ErrorCode = access.ErrorCode ?? "ACCESS_DENIED",
                ErrorMessage = access.ErrorMessage
            };
        }

        var members = await dbContext.BackupSetMembers
            .Include(m => m.Client)
            .Where(m => m.BackupSetId == request.BackupSetId)
            .OrderBy(m => m.JoinedAtUtc)
            .Select(m => new BackupSetMemberDto
            {
                ClientId = m.ClientId,
                ClientName = m.Client.Name,
                MachineName = m.Client.MachineName,
                Role = m.Role,
                JoinedAtUtc = m.JoinedAtUtc,
                LastSeenAtUtc = m.LastSeenAtUtc
            })
            .ToListAsync(cancellationToken);

        return new GetBackupSetMembersResponse
        {
            BackupSetId = request.BackupSetId,
            Members = members,
            Success = true
        };
    }

    public async Task<GetBackupSetManifestResponse> GetBackupSetManifestAsync(GetBackupSetManifestRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var access = await EnsureBackupSetAccessAsync(dbContext, request.BackupSetId, request.ClientId, cancellationToken);
        if (!access.Allowed)
        {
            return new GetBackupSetManifestResponse
            {
                BackupSetId = request.BackupSetId,
                Success = false,
                ErrorCode = access.ErrorCode ?? "ACCESS_DENIED",
                ErrorMessage = access.ErrorMessage
            };
        }

        var entries = await dbContext.Files
            .Where(f => f.BackupSetId == request.BackupSetId)
            .OrderBy(f => f.RelativePath)
            .Select(f => new BackupSetManifestEntryDto
            {
                FileId = f.Id,
                RelativePath = f.RelativePath,
                CurrentVersionNumber = f.CurrentVersionNumber,
                IsDeleted = f.IsDeleted,
                UpdatedAtUtc = f.UpdatedAtUtc,
                Size = f.Versions
                    .Where(v => v.VersionNumber == f.CurrentVersionNumber)
                    .Select(v => v.Size)
                    .FirstOrDefault(),
                Sha256 = f.Versions
                    .Where(v => v.VersionNumber == f.CurrentVersionNumber)
                    .Select(v => v.Sha256)
                    .FirstOrDefault() ?? string.Empty
            })
            .ToListAsync(cancellationToken);

        return new GetBackupSetManifestResponse
        {
            BackupSetId = request.BackupSetId,
            Success = true,
            GeneratedAtUtc = DateTime.UtcNow,
            Entries = entries
        };
    }
}
