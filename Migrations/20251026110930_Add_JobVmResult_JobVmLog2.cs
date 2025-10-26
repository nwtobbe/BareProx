using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class Add_JobVmResult_JobVmLog2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobVmResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    VMID = table.Column<int>(type: "INTEGER", nullable: false),
                    VmName = table.Column<string>(type: "TEXT", nullable: false),
                    HostName = table.Column<string>(type: "TEXT", nullable: false),
                    StorageName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    WasRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    IoFreezeAttempted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IoFreezeSucceeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SnapshotRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    SnapshotTaken = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProxmoxSnapshotName = table.Column<string>(type: "TEXT", nullable: true),
                    SnapshotUpid = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BackupRecordId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobVmResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobVmResults_BackupRecords_BackupRecordId",
                        column: x => x.BackupRecordId,
                        principalTable: "BackupRecords",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobVmResults_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobVmLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobVmResultId = table.Column<int>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobVmLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobVmLogs_JobVmResults_JobVmResultId",
                        column: x => x.JobVmResultId,
                        principalTable: "JobVmResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobVmLogs_JobVmResultId",
                table: "JobVmLogs",
                column: "JobVmResultId");

            migrationBuilder.CreateIndex(
                name: "IX_JobVmResults_BackupRecordId",
                table: "JobVmResults",
                column: "BackupRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_JobVmResults_JobId",
                table: "JobVmResults",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobVmLogs");

            migrationBuilder.DropTable(
                name: "JobVmResults");
        }
    }
}
