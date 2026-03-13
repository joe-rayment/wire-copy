using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TermReader.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginStepsToSiteCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoginStepsJson",
                table: "SiteCredentials",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoginStepsJson",
                table: "SiteCredentials");
        }
    }
}
