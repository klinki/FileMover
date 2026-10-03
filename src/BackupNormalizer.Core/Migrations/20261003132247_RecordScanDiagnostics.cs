using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupNormalizer.Core.Migrations
{
    /// <inheritdoc />
    public partial class RecordScanDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ErrorCount",
                table: "Scan",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FallbackReason",
                table: "Scan",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Scan",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScannedCount",
                table: "Scan",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScanDiagnostic",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<long>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanDiagnostic", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanDiagnostic_Scan_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanDiagnostic_ScanId",
                table: "ScanDiagnostic",
                column: "ScanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanDiagnostic");

            migrationBuilder.DropColumn(
                name: "ErrorCount",
                table: "Scan");

            migrationBuilder.DropColumn(
                name: "FallbackReason",
                table: "Scan");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Scan");

            migrationBuilder.DropColumn(
                name: "ScannedCount",
                table: "Scan");
        }
    }
}
