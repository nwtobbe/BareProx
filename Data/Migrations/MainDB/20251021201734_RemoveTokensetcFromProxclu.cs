using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTokensetcFromProxclu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiToken",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "ApiTokenId",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "ApiTokenSecret",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "CsrfToken",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "TokenExpiry",
                table: "ProxmoxClusters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiToken",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);

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

            migrationBuilder.AddColumn<string>(
                name: "CsrfToken",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TokenExpiry",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);
        }
    }
}
