using Backup.MetadataService.Data;
using Backup.MetadataService.Entities;
using Backup.Shared.DTOs;
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

        var backupSet = new BackupSetEntity
        {
            Id = Guid.NewGuid(),
            ClientId = request.ClientId,
            Name = request.Name.Trim(),
            LocalRootPath = string.IsNullOrWhiteSpace(request.LocalRootPath) ? "/" : request.LocalRootPath.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true
        };

        dbContext.BackupSets.Add(backupSet);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[MetadataService] Created BackupSet {BackupSetId} ('{Name}') for Client {ClientId}", backupSet.Id, backupSet.Name, request.ClientId);

        return new CreateBackupSetResponse
        {
            BackupSetId = backupSet.Id,
            Name = backupSet.Name,
            LocalRootPath = backupSet.LocalRootPath,
            Success = true
        };
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

        var backupSet = await dbContext.BackupSets.FindAsync([request.BackupSetId], cancellationToken);
        if (backupSet == null)
        {
            return new PrepareVersionResponse { Success = false, ErrorMessage = $"BackupSet {request.BackupSetId} was not found." };
        }

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
            string action = pending.ExpectedVersionNumber == 1 ? "CREATE" : "MODIFY";
            var changeLog = new ChangeLogEntity
            {
                Id = Guid.NewGuid(),
                ClientId = file.BackupSet.ClientId,
                BackupSetId = file.BackupSetId,
                FileId = file.Id,
                Action = action,
                Status = "COMMITTED",
                Details = $"Version {pending.ExpectedVersionNumber} committed with Sha256 {request.ActualSha256}",
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

        var fv = await dbContext.FileVersions
            .FirstOrDefaultAsync(v => v.FileId == request.FileId && v.VersionNumber == request.VersionNumber, cancellationToken);

        if (fv == null)
        {
            return new ResolveVersionResponse
            {
                FileId = request.FileId,
                Version = null,
                Success = false,
                ErrorMessage = $"Version {request.VersionNumber} not found for file {request.FileId}."
            };
        }

        return new ResolveVersionResponse
        {
            FileId = request.FileId,
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
}
