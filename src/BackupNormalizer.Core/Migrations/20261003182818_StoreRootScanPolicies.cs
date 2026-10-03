using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupNormalizer.Core.Migrations
{
    /// <inheritdoc />
    public partial class StoreRootScanPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RootScanPolicy",
                columns: table => new
                {
                    StorageRootId = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedPathRegexesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootScanPolicy", x => x.StorageRootId);
                    table.ForeignKey(
                        name: "FK_RootScanPolicy_StorageRoot_StorageRootId",
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
                name: "RootScanPolicy");
        }
    }
}
