using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupNormalizer.Migrations;

[DbContext(typeof(BackupNormalizerDbContext))]
[Migration("20260924000000_InitialEfSchema")]
public sealed class InitialEfSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER NOT NULL);
            INSERT INTO SchemaVersion(Version)
                SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM SchemaVersion);

            CREATE TABLE IF NOT EXISTS StorageRoot(
              Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Path TEXT NOT NULL, Role TEXT NOT NULL DEFAULT 'Unknown',
              Writable INTEGER NOT NULL DEFAULT 1, FileSystemId TEXT NOT NULL DEFAULT 'unknown',
              CaseSensitivity TEXT NOT NULL DEFAULT 'unknown', CreatedUtc TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS Scan(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, StorageRootId TEXT NOT NULL, StartedUtc TEXT NOT NULL,
              CompletedUtc TEXT, Status TEXT NOT NULL DEFAULT 'Started',
              FOREIGN KEY(StorageRootId) REFERENCES StorageRoot(Id));
            CREATE INDEX IF NOT EXISTS IX_Scan_StorageRootId ON Scan(StorageRootId);

            CREATE TABLE IF NOT EXISTS FileEntry(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, StorageRootId TEXT NOT NULL, RelativePath TEXT NOT NULL,
              Name TEXT NOT NULL, Size INTEGER NOT NULL, ModifiedUtc TEXT NOT NULL, CreatedUtc TEXT,
              FileIdentity TEXT, LastSeenScanId INTEGER NOT NULL, Status TEXT NOT NULL DEFAULT 'Ok', Error TEXT,
              UNIQUE(StorageRootId, RelativePath),
              FOREIGN KEY(StorageRootId) REFERENCES StorageRoot(Id));
            CREATE INDEX IF NOT EXISTS IX_FileEntry_Root ON FileEntry(StorageRootId);
            CREATE INDEX IF NOT EXISTS IX_FileEntry_Size ON FileEntry(Size);

            CREATE TABLE IF NOT EXISTS FileHash(
              FileEntryId INTEGER NOT NULL, Algorithm TEXT NOT NULL, Digest TEXT NOT NULL,
              SizeAtHash INTEGER NOT NULL, ModifiedUtcAtHash TEXT NOT NULL, CalculatedUtc TEXT NOT NULL,
              State TEXT NOT NULL DEFAULT 'Ok',
              PRIMARY KEY(FileEntryId, Algorithm),
              FOREIGN KEY(FileEntryId) REFERENCES FileEntry(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_FileHash_Digest ON FileHash(Algorithm, Digest);

            CREATE TABLE IF NOT EXISTS CanonicalEntry(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, RelativePath TEXT NOT NULL,
              Size INTEGER NOT NULL, ExpectedHash TEXT, SourceFileEntryId INTEGER);

            CREATE TABLE IF NOT EXISTS Plan(
              Id TEXT PRIMARY KEY, CreatedUtc TEXT NOT NULL, CanonicalRootId TEXT NOT NULL,
              Status TEXT NOT NULL DEFAULT 'Planned', EstimatedBytesCopied INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS PlanOperation(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, PlanId TEXT NOT NULL, Sequence INTEGER NOT NULL,
              Type TEXT NOT NULL, SourceRootId TEXT, SourcePath TEXT,
              DestinationRootId TEXT, DestinationPath TEXT,
              ExpectedSize INTEGER NOT NULL DEFAULT 0, ExpectedHash TEXT,
              Status TEXT NOT NULL DEFAULT 'Planned', StartedUtc TEXT, CompletedUtc TEXT, Error TEXT,
              FOREIGN KEY(PlanId) REFERENCES Plan(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_PlanOp_Plan ON PlanOperation(PlanId, Sequence);

            CREATE TABLE IF NOT EXISTS ExecutionLog(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, PlanOperationId INTEGER NOT NULL,
              TimestampUtc TEXT NOT NULL, Level TEXT NOT NULL, Message TEXT NOT NULL);
            """);
    }

    // This migration may baseline an existing database, so rollback must never drop its data.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

        modelBuilder.Entity("BackupNormalizer.CanonicalEntryEntity", b =>
            {
                b.Property<long>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasColumnName("Id");

                b.Property<string>("ExpectedHash")
                    .HasColumnType("TEXT")
                    .HasColumnName("ExpectedHash");

                b.Property<string>("RelativePath")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("RelativePath");

                b.Property<long>("Size")
                    .HasColumnType("INTEGER")
                    .HasColumnName("Size");

                b.Property<long?>("SourceFileEntryId")
                    .HasColumnType("INTEGER")
                    .HasColumnName("SourceFileEntryId");

                b.HasKey("Id");

                b.ToTable("CanonicalEntry", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.ExecutionLogEntity", b =>
            {
                b.Property<long>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasColumnName("Id");

                b.Property<string>("Level")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("Level");

                b.Property<string>("Message")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("Message");

                b.Property<long>("PlanOperationId")
                    .HasColumnType("INTEGER")
                    .HasColumnName("PlanOperationId");

                b.Property<string>("TimestampUtc")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("TimestampUtc");

                b.HasKey("Id");

                b.ToTable("ExecutionLog", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.FileEntryEntity", b =>
            {
                b.Property<long>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasColumnName("Id");

                b.Property<string>("CreatedUtc")
                    .HasColumnType("TEXT")
                    .HasColumnName("CreatedUtc");

                b.Property<string>("Error")
                    .HasColumnType("TEXT")
                    .HasColumnName("Error");

                b.Property<string>("FileIdentity")
                    .HasColumnType("TEXT")
                    .HasColumnName("FileIdentity");

                b.Property<long>("LastSeenScanId")
                    .HasColumnType("INTEGER")
                    .HasColumnName("LastSeenScanId");

                b.Property<string>("ModifiedUtc")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("ModifiedUtc");

                b.Property<string>("Name")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("Name");

                b.Property<string>("RelativePath")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("RelativePath");

                b.Property<long>("Size")
                    .HasColumnType("INTEGER")
                    .HasColumnName("Size");

                b.Property<string>("Status")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("Ok")
                    .HasColumnName("Status");

                b.Property<string>("StorageRootId")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("StorageRootId");

                b.HasKey("Id");

                b.HasAlternateKey("StorageRootId", "RelativePath");

                b.HasIndex("Size")
                    .HasDatabaseName("IX_FileEntry_Size");

                b.HasIndex("StorageRootId")
                    .HasDatabaseName("IX_FileEntry_Root");

                b.ToTable("FileEntry", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.FileHashEntity", b =>
            {
                b.Property<long>("FileEntryId")
                    .HasColumnType("INTEGER")
                    .HasColumnName("FileEntryId");

                b.Property<string>("Algorithm")
                    .HasColumnType("TEXT")
                    .HasColumnName("Algorithm");

                b.Property<string>("CalculatedUtc")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("CalculatedUtc");

                b.Property<string>("Digest")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("Digest");

                b.Property<string>("ModifiedUtcAtHash")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("ModifiedUtcAtHash");

                b.Property<long>("SizeAtHash")
                    .HasColumnType("INTEGER")
                    .HasColumnName("SizeAtHash");

                b.Property<string>("State")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("Ok")
                    .HasColumnName("State");

                b.HasKey("FileEntryId", "Algorithm");

                b.HasIndex("Algorithm", "Digest")
                    .HasDatabaseName("IX_FileHash_Digest");

                b.ToTable("FileHash", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.PlanEntity", b =>
            {
                b.Property<string>("Id")
                    .HasColumnType("TEXT")
                    .HasColumnName("Id");

                b.Property<string>("CanonicalRootId")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("CanonicalRootId");

                b.Property<string>("CreatedUtc")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("CreatedUtc");

                b.Property<long>("EstimatedBytesCopied")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasDefaultValue(0L)
                    .HasColumnName("EstimatedBytesCopied");

                b.Property<string>("Status")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("Planned")
                    .HasColumnName("Status");

                b.HasKey("Id");

                b.ToTable("Plan", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.PlanOperationEntity", b =>
            {
                b.Property<long>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasColumnName("Id");

                b.Property<string>("CompletedUtc")
                    .HasColumnType("TEXT")
                    .HasColumnName("CompletedUtc");

                b.Property<string>("DestinationPath")
                    .HasColumnType("TEXT")
                    .HasColumnName("DestinationPath");

                b.Property<string>("DestinationRootId")
                    .HasColumnType("TEXT")
                    .HasColumnName("DestinationRootId");

                b.Property<string>("Error")
                    .HasColumnType("TEXT")
                    .HasColumnName("Error");

                b.Property<string>("ExpectedHash")
                    .HasColumnType("TEXT")
                    .HasColumnName("ExpectedHash");

                b.Property<long>("ExpectedSize")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasDefaultValue(0L)
                    .HasColumnName("ExpectedSize");

                b.Property<string>("PlanId")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("PlanId");

                b.Property<int>("Sequence")
                    .HasColumnType("INTEGER")
                    .HasColumnName("Sequence");

                b.Property<string>("SourcePath")
                    .HasColumnType("TEXT")
                    .HasColumnName("SourcePath");

                b.Property<string>("SourceRootId")
                    .HasColumnType("TEXT")
                    .HasColumnName("SourceRootId");

                b.Property<string>("StartedUtc")
                    .HasColumnType("TEXT")
                    .HasColumnName("StartedUtc");

                b.Property<string>("Status")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("Planned")
                    .HasColumnName("Status");

                b.Property<string>("Type")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("Type");

                b.HasKey("Id");

                b.HasIndex("PlanId", "Sequence")
                    .HasDatabaseName("IX_PlanOp_Plan");

                b.ToTable("PlanOperation", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.ScanEntity", b =>
            {
                b.Property<long>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasColumnName("Id");

                b.Property<string>("CompletedUtc")
                    .HasColumnType("TEXT")
                    .HasColumnName("CompletedUtc");

                b.Property<string>("StartedUtc")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("StartedUtc");

                b.Property<string>("Status")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("Started")
                    .HasColumnName("Status");

                b.Property<string>("StorageRootId")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("StorageRootId");

                b.HasKey("Id");

                b.HasIndex("StorageRootId");

                b.ToTable("Scan", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.StorageRootEntity", b =>
            {
                b.Property<string>("Id")
                    .HasColumnType("TEXT")
                    .HasColumnName("Id");

                b.Property<string>("CaseSensitivity")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("unknown")
                    .HasColumnName("CaseSensitivity");

                b.Property<string>("CreatedUtc")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("CreatedUtc");

                b.Property<string>("FileSystemId")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("unknown")
                    .HasColumnName("FileSystemId");

                b.Property<string>("Name")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("Name");

                b.Property<string>("Path")
                    .IsRequired()
                    .HasColumnType("TEXT")
                    .HasColumnName("Path");

                b.Property<string>("Role")
                    .IsRequired()
                    .ValueGeneratedOnAdd()
                    .HasColumnType("TEXT")
                    .HasDefaultValue("Unknown")
                    .HasColumnName("Role");

                b.Property<bool>("Writable")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("INTEGER")
                    .HasDefaultValue(true)
                    .HasColumnName("Writable");

                b.HasKey("Id");

                b.ToTable("StorageRoot", (string)null);
            });

        modelBuilder.Entity("BackupNormalizer.FileEntryEntity", b =>
            {
                b.HasOne("BackupNormalizer.StorageRootEntity", null)
                    .WithMany()
                    .HasForeignKey("StorageRootId")
                    .OnDelete(DeleteBehavior.NoAction)
                    .IsRequired();
            });

        modelBuilder.Entity("BackupNormalizer.FileHashEntity", b =>
            {
                b.HasOne("BackupNormalizer.FileEntryEntity", null)
                    .WithMany()
                    .HasForeignKey("FileEntryId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();
            });

        modelBuilder.Entity("BackupNormalizer.PlanOperationEntity", b =>
            {
                b.HasOne("BackupNormalizer.PlanEntity", null)
                    .WithMany()
                    .HasForeignKey("PlanId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();
            });

        modelBuilder.Entity("BackupNormalizer.ScanEntity", b =>
            {
                b.HasOne("BackupNormalizer.StorageRootEntity", null)
                    .WithMany()
                    .HasForeignKey("StorageRootId")
                    .OnDelete(DeleteBehavior.NoAction)
                    .IsRequired();
            });
#pragma warning restore 612, 618
    }
}
