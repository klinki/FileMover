using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupNormalizer.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CanonicalEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpectedHash = table.Column<string>(type: "TEXT", nullable: true),
                    SourceFileEntryId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalEntry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanOperationId = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plan",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalRootId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Planned"),
                    EstimatedBytesCopied = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plan", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageRoot",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Unknown"),
                    Writable = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    FileSystemId = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "unknown"),
                    CaseSensitivity = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "unknown"),
                    CreatedUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageRoot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlanOperation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRootId = table.Column<string>(type: "TEXT", nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: true),
                    DestinationRootId = table.Column<string>(type: "TEXT", nullable: true),
                    DestinationPath = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedSize = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    ExpectedHash = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Planned"),
                    StartedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanOperation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanOperation_Plan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StorageRootId = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    FileIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenScanId = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Ok"),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileEntry", x => x.Id);
                    table.UniqueConstraint("AK_FileEntry_StorageRootId_RelativePath", x => new { x.StorageRootId, x.RelativePath });
                    table.ForeignKey(
                        name: "FK_FileEntry_StorageRoot_StorageRootId",
                        column: x => x.StorageRootId,
                        principalTable: "StorageRoot",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Scan",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StorageRootId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Started")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scan", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Scan_StorageRoot_StorageRootId",
                        column: x => x.StorageRootId,
                        principalTable: "StorageRoot",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "FileHash",
                columns: table => new
                {
                    FileEntryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", nullable: false),
                    Digest = table.Column<string>(type: "TEXT", nullable: false),
                    SizeAtHash = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtcAtHash = table.Column<string>(type: "TEXT", nullable: false),
                    CalculatedUtc = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Ok")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileHash", x => new { x.FileEntryId, x.Algorithm });
                    table.ForeignKey(
                        name: "FK_FileHash_FileEntry_FileEntryId",
                        column: x => x.FileEntryId,
                        principalTable: "FileEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileEntry_Root",
                table: "FileEntry",
                column: "StorageRootId");

            migrationBuilder.CreateIndex(
                name: "IX_FileEntry_Size",
                table: "FileEntry",
                column: "Size");

            migrationBuilder.CreateIndex(
                name: "IX_FileHash_Digest",
                table: "FileHash",
                columns: new[] { "Algorithm", "Digest" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanOp_Plan",
                table: "PlanOperation",
                columns: new[] { "PlanId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Scan_StorageRootId",
                table: "Scan",
                column: "StorageRootId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanonicalEntry");

            migrationBuilder.DropTable(
                name: "ExecutionLog");

            migrationBuilder.DropTable(
                name: "FileHash");

            migrationBuilder.DropTable(
                name: "PlanOperation");

            migrationBuilder.DropTable(
                name: "Scan");

            migrationBuilder.DropTable(
                name: "FileEntry");

            migrationBuilder.DropTable(
                name: "Plan");

            migrationBuilder.DropTable(
                name: "StorageRoot");
        }
    }
}
