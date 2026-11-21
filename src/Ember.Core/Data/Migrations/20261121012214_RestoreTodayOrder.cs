using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <summary>
    /// Data only: gives accounts that already had habits or routines before <see cref="TodayOrderRelease"/> their Today order from
    /// before then (routines with members by position, then habits outside a routine by creation). Only null orders are filled;
    /// accounts whose rows all came later keep the automatic order. Down leaves the orders as they are.
    /// </summary>
    public partial class RestoreTodayOrder : Migration
    {
        /// <summary>When the stored Today order went live (UTC); rows created before it were sorted by the old Today.</summary>
        public const string TodayOrderRelease = "2026-11-20T02:24:00Z";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE "AspNetUsers" AS u SET "TodayOrder" = o."Keys"
                FROM (
                    SELECT k."UserId", array_agg(k."Key" ORDER BY k."Section", k."Rank") AS "Keys"
                    FROM (
                        SELECT r."UserId", 'r:' || r."Id" AS "Key", 0 AS "Section",
                               ROW_NUMBER() OVER (PARTITION BY r."UserId" ORDER BY r."Position", r."CreatedAt", r."Id") AS "Rank"
                        FROM "Routines" AS r
                        WHERE EXISTS (SELECT 1 FROM "Habits" AS m WHERE m."RoutineId" = r."Id")
                        UNION ALL
                        SELECT h."UserId", 'h:' || h."Id", 1,
                               ROW_NUMBER() OVER (PARTITION BY h."UserId" ORDER BY h."CreatedAt", h."Id")
                        FROM "Habits" AS h
                        WHERE h."RoutineId" IS NULL
                    ) AS k
                    GROUP BY k."UserId"
                ) AS o
                WHERE u."Id" = o."UserId" AND u."TodayOrder" IS NULL
                  AND (EXISTS (SELECT 1 FROM "Habits" AS h WHERE h."UserId" = u."Id" AND h."CreatedAt" < TIMESTAMPTZ '{TodayOrderRelease}')
                       OR EXISTS (SELECT 1 FROM "Routines" AS r WHERE r."UserId" = u."Id" AND r."CreatedAt" < TIMESTAMPTZ '{TodayOrderRelease}'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
