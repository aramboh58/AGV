using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AGV.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAreaNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AreaNode",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFromVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AreaNode", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AreaNode_NodeId",
                table: "AreaNode",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "UX_AreaNode_Area_Node_Version",
                table: "AreaNode",
                columns: new[] { "AreaId", "NodeId", "EffectiveFromVersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AreaNode");
        }
    }
}
