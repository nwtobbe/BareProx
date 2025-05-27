using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class db_update : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackupRecords_BackupSchedules_ScheduleId",
                table: "BackupRecords");

            migrationBuilder.DropIndex(
                name: "IX_BackupRecords_ScheduleId",
                table: "BackupRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
    }
}
