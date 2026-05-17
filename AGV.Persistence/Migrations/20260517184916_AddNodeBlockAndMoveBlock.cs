using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AGV.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeBlockAndMoveBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MoveBlock",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MoveId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockReason = table.Column<byte>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsEngineerBlock = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoveBlock", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeBlock",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockReason = table.Column<byte>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsEngineerBlock = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeBlock", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MoveBlock_IsEngineerBlock",
                table: "MoveBlock",
                column: "IsEngineerBlock");

            migrationBuilder.CreateIndex(
                name: "IX_MoveBlock_MoveId",
                table: "MoveBlock",
                column: "MoveId");

            migrationBuilder.CreateIndex(
                name: "IX_NodeBlock_IsEngineerBlock",
                table: "NodeBlock",
                column: "IsEngineerBlock");

            migrationBuilder.CreateIndex(
                name: "IX_NodeBlock_NodeId",
                table: "NodeBlock",
                column: "NodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MoveBlock");

            migrationBuilder.DropTable(
                name: "NodeBlock");
        }
    }
}
