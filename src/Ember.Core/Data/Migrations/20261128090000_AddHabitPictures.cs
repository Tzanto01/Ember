using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHabitPictures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Picture",
                table: "Habits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PictureOfferAnswered",
                table: "Habits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PortionAmount",
                table: "Habits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PortionUnit",
                table: "Habits",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SuggestPictures",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Units",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Picture",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "PictureOfferAnswered",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "PortionAmount",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "PortionUnit",
                table: "Habits");

            migrationBuilder.DropColumn(
                name: "SuggestPictures",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Units",
                table: "AspNetUsers");
        }
    }
}
