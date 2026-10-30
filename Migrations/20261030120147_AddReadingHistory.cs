using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHeaven.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddReadingHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadingHistory",
                columns: table => new
                {
                    ReadingHistoryEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DateStarted = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DateFinished = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ElapsedTime = table.Column<TimeSpan>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingHistory", x => x.ReadingHistoryEntryId);
                    table.ForeignKey(
                        name: "FK_ReadingHistory_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "BookId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReadingHistory_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "ProfileId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReadingHistory_BookId",
                table: "ReadingHistory",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingHistory_ProfileId_BookId_DateStarted_DateFinished",
                table: "ReadingHistory",
                columns: new[] { "ProfileId", "BookId", "DateStarted", "DateFinished" },
                unique: true);

            // Dates are copied as-is (same TEXT format EF uses) so that entries created later
            // by the app for the same reading hit the unique index instead of duplicating.
            // Random UUID v4: version nibble forced to 4 and variant nibble to one of 8, 9, A or B.
            migrationBuilder.Sql(@"
                INSERT OR IGNORE INTO ReadingHistory (ReadingHistoryEntryId, ProfileId, BookId, DateStarted, DateFinished, ElapsedTime)
                SELECT
                    substr(RandomId, 1, 8) || '-' || substr(RandomId, 9, 4) || '-4' || substr(RandomId, 14, 3) || '-' ||
                    substr('89AB', abs(random()) % 4 + 1, 1) || substr(RandomId, 18, 3) || '-' || substr(RandomId, 21),
                    ProfileId,
                    BookId,
                    StartDate,
                    EndDate,
                    ElapsedTime
                FROM (SELECT *, hex(randomblob(16)) AS RandomId FROM BooksProgress)
                WHERE StartDate IS NOT NULL AND EndDate IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReadingHistory");
        }
    }
}
