using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupNormalizer.Core.Migrations
{
    /// <inheritdoc />
    public partial class RecordLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SkipReason",
                table: "PlanOperation",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryKind",
                table: "FileEntry",
                type: "TEXT",
                nullable: false,
                defaultValue: "File");

            migrationBuilder.AddColumn<string>(
                name: "LinkNote",
                table: "FileEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkTarget",
                table: "FileEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPath",
                table: "FileEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE FileHash SET State = 'Stale'
                WHERE FileEntryId IN (SELECT Id FROM FileEntry WHERE Status = 'UnsupportedEntry' AND Error = 'symlink');
                UPDATE FileEntry SET EntryKind = 'ReparsePoint', Status = 'Ok', Error = NULL,
                    LinkNote = 'Rescan to record link metadata.'
                WHERE Status = 'UnsupportedEntry' AND Error = 'symlink';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipReason",
                table: "PlanOperation");

            migrationBuilder.DropColumn(
                name: "EntryKind",
                table: "FileEntry");

            migrationBuilder.DropColumn(
                name: "LinkNote",
                table: "FileEntry");

            migrationBuilder.DropColumn(
                name: "LinkTarget",
                table: "FileEntry");

            migrationBuilder.DropColumn(
                name: "TargetPath",
                table: "FileEntry");
        }
    }
}
