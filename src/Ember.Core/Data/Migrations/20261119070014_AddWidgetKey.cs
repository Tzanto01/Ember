using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWidgetKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "WidgetKeyHash",
                table: "AuthSessions",
                type: "bytea",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_WidgetKeyHash",
                table: "AuthSessions",
                column: "WidgetKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthSessions_WidgetKeyHash",
                table: "AuthSessions");

            migrationBuilder.DropColumn(
                name: "WidgetKeyHash",
                table: "AuthSessions");
        }
    }
}
