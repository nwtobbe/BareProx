using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AutoUpdateDb5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ControllerId",
                table: "NetappSnapshots");

            migrationBuilder.DropColumn(
                name: "ControllerRole",
                table: "NetappSnapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ControllerId",
                table: "NetappSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ControllerRole",
                table: "NetappSnapshots",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
