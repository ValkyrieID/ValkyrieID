using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ValkyrieID.Data.Sqlite.Migrations
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
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
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
