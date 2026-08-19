using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Core.Migrations
{
    /// <inheritdoc />
    public partial class FixProfileSettingsValueDecimals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ProfilesSettings
                SET FontSize = ROUND(FontSize, 0),
                    LineHeight = ROUND(LineHeight, 1),
                    LetterSpacing = ROUND(LetterSpacing, 1),
                    WordSpacing = ROUND(WordSpacing, 1),
                    ParagraphSpacing = ROUND(ParagraphSpacing, 1),
                    TextIndent = ROUND(TextIndent, 1),
                    HorizontalMargin = ROUND(HorizontalMargin, 1),
                    VerticalMargin = ROUND(VerticalMargin, 1)
                    ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
