using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapMirrorDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackoffLevel",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationClusterName",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationSvmName",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExportedSnapshot",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTransferCompressionRatio",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTransferDuration",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTransferEndTime",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTransferState",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTransferType",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyType",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyUuid",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceClusterName",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSvmName",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalTransferBytes",
                table: "SnapMirrorRelations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotalTransferDuration",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackoffLevel",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "DestinationClusterName",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "DestinationSvmName",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "ExportedSnapshot",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "LastTransferCompressionRatio",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "LastTransferDuration",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "LastTransferEndTime",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "LastTransferState",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "LastTransferType",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "PolicyType",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "PolicyUuid",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "SourceClusterName",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "SourceSvmName",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "TotalTransferBytes",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "TotalTransferDuration",
                table: "SnapMirrorRelations");
        }
    }
}
