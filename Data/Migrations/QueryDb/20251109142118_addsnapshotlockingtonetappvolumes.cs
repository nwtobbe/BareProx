using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.QueryDb
{
    /// <inheritdoc />
    public partial class addsnapshotlockingtonetappvolumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SnapshotLockingEnabled",
                table: "InventoryNetappVolumes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapshotLockingEnabled",
                table: "InventoryNetappVolumes");
        }
    }
}
