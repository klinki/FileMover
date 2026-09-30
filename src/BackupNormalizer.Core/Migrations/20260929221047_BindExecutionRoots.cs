using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupNormalizer.Core.Migrations
{
    /// <inheritdoc />
    public partial class BindExecutionRoots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionSourceRootPath",
                table: "Plan",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionTargetRootPath",
                table: "Plan",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionSourceRootPath",
                table: "Plan");

            migrationBuilder.DropColumn(
                name: "ExecutionTargetRootPath",
                table: "Plan");
        }
    }
}
