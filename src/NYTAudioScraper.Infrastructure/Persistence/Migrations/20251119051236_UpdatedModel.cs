using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NYTAudioScraper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdatedModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ScrapingSessionArticle",
                table: "ScrapingSessionArticle");

            migrationBuilder.DropIndex(
                name: "IX_ScrapingSessionArticle_ArticleId",
                table: "ScrapingSessionArticle");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ScrapingSessionArticle",
                table: "ScrapingSessionArticle",
                columns: new[] { "ArticleId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingSessionArticle_SessionId",
                table: "ScrapingSessionArticle",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ScrapingSessionArticle",
                table: "ScrapingSessionArticle");

            migrationBuilder.DropIndex(
                name: "IX_ScrapingSessionArticle_SessionId",
                table: "ScrapingSessionArticle");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ScrapingSessionArticle",
                table: "ScrapingSessionArticle",
                columns: new[] { "SessionId", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingSessionArticle_ArticleId",
                table: "ScrapingSessionArticle",
                column: "ArticleId");
        }
    }
}
