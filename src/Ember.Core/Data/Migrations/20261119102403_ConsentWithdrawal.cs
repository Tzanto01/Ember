using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsentWithdrawal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentWithdrawnAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletionReminderSentAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FiresideConsentAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiresideConsentVersion",
                table: "AspNetUsers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" u SET "ConsentWithdrawnAt" = now() at time zone 'utc'
                WHERE u."HealthDataConsentAt" IS NULL AND (
                    EXISTS (SELECT 1 FROM "Habits" h WHERE h."UserId" = u."Id")
                    OR EXISTS (SELECT 1 FROM "DayMarks" m WHERE m."UserId" = u."Id")
                    OR EXISTS (SELECT 1 FROM "DayNotes" n WHERE n."UserId" = u."Id")
                    OR EXISTS (SELECT 1 FROM "Moods" o WHERE o."UserId" = u."Id")
                    OR EXISTS (SELECT 1 FROM "UserHelps" p WHERE p."UserId" = u."Id"));
                """);

            migrationBuilder.Sql("""
                DELETE FROM "Warmth" w USING "AspNetUsers" u
                WHERE u."FiresideEnabled" AND (w."FromUserId" = u."Id" OR w."ToUserId" = u."Id");
                DELETE FROM "FiresideLinks" l USING "AspNetUsers" u
                WHERE u."FiresideEnabled" AND (l."OwnerId" = u."Id" OR l."FriendId" = u."Id");
                DELETE FROM "FiresideCodes" c USING "AspNetUsers" u
                WHERE u."FiresideEnabled" AND c."UserId" = u."Id";
                UPDATE "AspNetUsers" SET "FiresideEnabled" = FALSE, "WarmthAskUntilUtc" = NULL WHERE "FiresideEnabled";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsentWithdrawnAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletionReminderSentAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FiresideConsentAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FiresideConsentVersion",
                table: "AspNetUsers");
        }
    }
}
