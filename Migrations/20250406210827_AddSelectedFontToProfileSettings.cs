using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedFontToProfileSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedFont",
                table: "ProfilesSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedFont",
                table: "ProfilesSettings");
        }
    }
}
