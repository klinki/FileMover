using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupNormalizer.Core.Migrations
{
    /// <inheritdoc />
    public partial class TrackUsnCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanCheckpoint",
                columns: table => new
                {
                    StorageRootId = table.Column<string>(type: "TEXT", nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", nullable: false),
                    VolumeIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    RootIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    JournalId = table.Column<string>(type: "TEXT", nullable: false),
                    NextUsn = table.Column<long>(type: "INTEGER", nullable: false),
                    ScanId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanCheckpoint", x => x.StorageRootId);
                    table.ForeignKey(
                        name: "FK_ScanCheckpoint_StorageRoot_StorageRootId",
                        column: x => x.StorageRootId,
                        principalTable: "StorageRoot",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanCheckpoint");
        }
    }
}
