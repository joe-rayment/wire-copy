// Educational and personal use only.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NYTAudioScraper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Articles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Section = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScrapedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AudioFilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Articles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AudioChapters",
                columns: table => new
                {
                    ArticleId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartTimeMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    AudioFilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioChapters", x => new { x.ArticleId, x.StartTimeMs });
                });

            migrationBuilder.CreateTable(
                name: "ScrapingSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OutputFilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalCharactersProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapingSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrapingSessionArticle",
                columns: table => new
                {
                    ArticleId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapingSessionArticle", x => new { x.ArticleId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_ScrapingSessionArticle_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScrapingSessionArticle_ScrapingSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ScrapingSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_PublishedDate",
                table: "Articles",
                column: "PublishedDate");

            migrationBuilder.CreateIndex(
                name: "IX_Articles_Url",
                table: "Articles",
                column: "Url");

            migrationBuilder.CreateIndex(
                name: "IX_AudioChapters_ArticleId",
                table: "AudioChapters",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingSessionArticle_SessionId",
                table: "ScrapingSessionArticle",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingSessions_StartedAt",
                table: "ScrapingSessions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingSessions_Status",
                table: "ScrapingSessions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioChapters");

            migrationBuilder.DropTable(
                name: "ScrapingSessionArticle");

            migrationBuilder.DropTable(
                name: "Articles");

            migrationBuilder.DropTable(
                name: "ScrapingSessions");
        }
    }
}
