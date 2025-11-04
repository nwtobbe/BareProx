using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedNetappVolumeIdToBackupSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SelectedNetappVolumeId",
                table: "BackupSchedules",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedNetappVolumeId",
                table: "BackupSchedules");
        }
    }
}
