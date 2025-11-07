using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapMirrorPolicyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SnapMirrorPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Uuid = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    NetworkCompressionEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Throttle = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapMirrorPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapMirrorPolicyRetentions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SnapMirrorPolicyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Preserve = table.Column<bool>(type: "INTEGER", nullable: false),
                    Warn = table.Column<int>(type: "INTEGER", nullable: false),
                    Period = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapMirrorPolicyRetentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnapMirrorPolicyRetentions_SnapMirrorPolicies_SnapMirrorPolicyId",
                        column: x => x.SnapMirrorPolicyId,
                        principalTable: "SnapMirrorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SnapMirrorPolicies_Uuid",
                table: "SnapMirrorPolicies",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapMirrorPolicyRetentions_SnapMirrorPolicyId",
                table: "SnapMirrorPolicyRetentions",
                column: "SnapMirrorPolicyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SnapMirrorPolicyRetentions");

            migrationBuilder.DropTable(
                name: "SnapMirrorPolicies");
        }
    }
}
