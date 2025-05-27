using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class CreateSnapshotStatus2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VolumeName",
                table: "NetappSnapshots",
                newName: "PrimaryVolume");

            migrationBuilder.AddColumn<bool>(
                name: "ExistsOnPrimary",
                table: "NetappSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExistsOnSecondary",
                table: "NetappSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "NetappSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChecked",
                table: "NetappSnapshots",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "PrimaryControllerId",
                table: "NetappSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SecondaryControllerId",
                table: "NetappSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondaryVolume",
                table: "NetappSnapshots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExistsOnPrimary",
                table: "NetappSnapshots");

            migrationBuilder.DropColumn(
                name: "ExistsOnSecondary",
                table: "NetappSnapshots");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "NetappSnapshots");

            migrationBuilder.DropColumn(
                name: "LastChecked",
                table: "NetappSnapshots");

            migrationBuilder.DropColumn(
                name: "PrimaryControllerId",
                table: "NetappSnapshots");

            migrationBuilder.DropColumn(
                name: "SecondaryControllerId",
                table: "NetappSnapshots");

            migrationBuilder.DropColumn(
                name: "SecondaryVolume",
                table: "NetappSnapshots");

            migrationBuilder.RenameColumn(
                name: "PrimaryVolume",
                table: "NetappSnapshots",
                newName: "VolumeName");
        }
    }
}
