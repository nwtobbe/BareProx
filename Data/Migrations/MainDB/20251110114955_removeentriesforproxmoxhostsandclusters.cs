using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class removeentriesforproxmoxhostsandclusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastStatusMessage",
                table: "ProxmoxHosts");

            migrationBuilder.DropColumn(
                name: "HasQuorum",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "LastStatusMessage",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "OnlineHostCount",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "TotalHostCount",
                table: "ProxmoxClusters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastStatusMessage",
                table: "ProxmoxHosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasQuorum",
                table: "ProxmoxClusters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStatusMessage",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OnlineHostCount",
                table: "ProxmoxClusters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalHostCount",
                table: "ProxmoxClusters",
                type: "INTEGER",
                nullable: true);
        }
    }
}
