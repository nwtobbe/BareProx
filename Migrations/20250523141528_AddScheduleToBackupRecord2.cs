using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleToBackupRecord2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Frequency",
                table: "BackupSchedules",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableIoFreeze",
                table: "BackupRecords",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsApplicationAware",
                table: "BackupRecords",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleId",
                table: "BackupRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseProxmoxSnapshot",
                table: "BackupRecords",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WithMemory",
                table: "BackupRecords",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_BackupRecords_ScheduleId",
                table: "BackupRecords",
                column: "ScheduleId");

            migrationBuilder.AddForeignKey(
                name: "FK_BackupRecords_BackupSchedules_ScheduleId",
                table: "BackupRecords",
                column: "ScheduleId",
                principalTable: "BackupSchedules",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackupRecords_BackupSchedules_ScheduleId",
                table: "BackupRecords");

            migrationBuilder.DropIndex(
                name: "IX_BackupRecords_ScheduleId",
                table: "BackupRecords");

            migrationBuilder.DropColumn(
                name: "EnableIoFreeze",
                table: "BackupRecords");

            migrationBuilder.DropColumn(
                name: "IsApplicationAware",
                table: "BackupRecords");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "BackupRecords");

            migrationBuilder.DropColumn(
                name: "UseProxmoxSnapshot",
                table: "BackupRecords");

            migrationBuilder.DropColumn(
                name: "WithMemory",
                table: "BackupRecords");

            migrationBuilder.AlterColumn<int>(
                name: "Frequency",
                table: "BackupSchedules",
                type: "int",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
