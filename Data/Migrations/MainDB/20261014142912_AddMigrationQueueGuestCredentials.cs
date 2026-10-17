using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.MainDB
{
    /// <inheritdoc />
    public partial class AddMigrationQueueGuestCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedGuestPassword",
                table: "MigrationQueueItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GuestUsername",
                table: "MigrationQueueItems",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedGuestPassword",
                table: "MigrationQueueItems");

            migrationBuilder.DropColumn(
                name: "GuestUsername",
                table: "MigrationQueueItems");
        }
    }
}
