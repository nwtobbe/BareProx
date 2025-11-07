using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class addtokens : Migration
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
                name: "ApiTokenSecretEnc",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseApiToken",
                table: "ProxmoxClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiTokenId",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "ApiTokenSecretEnc",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "UseApiToken",
                table: "ProxmoxClusters");
        }
    }
}
