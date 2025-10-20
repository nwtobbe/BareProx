using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class Addapitokentohosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CsrfEnc",
                table: "ProxmoxHosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketEnc",
                table: "ProxmoxHosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TicketIssuedUtc",
                table: "ProxmoxHosts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CsrfEnc",
                table: "ProxmoxHosts");

            migrationBuilder.DropColumn(
                name: "TicketEnc",
                table: "ProxmoxHosts");

            migrationBuilder.DropColumn(
                name: "TicketIssuedUtc",
                table: "ProxmoxHosts");
        }
    }
}
