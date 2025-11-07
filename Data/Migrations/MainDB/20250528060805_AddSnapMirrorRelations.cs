using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapMirrorRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SnapMirrorRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceControllerId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceVolume = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationControllerId = table.Column<int>(type: "INTEGER", nullable: false),
                    DestinationVolume = table.Column<string>(type: "TEXT", nullable: false),
                    RelationshipType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapMirrorRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnapMirrorRelations_NetappControllers_DestinationControllerId",
                        column: x => x.DestinationControllerId,
                        principalTable: "NetappControllers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SnapMirrorRelations_NetappControllers_SourceControllerId",
                        column: x => x.SourceControllerId,
                        principalTable: "NetappControllers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SnapMirrorRelations_DestinationControllerId",
                table: "SnapMirrorRelations",
                column: "DestinationControllerId");

            migrationBuilder.CreateIndex(
                name: "IX_SnapMirrorRelations_SourceControllerId",
                table: "SnapMirrorRelations",
                column: "SourceControllerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SnapMirrorRelations");
        }
    }
}
