using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireCopy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CredentialType = table.Column<int>(type: "INTEGER", nullable: false),
                    EncryptedUsername = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EncryptedPassword = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UsernameSelector = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PasswordSelector = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SubmitSelector = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LoginUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteCredentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteCredentials_Domain",
                table: "SiteCredentials",
                column: "Domain",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteCredentials");
        }
    }
}
