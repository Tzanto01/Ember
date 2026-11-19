using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTodayOrderAndHabitNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OfferHabitNote",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "TodayOrder",
                table: "AspNetUsers",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HabitNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HabitId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Text = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    CipherVersion = table.Column<int>(type: "integer", nullable: true),
                    CipherIv = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CipherText = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HabitNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HabitNotes_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HabitNotes_HabitId_Date",
                table: "HabitNotes",
                columns: new[] { "HabitId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HabitNotes");

            migrationBuilder.DropColumn(
                name: "OfferHabitNote",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TodayOrder",
                table: "AspNetUsers");
        }
    }
}
