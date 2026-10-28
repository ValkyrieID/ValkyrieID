using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ValkyrieID.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class BackChannelLogout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackChannelLogoutUri",
                table: "Clients",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackChannelLogoutDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackChannelLogoutDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackChannelLogoutDeliveries_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "ClientId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSessionClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessionClients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessionClients_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "ClientId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSessionClients_UserSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UserSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_SessionId",
                table: "RefreshTokens",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_BackChannelLogoutDeliveries_ClientId",
                table: "BackChannelLogoutDeliveries",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_BackChannelLogoutDeliveries_NextAttemptAt",
                table: "BackChannelLogoutDeliveries",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessionClients_ClientId",
                table: "UserSessionClients",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessionClients_SessionId_ClientId",
                table: "UserSessionClients",
                columns: new[] { "SessionId", "ClientId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackChannelLogoutDeliveries");

            migrationBuilder.DropTable(
                name: "UserSessionClients");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_SessionId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "BackChannelLogoutUri",
                table: "Clients");
        }
    }
}
