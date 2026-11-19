using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountCreatedAndSessionClient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "AuthSessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "AuthSessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" u SET "CreatedUtc" = LEAST(
                    (SELECT min(h."CreatedAt") FROM "Habits" h WHERE h."UserId" = u."Id"),
                    (SELECT min(s."CreatedUtc") FROM "AuthSessions" s WHERE s."UserId" = u."Id"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "AuthSessions");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "AuthSessions");

            migrationBuilder.DropColumn(
                name: "CreatedUtc",
                table: "AspNetUsers");
        }
    }
}
