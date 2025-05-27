using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxClusterAndHostHealthColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOnline",
                table: "ProxmoxHosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChecked",
                table: "ProxmoxHosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStatus",
                table: "ProxmoxHosts",
                type: "TEXT",
                nullable: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsOnline",
                table: "ProxmoxHosts");

            migrationBuilder.DropColumn(
                name: "LastChecked",
                table: "ProxmoxHosts");

            migrationBuilder.DropColumn(
                name: "LastStatus",
                table: "ProxmoxHosts");

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
    }
}
