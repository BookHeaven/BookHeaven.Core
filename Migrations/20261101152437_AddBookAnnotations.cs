using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBookAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookAnnotations",
                columns: table => new
                {
                    BookAnnotationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StartOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    EndOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookAnnotations", x => x.BookAnnotationId);
                    table.ForeignKey(
                        name: "FK_BookAnnotations_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookAnnotations_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "ProfileId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookAnnotations_BookId",
                table: "BookAnnotations",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookAnnotations_ProfileId_BookId_ChapterIndex_StartOffset",
                table: "BookAnnotations",
                columns: new[] { "ProfileId", "BookId", "ChapterIndex", "StartOffset" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookAnnotations");
        }
    }
}
