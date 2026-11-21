using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckInSkipped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Skipped",
                table: "HabitLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Skipped",
                table: "HabitLogs");
        }
    }
}
