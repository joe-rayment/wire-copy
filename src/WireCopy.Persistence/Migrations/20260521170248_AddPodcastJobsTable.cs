using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireCopy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPodcastJobsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PodcastJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CollectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CollectionTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastProgressAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastProgressJson = table.Column<string>(type: "TEXT", nullable: true),
                    TargetLocalPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TargetFeedUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ErrorClass = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PodcastJobs_Status",
                table: "PodcastJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PodcastJobs");
        }
    }
}
