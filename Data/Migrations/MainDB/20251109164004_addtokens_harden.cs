using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class addtokens_harden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApiTokenExpiresUtc",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApiTokenLifetimeDays",
                table: "ProxmoxClusters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApiTokenRenewBeforeMinutes",
                table: "ProxmoxClusters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiTokenExpiresUtc",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "ApiTokenLifetimeDays",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "ApiTokenRenewBeforeMinutes",
                table: "ProxmoxClusters");
        }
    }
}
