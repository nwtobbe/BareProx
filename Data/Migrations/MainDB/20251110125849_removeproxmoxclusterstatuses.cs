using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class removeproxmoxclusterstatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastChecked",
                table: "ProxmoxClusters");

            migrationBuilder.DropColumn(
                name: "LastStatus",
                table: "ProxmoxClusters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastChecked",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStatus",
                table: "ProxmoxClusters",
                type: "TEXT",
                nullable: true);
        }
    }
}
