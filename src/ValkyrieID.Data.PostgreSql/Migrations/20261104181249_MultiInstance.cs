using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ValkyrieID.Data.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class MultiInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Leases",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => x.Name);
                });

            migrationBuilder.InsertData(
                table: "Leases",
                columns: new[] { "Name", "ExpiresAt", "Owner" },
                values: new object[] { "signing-keys", null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Leases");
        }
    }
}
