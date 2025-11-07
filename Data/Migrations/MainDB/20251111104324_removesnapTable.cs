using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class removesnapTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetappSnapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetappSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExistsOnPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExistsOnSecondary = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsReplicated = table.Column<bool>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastChecked = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PrimaryControllerId = table.Column<int>(type: "INTEGER", nullable: false),
                    PrimaryVolume = table.Column<string>(type: "TEXT", nullable: false),
                    SecondaryControllerId = table.Column<int>(type: "INTEGER", nullable: true),
                    SecondaryVolume = table.Column<string>(type: "TEXT", nullable: true),
                    SnapmirrorLabel = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetappSnapshots", x => x.Id);
                });
        }
    }
}
