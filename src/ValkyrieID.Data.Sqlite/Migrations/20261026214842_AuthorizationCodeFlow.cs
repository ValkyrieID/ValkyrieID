using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ValkyrieID.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizationCodeFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedCorsOrigins",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "IdTokenLifetime",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostLogoutRedirectUris",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "RedirectUris",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "RequirePkce",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedCorsOrigins",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "IdTokenLifetime",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "PostLogoutRedirectUris",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "RedirectUris",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "RequirePkce",
                table: "Clients");
        }
    }
}
