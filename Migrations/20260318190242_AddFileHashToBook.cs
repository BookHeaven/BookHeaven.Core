using BookHeaven.Core.Helpers;
using BookHeaven.Core.Extensions;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFileHashToBook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "Books",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Books_FileHash",
                table: "Books",
                column: "FileHash");
            
            // Populate hashes for existing books
            var files = Directory.GetFiles(CoreGlobals.BooksPath);
            foreach (var file in files)
            {
                var bookId = Path.GetFileNameWithoutExtension(file);
                var hash = FileHelpers.GetPartialMd5HashAsync(file).GetAwaiter().GetResult();
                migrationBuilder.Sql($"UPDATE Books SET FileHash = '{hash}' WHERE upper(BookId) = upper('{bookId}');");
            }
            
            
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Books_FileHash",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "Books");
        }
    }
}
