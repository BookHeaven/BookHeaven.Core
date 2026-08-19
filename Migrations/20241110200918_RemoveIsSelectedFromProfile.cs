using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Core.Migrations
{
    /// <inheritdoc />
    public partial class RemoveIsSelectedFromProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSelected",
                table: "Profiles");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Profiles",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<bool>(
                name: "IsSelected",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
