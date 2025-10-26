using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class FixEmailSettingsSeed_NoDynamicValues2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "EmailSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2025, 10, 29, 11, 39, 0, 0, DateTimeKind.Utc).AddTicks(8732));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "EmailSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2025, 10, 29, 11, 38, 41, 922, DateTimeKind.Utc).AddTicks(2416));
        }
    }
}
