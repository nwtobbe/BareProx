using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.QueryDb
{
    /// <inheritdoc />
    public partial class AddClusterHostStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryClusterStatuses",
                columns: table => new
                {
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClusterName = table.Column<string>(type: "TEXT", nullable: true),
                    HasQuorum = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnlineHostCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalHostCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastStatusMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryClusterStatuses", x => x.ClusterId);
                });

            migrationBuilder.CreateTable(
                name: "InventoryHostStatuses",
                columns: table => new
                {
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: false),
                    HostId = table.Column<int>(type: "INTEGER", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", nullable: false),
                    HostAddress = table.Column<string>(type: "TEXT", nullable: false),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastStatusMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryHostStatuses", x => new { x.ClusterId, x.HostId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryHostStatuses_ClusterId",
                table: "InventoryHostStatuses",
                column: "ClusterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryClusterStatuses");

            migrationBuilder.DropTable(
                name: "InventoryHostStatuses");
        }
    }
}
