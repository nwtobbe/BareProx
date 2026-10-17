using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Data.Migrations.MainDB
{
    /// <summary>
    /// Persist the source VM name independently of MigrationQueueItems so reports
    /// survive a queue clear. Backfill names for historical rows where an item
    /// still exists. Do not create, drop or modify unrelated tables.
    /// </summary>
    public partial class AddMigrationLogServerName : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ServerName",
                table: "MigrationQueueLogs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "MigrationQueueLogs"
                SET "ServerName" = (
                    SELECT q."Name"
                    FROM "MigrationQueueItems" AS q
                    WHERE q."Id" = "MigrationQueueLogs"."ItemId"
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM "MigrationQueueItems" AS q
                    WHERE q."Id" = "MigrationQueueLogs"."ItemId"
                      AND q."Name" IS NOT NULL
                      AND TRIM(q."Name") <> ''
                );
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServerName",
                table: "MigrationQueueLogs");
        }
    }
}
