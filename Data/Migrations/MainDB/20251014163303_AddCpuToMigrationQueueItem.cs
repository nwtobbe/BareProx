using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddCpuToMigrationQueueItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MemoryGiB",
                table: "MigrationQueueItems",
                newName: "Sockets");

            migrationBuilder.AddColumn<int>(
                name: "Cores",
                table: "MigrationQueueItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cores",
                table: "MigrationQueueItems");

            migrationBuilder.RenameColumn(
                name: "Sockets",
                table: "MigrationQueueItems",
                newName: "MemoryGiB");
        }
    }
}
