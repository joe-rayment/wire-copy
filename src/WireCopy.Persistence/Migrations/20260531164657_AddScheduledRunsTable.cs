using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireCopy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledRunsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipeName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OccurrenceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetLocalPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TargetFeedUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ErrorClass = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    StepOutcomesJson = table.Column<string>(type: "TEXT", nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledRuns_RecipeId_OccurrenceKey",
                table: "ScheduledRuns",
                columns: new[] { "RecipeId", "OccurrenceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledRuns_Status",
                table: "ScheduledRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledRuns");
        }
    }
}
