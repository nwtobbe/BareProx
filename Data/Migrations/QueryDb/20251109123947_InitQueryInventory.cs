using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.QueryDb
{
    /// <inheritdoc />
    public partial class InitQueryInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryMetadata",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMetadata", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "InventoryNetappVolumes",
                columns: table => new
                {
                    VolumeUuid = table.Column<string>(type: "TEXT", nullable: false),
                    NetappControllerId = table.Column<int>(type: "INTEGER", nullable: false),
                    SvmName = table.Column<string>(type: "TEXT", nullable: false),
                    VolumeName = table.Column<string>(type: "TEXT", nullable: false),
                    JunctionPath = table.Column<string>(type: "TEXT", nullable: true),
                    NfsIps = table.Column<string>(type: "TEXT", nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryNetappVolumes", x => x.VolumeUuid);
                });

            migrationBuilder.CreateTable(
                name: "InventoryStorages",
                columns: table => new
                {
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: false),
                    StorageId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    ContentFlags = table.Column<string>(type: "TEXT", nullable: false),
                    IsImageCapable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Server = table.Column<string>(type: "TEXT", nullable: true),
                    Export = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    Options = table.Column<string>(type: "TEXT", nullable: true),
                    Shared = table.Column<bool>(type: "INTEGER", nullable: false),
                    NetappVolumeUuid = table.Column<string>(type: "TEXT", nullable: true),
                    MatchQuality = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScanStatus = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryStorages", x => new { x.ClusterId, x.StorageId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryVms",
                columns: table => new
                {
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: false),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NodeName = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    IsTemplate = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryVms", x => new { x.ClusterId, x.VmId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryVolumeReplications",
                columns: table => new
                {
                    PrimaryVolumeUuid = table.Column<string>(type: "TEXT", nullable: false),
                    SecondaryVolumeUuid = table.Column<string>(type: "TEXT", nullable: false),
                    RelationshipType = table.Column<string>(type: "TEXT", nullable: true),
                    IsHealthy = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryVolumeReplications", x => new { x.PrimaryVolumeUuid, x.SecondaryVolumeUuid });
                    table.ForeignKey(
                        name: "FK_InventoryVolumeReplications_InventoryNetappVolumes_PrimaryVolumeUuid",
                        column: x => x.PrimaryVolumeUuid,
                        principalTable: "InventoryNetappVolumes",
                        principalColumn: "VolumeUuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryVolumeReplications_InventoryNetappVolumes_SecondaryVolumeUuid",
                        column: x => x.SecondaryVolumeUuid,
                        principalTable: "InventoryNetappVolumes",
                        principalColumn: "VolumeUuid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryVmDisks",
                columns: table => new
                {
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: false),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    StorageId = table.Column<string>(type: "TEXT", nullable: false),
                    VolId = table.Column<string>(type: "TEXT", nullable: false),
                    NodeName = table.Column<string>(type: "TEXT", nullable: false),
                    IsBootDisk = table.Column<bool>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryVmDisks", x => new { x.ClusterId, x.VmId, x.StorageId, x.VolId });
                    table.ForeignKey(
                        name: "FK_InventoryVmDisks_InventoryStorages_ClusterId_StorageId",
                        columns: x => new { x.ClusterId, x.StorageId },
                        principalTable: "InventoryStorages",
                        principalColumns: new[] { "ClusterId", "StorageId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryVmDisks_InventoryVms_ClusterId_VmId",
                        columns: x => new { x.ClusterId, x.VmId },
                        principalTable: "InventoryVms",
                        principalColumns: new[] { "ClusterId", "VmId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryNetappVolumes_JunctionPath",
                table: "InventoryNetappVolumes",
                column: "JunctionPath");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryNetappVolumes_NetappControllerId",
                table: "InventoryNetappVolumes",
                column: "NetappControllerId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryStorages_NetappVolumeUuid",
                table: "InventoryStorages",
                column: "NetappVolumeUuid");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryVmDisks_ClusterId_StorageId",
                table: "InventoryVmDisks",
                columns: new[] { "ClusterId", "StorageId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryVmDisks_ClusterId_VmId",
                table: "InventoryVmDisks",
                columns: new[] { "ClusterId", "VmId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryVms_ClusterId_NodeName",
                table: "InventoryVms",
                columns: new[] { "ClusterId", "NodeName" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryVolumeReplications_SecondaryVolumeUuid",
                table: "InventoryVolumeReplications",
                column: "SecondaryVolumeUuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryMetadata");

            migrationBuilder.DropTable(
                name: "InventoryVmDisks");

            migrationBuilder.DropTable(
                name: "InventoryVolumeReplications");

            migrationBuilder.DropTable(
                name: "InventoryStorages");

            migrationBuilder.DropTable(
                name: "InventoryVms");

            migrationBuilder.DropTable(
                name: "InventoryNetappVolumes");
        }
    }
}
