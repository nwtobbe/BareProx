using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.QueryDb
{
    /// <inheritdoc />
    public partial class AddsnapTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetappSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotName = table.Column<string>(type: "TEXT", nullable: false),
                    PrimaryVolume = table.Column<string>(type: "TEXT", nullable: false),
                    SecondaryVolume = table.Column<string>(type: "TEXT", nullable: true),
                    PrimaryControllerId = table.Column<int>(type: "INTEGER", nullable: false),
                    SecondaryControllerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExistsOnPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExistsOnSecondary = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SnapmirrorLabel = table.Column<string>(type: "TEXT", nullable: false),
                    IsReplicated = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastChecked = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetappSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetappSnapshots_JobId_SnapshotName",
                table: "NetappSnapshots",
                columns: new[] { "JobId", "SnapshotName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetappSnapshots_PrimaryControllerId",
                table: "NetappSnapshots",
                column: "PrimaryControllerId");

            migrationBuilder.CreateIndex(
                name: "IX_NetappSnapshots_SecondaryControllerId",
                table: "NetappSnapshots",
                column: "SecondaryControllerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetappSnapshots");
        }
    }
}
