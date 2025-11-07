using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupScheduleLocking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableLocking",
                table: "BackupSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LockRetentionCount",
                table: "BackupSchedules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockRetentionUnit",
                table: "BackupSchedules",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableLocking",
                table: "BackupSchedules");

            migrationBuilder.DropColumn(
                name: "LockRetentionCount",
                table: "BackupSchedules");

            migrationBuilder.DropColumn(
                name: "LockRetentionUnit",
                table: "BackupSchedules");
        }
    }
}
