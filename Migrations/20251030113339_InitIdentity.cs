using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NextStakeWebApp.Migrations
{
    /// <inheritdoc />
    public partial class InitIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Assicura lo schema "public"
            migrationBuilder.EnsureSchema(name: "public");

            // I rename in "public" sono innocui anche se già nello schema
            migrationBuilder.RenameTable(name: "FavoriteMatches", newName: "FavoriteMatches", newSchema: "public");
            migrationBuilder.RenameTable(name: "AspNetUserTokens", newName: "AspNetUserTokens", newSchema: "public");
            migrationBuilder.RenameTable(name: "AspNetUsers", newName: "AspNetUsers", newSchema: "public");
            migrationBuilder.RenameTable(name: "AspNetUserRoles", newName: "AspNetUserRoles", newSchema: "public");
            migrationBuilder.RenameTable(name: "AspNetUserLogins", newName: "AspNetUserLogins", newSchema: "public");
            migrationBuilder.RenameTable(name: "AspNetUserClaims", newName: "AspNetUserClaims", newSchema: "public");
            migrationBuilder.RenameTable(name: "AspNetRoles", newName: "AspNetRoles", newSchema: "public");
            migrationBuilder.RenameTable(name: "AspNetRoleClaims", newName: "AspNetRoleClaims", newSchema: "public");

            // Conversioni tipo data/ora (sono no-op se già impostate così)
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                schema: "public",
                table: "FavoriteMatches",
                type: "timestamp without time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "PlanExpiresAtUtc",
                schema: "public",
                table: "AspNetUsers",
                type: "timestamp without time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                schema: "public",
                table: "AspNetUsers",
                type: "timestamp without time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            // ✅ Aggiungi DisplayName SOLO se non esiste già
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name   = 'AspNetUsers'
          AND column_name  = 'DisplayName'
    ) THEN
        ALTER TABLE ""public"".""AspNetUsers""
        ADD COLUMN ""DisplayName"" text NULL;
    END IF;
END $$;
");

            // ❌ NIENTE creazione tabella 'analyses':
            // è gestita come keyless in ReadDbContext con ExcludeFromMigrations().
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ❌ Non droppiamo 'analyses' (non è gestita da EF)

            // ✅ Droppa DisplayName SOLO se esiste
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name   = 'AspNetUsers'
          AND column_name  = 'DisplayName'
    ) THEN
        ALTER TABLE ""public"".""AspNetUsers""
        DROP COLUMN ""DisplayName"";
    END IF;
END $$;
");

            // I rename di ritorno (rimangono no-op nella pratica)
            migrationBuilder.RenameTable(name: "FavoriteMatches", schema: "public", newName: "FavoriteMatches");
            migrationBuilder.RenameTable(name: "AspNetUserTokens", schema: "public", newName: "AspNetUserTokens");
            migrationBuilder.RenameTable(name: "AspNetUsers", schema: "public", newName: "AspNetUsers");
            migrationBuilder.RenameTable(name: "AspNetUserRoles", schema: "public", newName: "AspNetUserRoles");
            migrationBuilder.RenameTable(name: "AspNetUserLogins", schema: "public", newName: "AspNetUserLogins");
            migrationBuilder.RenameTable(name: "AspNetUserClaims", schema: "public", newName: "AspNetUserClaims");
            migrationBuilder.RenameTable(name: "AspNetRoles", schema: "public", newName: "AspNetRoles");
            migrationBuilder.RenameTable(name: "AspNetRoleClaims", schema: "public", newName: "AspNetRoleClaims");

            // Ripristino tipi data/ora (se serve)
            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "FavoriteMatches",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "PlanExpiresAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone");
        }
    }
}
