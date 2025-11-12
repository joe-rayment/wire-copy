using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NYTAudioScraper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioFilePathToArticle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioFilePath",
                table: "Articles",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioFilePath",
                table: "Articles");
        }
    }
}
