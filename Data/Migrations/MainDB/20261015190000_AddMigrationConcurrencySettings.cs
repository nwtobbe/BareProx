using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace BareProx.Migrations
{
    /// <summary>Per-cluster migration concurrency limits; defaults protect existing installs.</summary>
    [DbContext(typeof(BareProx.Data.ApplicationDbContext))]
    [Migration("20261015190000_AddMigrationConcurrencySettings")]
    public partial class AddMigrationConcurrencySettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "MaxParallelMigrations", table: "MigrationSelections",
                nullable: false, defaultValue: 4);
            migrationBuilder.AddColumn<int>(name: "MaxParallelCutovers", table: "MigrationSelections",
                nullable: false, defaultValue: 1);
        }
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MaxParallelMigrations", table: "MigrationSelections");
            migrationBuilder.DropColumn(name: "MaxParallelCutovers", table: "MigrationSelections");
        }
    }
}
