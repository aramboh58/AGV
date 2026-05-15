using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AGV.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoadmapVersion",
                columns: table => new
                {
                    VersionId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VersionLabel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadmapVersion", x => x.VersionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoadmapVersion_IsActive",
                table: "RoadmapVersion",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoadmapVersion");
        }
    }
}
