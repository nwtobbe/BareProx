using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddMoreVolumeOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExportPolicyName",
                table: "SelectedNetappVolumes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SnapshotLockingEnabled",
                table: "SelectedNetappVolumes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SpaceAvailable",
                table: "SelectedNetappVolumes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SpaceSize",
                table: "SelectedNetappVolumes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SpaceUsed",
                table: "SelectedNetappVolumes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExportPolicyName",
                table: "SelectedNetappVolumes");

            migrationBuilder.DropColumn(
                name: "SnapshotLockingEnabled",
                table: "SelectedNetappVolumes");

            migrationBuilder.DropColumn(
                name: "SpaceAvailable",
                table: "SelectedNetappVolumes");

            migrationBuilder.DropColumn(
                name: "SpaceSize",
                table: "SelectedNetappVolumes");

            migrationBuilder.DropColumn(
                name: "SpaceUsed",
                table: "SelectedNetappVolumes");
        }
    }
}
