using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropFeedbackBoardColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestedAt",
                table: "Feedback");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Feedback");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Feedback");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DigestedAt",
                table: "Feedback",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "Feedback",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Feedback",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "open");
        }
    }
}
