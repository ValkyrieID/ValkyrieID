using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ValkyrieID.Data.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizationCodeFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "AllowedCorsOrigins",
                table: "Clients",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>());

            migrationBuilder.AddColumn<TimeSpan>(
                name: "IdTokenLifetime",
                table: "Clients",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "PostLogoutRedirectUris",
                table: "Clients",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>());

            migrationBuilder.AddColumn<List<string>>(
                name: "RedirectUris",
                table: "Clients",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>());

            migrationBuilder.AddColumn<bool>(
                name: "RequirePkce",
                table: "Clients",
                type: "boolean",
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
