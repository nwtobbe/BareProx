using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmtpHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SecurityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "TEXT", nullable: true),
                    From = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DefaultRecipients = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    OnBackupSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnBackupFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnRestoreSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnRestoreFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnWarnings = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinSeverity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "EmailSettings",
                columns: new[] { "Id", "DefaultRecipients", "Enabled", "From", "MinSeverity", "OnBackupFailure", "OnBackupSuccess", "OnRestoreFailure", "OnRestoreSuccess", "OnWarnings", "ProtectedPassword", "SecurityMode", "SmtpHost", "SmtpPort", "UpdatedUtc", "Username" },
                values: new object[] { 1, null, false, null, "Info", true, false, true, false, true, null, "StartTls", null, 587, new DateTime(2025, 10, 29, 11, 36, 5, 80, DateTimeKind.Utc).AddTicks(8188), null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailSettings");
        }
    }
}
