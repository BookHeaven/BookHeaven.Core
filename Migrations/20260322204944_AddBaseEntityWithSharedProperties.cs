using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBaseEntityWithSharedProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Fonts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Books",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Authors",
                type: "TEXT",
                nullable: true);
            
            var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz");
            migrationBuilder.Sql($"UPDATE Fonts SET UpdatedAt = '{now}'");
            migrationBuilder.Sql($"UPDATE Books SET UpdatedAt = '{now}'");
            migrationBuilder.Sql($"UPDATE Authors SET UpdatedAt = '{now}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Fonts");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Authors");
        }
    }
}
