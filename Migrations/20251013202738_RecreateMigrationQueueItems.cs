using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class RecreateMigrationQueueItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigratonQueueItem");

            migrationBuilder.CreateTable(
                name: "MigrationQueueItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VmId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CpuType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OsType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PrepareVirtio = table.Column<bool>(type: "INTEGER", nullable: false),
                    MountVirtioIso = table.Column<bool>(type: "INTEGER", nullable: false),
                    VirtioIsoName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ScsiController = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VmxPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Uuid = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Uefi = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisksJson = table.Column<string>(type: "TEXT", nullable: false),
                    NicsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationQueueItems", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationQueueItems");

            migrationBuilder.CreateTable(
                name: "MigratonQueueItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CpuType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DisksJson = table.Column<string>(type: "TEXT", nullable: false),
                    MountVirtioIso = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    NicsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OsType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PrepareVirtio = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScsiController = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Uefi = table.Column<bool>(type: "INTEGER", nullable: false),
                    Uuid = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VirtioIsoName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    VmId = table.Column<int>(type: "INTEGER", nullable: true),
                    VmxPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigratonQueueItem", x => x.Id);
                });
        }
    }
}
