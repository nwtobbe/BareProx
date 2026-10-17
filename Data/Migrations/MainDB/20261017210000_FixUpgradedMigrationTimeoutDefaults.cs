using BareProx.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.MainDB
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261017210000_FixUpgradedMigrationTimeoutDefaults")]
    public partial class FixUpgradedMigrationTimeoutDefaults : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The original AddMigrationTimeoutSettings migration populated existing
            // MigrationSelections rows with 0. Correct only invalid zero values;
            // preserve all valid user-configured values.
            migrationBuilder.Sql(@"
UPDATE MigrationSelections
SET GuestPrepTimeoutMinutes = 35
WHERE GuestPrepTimeoutMinutes = 0;

UPDATE MigrationSelections
SET VmwareShutdownTimeoutMinutes = 15
WHERE VmwareShutdownTimeoutMinutes = 0;

UPDATE MigrationSelections
SET GuestAgentStartupTimeoutMinutes = 20
WHERE GuestAgentStartupTimeoutMinutes = 0;

UPDATE MigrationSelections
SET GuestVerificationTimeoutMinutes = 20
WHERE GuestVerificationTimeoutMinutes = 0;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately no-op. Restoring zeroes would corrupt valid timeout settings.
        }
    }
}
