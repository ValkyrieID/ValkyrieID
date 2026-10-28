using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ValkyrieID.Data.PostgreSql.Migrations
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
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackChannelLogoutDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
