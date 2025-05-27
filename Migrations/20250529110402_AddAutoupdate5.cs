using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoupdate5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "State",
                table: "SnapMirrorRelations",
                newName: "state");

            migrationBuilder.RenameColumn(
                name: "LagTime",
                table: "SnapMirrorRelations",
                newName: "lag_time");

            migrationBuilder.RenameColumn(
                name: "IsHealthy",
                table: "SnapMirrorRelations",
                newName: "healthy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "state",
                table: "SnapMirrorRelations",
                newName: "State");

            migrationBuilder.RenameColumn(
                name: "lag_time",
                table: "SnapMirrorRelations",
                newName: "LagTime");

            migrationBuilder.RenameColumn(
                name: "healthy",
                table: "SnapMirrorRelations",
                newName: "IsHealthy");
        }
    }
}
