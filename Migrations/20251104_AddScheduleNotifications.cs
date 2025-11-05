using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

public partial class AddScheduleNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Works on SQL Server and SQLite
        migrationBuilder.AddColumn<bool>(
            name: "NotifyOnSuccess",
            table: "BackupSchedules",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "NotifyOnError",
            table: "BackupSchedules",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "NotificationEmails",
            table: "BackupSchedules",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "NotifyOnSuccess",
            table: "BackupSchedules");

        migrationBuilder.DropColumn(
            name: "NotifyOnError",
            table: "BackupSchedules");

        migrationBuilder.DropColumn(
            name: "NotificationEmails",
            table: "BackupSchedules");
    }
}
