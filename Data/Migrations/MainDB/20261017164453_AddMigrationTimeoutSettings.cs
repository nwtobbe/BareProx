using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.MainDB
{
    /// <inheritdoc />
    public partial class AddMigrationTimeoutSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuestAgentStartupTimeoutMinutes",
                table: "MigrationSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GuestPrepTimeoutMinutes",
                table: "MigrationSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GuestVerificationTimeoutMinutes",
                table: "MigrationSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "VmwareShutdownTimeoutMinutes",
                table: "MigrationSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GuestAgentStartupTimeoutMinutes",
                table: "MigrationSelections");

            migrationBuilder.DropColumn(
                name: "GuestPrepTimeoutMinutes",
                table: "MigrationSelections");

            migrationBuilder.DropColumn(
                name: "GuestVerificationTimeoutMinutes",
                table: "MigrationSelections");

            migrationBuilder.DropColumn(
                name: "VmwareShutdownTimeoutMinutes",
                table: "MigrationSelections");
        }
    }
}
