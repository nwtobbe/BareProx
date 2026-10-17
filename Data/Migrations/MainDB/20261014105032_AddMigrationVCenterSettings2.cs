using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.MainDB
{
    /// <inheritdoc />
    public partial class AddMigrationVCenterSettings2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationVCenterSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Server = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    EncryptedPassword = table.Column<string>(type: "TEXT", nullable: false),
                    AllowUntrustedCertificate = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationVCenterSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationVCenterSettings");
        }
    }
}
