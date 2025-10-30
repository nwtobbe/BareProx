using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class FixEmailSettingsSeed_NoDynamicValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "EmailSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2025, 10, 29, 11, 38, 41, 922, DateTimeKind.Utc).AddTicks(2416));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "EmailSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2025, 10, 29, 11, 36, 5, 80, DateTimeKind.Utc).AddTicks(8188));
        }
    }
}
