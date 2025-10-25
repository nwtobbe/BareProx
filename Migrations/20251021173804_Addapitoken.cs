using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class Addapitoken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiTokenId",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiTokenSecret",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiTokenId",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "ApiTokenSecret",
                table: "ProxmoxClusters");
        }
    }
}
