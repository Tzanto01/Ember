using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ember.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFireside : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FiresideEnabled",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FiresideJoinFailures",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FiresideJoinWindowUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiresideName",
                table: "AspNetUsers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FiresideNotify",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "WarmthAskUntilUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FiresideCodes",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodeProtected = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiresideCodes", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_FiresideCodes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FiresideLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FriendId = table.Column<Guid>(type: "uuid", nullable: false),
                    Muted = table.Column<bool>(type: "boolean", nullable: false),
                    ShareFlame = table.Column<bool>(type: "boolean", nullable: false),
                    KnownName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    JoinedViaMyCode = table.Column<bool>(type: "boolean", nullable: false),
                    JoinNotifyPending = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiresideLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiresideLinks_AspNetUsers_FriendId",
                        column: x => x.FriendId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FiresideLinks_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Warmth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Message = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NotifyPending = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warmth", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Warmth_AspNetUsers_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Warmth_AspNetUsers_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FiresideCodes_CodeHash",
                table: "FiresideCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiresideCodes_ExpiresAtUtc",
                table: "FiresideCodes",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FiresideLinks_FriendId",
                table: "FiresideLinks",
                column: "FriendId");

            migrationBuilder.CreateIndex(
                name: "IX_FiresideLinks_JoinNotifyPending",
                table: "FiresideLinks",
                column: "JoinNotifyPending",
                filter: "\"JoinNotifyPending\"");

            migrationBuilder.CreateIndex(
                name: "IX_FiresideLinks_OwnerId_FriendId",
                table: "FiresideLinks",
                columns: new[] { "OwnerId", "FriendId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warmth_FromUserId_ToUserId_SentAtUtc",
                table: "Warmth",
                columns: new[] { "FromUserId", "ToUserId", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Warmth_NotifyPending",
                table: "Warmth",
                column: "NotifyPending",
                filter: "\"NotifyPending\"");

            migrationBuilder.CreateIndex(
                name: "IX_Warmth_SentAtUtc",
                table: "Warmth",
                column: "SentAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Warmth_ToUserId_FromUserId",
                table: "Warmth",
                columns: new[] { "ToUserId", "FromUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FiresideCodes");

            migrationBuilder.DropTable(
                name: "FiresideLinks");

            migrationBuilder.DropTable(
                name: "Warmth");

            migrationBuilder.DropColumn(
                name: "FiresideEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FiresideJoinFailures",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FiresideJoinWindowUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FiresideName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FiresideNotify",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WarmthAskUntilUtc",
                table: "AspNetUsers");
        }
    }
}
