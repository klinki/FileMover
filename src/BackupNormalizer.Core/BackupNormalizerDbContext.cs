using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed class BackupNormalizerDbContext(DbContextOptions<BackupNormalizerDbContext> options) : DbContext(options)
{
    public DbSet<StorageRootEntity> StorageRoots => Set<StorageRootEntity>();
    public DbSet<ScanEntity> Scans => Set<ScanEntity>();
    public DbSet<FileEntryEntity> FileEntries => Set<FileEntryEntity>();
    public DbSet<FileHashEntity> FileHashes => Set<FileHashEntity>();
    public DbSet<PlanEntity> Plans => Set<PlanEntity>();
    public DbSet<PlanOperationEntity> PlanOperations => Set<PlanOperationEntity>();
    public DbSet<ExecutionLogEntity> ExecutionLogs => Set<ExecutionLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => ConfigureModel(modelBuilder);

    internal static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StorageRootEntity>(entity =>
        {
            entity.ToTable("StorageRoot");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("Id");
            entity.Property(x => x.Name).HasColumnName("Name").IsRequired();
            entity.Property(x => x.Path).HasColumnName("Path").IsRequired();
            entity.Property(x => x.Writable).HasColumnName("Writable").HasDefaultValue(true);
            entity.Property(x => x.FileSystemId).HasColumnName("FileSystemId").HasDefaultValue("unknown");
            entity.Property(x => x.CaseSensitivity).HasColumnName("CaseSensitivity").HasDefaultValue("unknown");
            entity.Property(x => x.CreatedUtc).HasColumnName("CreatedUtc").IsRequired();
        });

        modelBuilder.Entity<ScanEntity>(entity =>
        {
            entity.ToTable("Scan");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            entity.Property(x => x.StorageRootId).HasColumnName("StorageRootId").IsRequired();
            entity.Property(x => x.StartedUtc).HasColumnName("StartedUtc").IsRequired();
            entity.Property(x => x.CompletedUtc).HasColumnName("CompletedUtc");
            entity.Property(x => x.Status).HasColumnName("Status").HasDefaultValue("Started");
            entity.HasOne<StorageRootEntity>().WithMany().HasForeignKey(x => x.StorageRootId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<FileEntryEntity>(entity =>
        {
            entity.ToTable("FileEntry");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            entity.Property(x => x.StorageRootId).HasColumnName("StorageRootId").IsRequired();
            entity.Property(x => x.RelativePath).HasColumnName("RelativePath").IsRequired();
            entity.Property(x => x.Name).HasColumnName("Name").IsRequired();
            entity.Property(x => x.Size).HasColumnName("Size");
            entity.Property(x => x.ModifiedUtc).HasColumnName("ModifiedUtc").IsRequired();
            entity.Property(x => x.CreatedUtc).HasColumnName("CreatedUtc");
            entity.Property(x => x.FileIdentity).HasColumnName("FileIdentity");
            entity.Property(x => x.LastSeenScanId).HasColumnName("LastSeenScanId");
            entity.Property(x => x.Status).HasColumnName("Status").HasDefaultValue("Ok");
            entity.Property(x => x.Error).HasColumnName("Error");
            entity.HasAlternateKey(x => new { x.StorageRootId, x.RelativePath });
            entity.HasIndex(x => x.StorageRootId).HasDatabaseName("IX_FileEntry_Root");
            entity.HasIndex(x => x.Size).HasDatabaseName("IX_FileEntry_Size");
            entity.HasOne<StorageRootEntity>().WithMany().HasForeignKey(x => x.StorageRootId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<FileHashEntity>(entity =>
        {
            entity.ToTable("FileHash");
            entity.HasKey(x => new { x.FileEntryId, x.Algorithm });
            entity.Property(x => x.FileEntryId).HasColumnName("FileEntryId");
            entity.Property(x => x.Algorithm).HasColumnName("Algorithm");
            entity.Property(x => x.Digest).HasColumnName("Digest").IsRequired();
            entity.Property(x => x.SizeAtHash).HasColumnName("SizeAtHash");
            entity.Property(x => x.ModifiedUtcAtHash).HasColumnName("ModifiedUtcAtHash").IsRequired();
            entity.Property(x => x.CalculatedUtc).HasColumnName("CalculatedUtc").IsRequired();
            entity.Property(x => x.State).HasColumnName("State").HasDefaultValue("Ok");
            entity.HasIndex(x => new { x.Algorithm, x.Digest }).HasDatabaseName("IX_FileHash_Digest");
            entity.HasOne<FileEntryEntity>().WithMany().HasForeignKey(x => x.FileEntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlanEntity>(entity =>
        {
            entity.ToTable("Plan");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("Id");
            entity.Property(x => x.CreatedUtc).HasColumnName("CreatedUtc").IsRequired();
            entity.Property(x => x.SourceDatabasePath).HasColumnName("SourceDatabasePath").IsRequired();
            entity.Property(x => x.SourceRootId).HasColumnName("SourceRootId").IsRequired();
            entity.Property(x => x.SourceRootPath).HasColumnName("SourceRootPath").IsRequired();
            entity.Property(x => x.TargetRootId).HasColumnName("TargetRootId").IsRequired();
            entity.Property(x => x.TargetRootPath).HasColumnName("TargetRootPath").IsRequired();
            entity.Property(x => x.Status).HasColumnName("Status").HasDefaultValue("Planned");
            entity.Property(x => x.EstimatedBytesCopied).HasColumnName("EstimatedBytesCopied").HasDefaultValue(0L);
        });

        modelBuilder.Entity<PlanOperationEntity>(entity =>
        {
            entity.ToTable("PlanOperation");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            entity.Property(x => x.PlanId).HasColumnName("PlanId").IsRequired();
            entity.Property(x => x.Sequence).HasColumnName("Sequence");
            entity.Property(x => x.Type).HasColumnName("Type").IsRequired();
            entity.Property(x => x.SourceKind).HasColumnName("SourceKind");
            entity.Property(x => x.SourceRootId).HasColumnName("SourceRootId");
            entity.Property(x => x.SourcePath).HasColumnName("SourcePath");
            entity.Property(x => x.DestinationRootId).HasColumnName("DestinationRootId");
            entity.Property(x => x.DestinationPath).HasColumnName("DestinationPath");
            entity.Property(x => x.ExpectedSize).HasColumnName("ExpectedSize").HasDefaultValue(0L);
            entity.Property(x => x.ExpectedHash).HasColumnName("ExpectedHash");
            entity.Property(x => x.Status).HasColumnName("Status").HasDefaultValue("Planned");
            entity.Property(x => x.StartedUtc).HasColumnName("StartedUtc");
            entity.Property(x => x.CompletedUtc).HasColumnName("CompletedUtc");
            entity.Property(x => x.Error).HasColumnName("Error");
            entity.HasIndex(x => new { x.PlanId, x.Sequence }).HasDatabaseName("IX_PlanOp_Plan");
            entity.HasOne<PlanEntity>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExecutionLogEntity>(entity =>
        {
            entity.ToTable("ExecutionLog");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            entity.Property(x => x.PlanOperationId).HasColumnName("PlanOperationId");
            entity.Property(x => x.TimestampUtc).HasColumnName("TimestampUtc").IsRequired();
            entity.Property(x => x.Level).HasColumnName("Level").IsRequired();
            entity.Property(x => x.Message).HasColumnName("Message").IsRequired();
        });
    }
}
