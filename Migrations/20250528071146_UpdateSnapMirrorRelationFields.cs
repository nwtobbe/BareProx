using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BareProx.Migrations
{
    /// <inheritdoc />
    public partial class UpdateSnapMirrorRelationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SnapMirrorRelations_NetappControllers_DestinationControllerId",
                table: "SnapMirrorRelations");

            migrationBuilder.DropForeignKey(
                name: "FK_SnapMirrorRelations_NetappControllers_SourceControllerId",
                table: "SnapMirrorRelations");

            migrationBuilder.DropIndex(
                name: "IX_SnapMirrorRelations_DestinationControllerId",
                table: "SnapMirrorRelations");

            migrationBuilder.DropIndex(
                name: "IX_SnapMirrorRelations_SourceControllerId",
                table: "SnapMirrorRelations");

            migrationBuilder.AddColumn<bool>(
                name: "IsHealthy",
                table: "SnapMirrorRelations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LagTime",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapMirrorPolicy",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "SnapMirrorRelations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsHealthy",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "LagTime",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "SnapMirrorPolicy",
                table: "SnapMirrorRelations");

            migrationBuilder.DropColumn(
                name: "State",
                table: "SnapMirrorRelations");

            migrationBuilder.CreateIndex(
                name: "IX_SnapMirrorRelations_DestinationControllerId",
                table: "SnapMirrorRelations",
                column: "DestinationControllerId");

            migrationBuilder.CreateIndex(
                name: "IX_SnapMirrorRelations_SourceControllerId",
                table: "SnapMirrorRelations",
                column: "SourceControllerId");

            migrationBuilder.AddForeignKey(
                name: "FK_SnapMirrorRelations_NetappControllers_DestinationControllerId",
                table: "SnapMirrorRelations",
                column: "DestinationControllerId",
                principalTable: "NetappControllers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SnapMirrorRelations_NetappControllers_SourceControllerId",
                table: "SnapMirrorRelations",
                column: "SourceControllerId",
                principalTable: "NetappControllers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
