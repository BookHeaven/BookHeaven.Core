using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterProgressToBookProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ChapterProgress",
                table: "BooksProgress",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
            
            migrationBuilder.Sql(@"
                UPDATE BooksProgress
                SET ChapterProgress = CASE
                    WHEN PageCount IS NULL OR PageCount = 0 THEN 0.0
                    ELSE CAST(Page AS REAL) / PageCount
                END;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChapterProgress",
                table: "BooksProgress");
        }
    }
}
