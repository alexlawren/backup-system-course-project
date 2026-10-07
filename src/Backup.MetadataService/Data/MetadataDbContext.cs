using Backup.MetadataService.Entities;
using Microsoft.EntityFrameworkCore;

namespace Backup.MetadataService.Data;

public class MetadataDbContext : DbContext
{
    public MetadataDbContext(DbContextOptions<MetadataDbContext> options) : base(options)
    {
    }

    public DbSet<ClientEntity> Clients => Set<ClientEntity>();
    public DbSet<BackupSetEntity> BackupSets => Set<BackupSetEntity>();
    public DbSet<FileEntity> Files => Set<FileEntity>();
    public DbSet<FileVersionEntity> FileVersions => Set<FileVersionEntity>();
    public DbSet<ChangeLogEntity> ChangeLogs => Set<ChangeLogEntity>();
    public DbSet<PendingFileOperationEntity> PendingFileOperations => Set<PendingFileOperationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ClientEntity
        modelBuilder.Entity<ClientEntity>(b =>
        {
            b.ToTable("clients");
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).IsRequired().HasMaxLength(255);
            b.Property(c => c.MachineName).IsRequired().HasMaxLength(255);
            b.Property(c => c.CreatedAtUtc).IsRequired();
            b.Property(c => c.LastSeenAtUtc).IsRequired();
        });

        // BackupSetEntity
        modelBuilder.Entity<BackupSetEntity>(b =>
        {
            b.ToTable("backup_sets");
            b.HasKey(bs => bs.Id);
            b.Property(bs => bs.Name).IsRequired().HasMaxLength(255);
            b.Property(bs => bs.LocalRootPath).IsRequired().HasMaxLength(1024);
            b.Property(bs => bs.CreatedAtUtc).IsRequired();
            b.Property(bs => bs.IsActive).IsRequired().HasDefaultValue(true);

            b.HasOne(bs => bs.Client)
                .WithMany(c => c.BackupSets)
                .HasForeignKey(bs => bs.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(bs => bs.ClientId);
        });

        // FileEntity
        modelBuilder.Entity<FileEntity>(b =>
        {
            b.ToTable("files");
            b.HasKey(f => f.Id);
            b.Property(f => f.RelativePath).IsRequired().HasMaxLength(2048);
            b.Property(f => f.CurrentVersionNumber).IsRequired().HasDefaultValue(0);
            b.Property(f => f.IsDeleted).IsRequired().HasDefaultValue(false);
            b.Property(f => f.CreatedAtUtc).IsRequired();
            b.Property(f => f.UpdatedAtUtc).IsRequired();

            b.HasOne(f => f.BackupSet)
                .WithMany(bs => bs.Files)
                .HasForeignKey(f => f.BackupSetId)
                .OnDelete(DeleteBehavior.Cascade);

            // UNIQUE (BackupSetId, RelativePath)
            b.HasIndex(f => new { f.BackupSetId, f.RelativePath })
                .IsUnique();
        });

        // FileVersionEntity
        modelBuilder.Entity<FileVersionEntity>(b =>
        {
            b.ToTable("file_versions");
            b.HasKey(fv => fv.Id);
            b.Property(fv => fv.VersionNumber).IsRequired();
            b.Property(fv => fv.Size).IsRequired();
            b.Property(fv => fv.Sha256).IsRequired().HasMaxLength(64);
            b.Property(fv => fv.StorageKey).HasMaxLength(1024);
            b.Property(fv => fv.CreatedAtUtc).IsRequired();

            b.HasOne(fv => fv.File)
                .WithMany(f => f.Versions)
                .HasForeignKey(fv => fv.FileId)
                .OnDelete(DeleteBehavior.Cascade);

            // UNIQUE (FileId, VersionNumber)
            b.HasIndex(fv => new { fv.FileId, fv.VersionNumber })
                .IsUnique();

            b.HasIndex(fv => fv.FileId);
        });

        // ChangeLogEntity
        modelBuilder.Entity<ChangeLogEntity>(b =>
        {
            b.ToTable("change_logs");
            b.HasKey(cl => cl.Id);
            b.Property(cl => cl.Action).IsRequired().HasMaxLength(64);
            b.Property(cl => cl.Status).IsRequired().HasMaxLength(64);
            b.Property(cl => cl.Details).HasMaxLength(2048);
            b.Property(cl => cl.CreatedAtUtc).IsRequired();

            b.HasOne(cl => cl.Client)
                .WithMany(c => c.ChangeLogs)
                .HasForeignKey(cl => cl.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(cl => cl.BackupSet)
                .WithMany(bs => bs.ChangeLogs)
                .HasForeignKey(cl => cl.BackupSetId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(cl => cl.File)
                .WithMany(f => f.ChangeLogs)
                .HasForeignKey(cl => cl.FileId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasIndex(cl => cl.CreatedAtUtc);
        });

        // PendingFileOperationEntity
        modelBuilder.Entity<PendingFileOperationEntity>(b =>
        {
            b.ToTable("pending_file_operations");
            b.HasKey(p => p.OperationId);
            b.Property(p => p.ExpectedVersionNumber).IsRequired();
            b.Property(p => p.ExpectedSize).IsRequired();
            b.Property(p => p.ExpectedSha256).IsRequired().HasMaxLength(64);
            b.Property(p => p.CreatedAtUtc).IsRequired();
            b.Property(p => p.ExpiresAtUtc).IsRequired();

            b.HasOne(p => p.File)
                .WithMany(f => f.PendingOperations)
                .HasForeignKey(p => p.FileId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(p => p.ExpiresAtUtc);
        });
    }
}
