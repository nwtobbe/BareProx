using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotificationEmails",
                table: "BackupSchedules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnError",
                table: "BackupSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnSuccess",
                table: "BackupSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationEmails",
                table: "BackupSchedules");

            migrationBuilder.DropColumn(
                name: "NotifyOnError",
                table: "BackupSchedules");

            migrationBuilder.DropColumn(
                name: "NotifyOnSuccess",
                table: "BackupSchedules");
        }
    }
}
