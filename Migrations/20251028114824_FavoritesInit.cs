using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NextStakeWebApp.Migrations
{
    /// <inheritdoc />
    public partial class FavoritesInit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FavoriteMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    MatchId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteMatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteMatches_UserId_MatchId",
                table: "FavoriteMatches",
                columns: new[] { "UserId", "MatchId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FavoriteMatches");
        }
    }
}
