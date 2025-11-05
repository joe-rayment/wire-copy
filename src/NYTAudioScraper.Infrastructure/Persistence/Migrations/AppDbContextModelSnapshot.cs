using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NYTAudioScraper.Infrastructure.Persistence;

#nullable disable

namespace NYTAudioScraper.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    partial class AppDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.0");

            modelBuilder.Entity("NYTAudioScraper.Domain.Entities.Article", b =>
                {
                    b.Property<string>("Id")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("Author")
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<string>("Content")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("PublishedDate")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("ScrapedDate")
                        .HasColumnType("TEXT");

                    b.Property<string>("Section")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("Title")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("Url")
                        .IsRequired()
                        .HasMaxLength(1000)
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("PublishedDate");

                    b.HasIndex("Url");

                    b.ToTable("Articles", (string)null);
                });

            modelBuilder.Entity("NYTAudioScraper.Domain.Entities.AudioChapter", b =>
                {
                    b.Property<string>("ArticleId")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<int>("StartTimeMs")
                        .HasColumnType("INTEGER");

                    b.Property<string>("AudioFilePath")
                        .HasMaxLength(1000)
                        .HasColumnType("TEXT");

                    b.Property<int>("DurationMs")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Title")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.HasKey("ArticleId", "StartTimeMs");

                    b.HasIndex("ArticleId");

                    b.ToTable("AudioChapters", (string)null);
                });

            modelBuilder.Entity("NYTAudioScraper.Domain.Entities.ScrapingSession", b =>
                {
                    b.Property<string>("Id")
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("CompletedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("ErrorMessage")
                        .HasColumnType("TEXT");

                    b.Property<decimal>("EstimatedCost")
                        .HasPrecision(18, 4)
                        .HasColumnType("TEXT");

                    b.Property<string>("OutputFilePath")
                        .HasMaxLength(1000)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("StartedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<int>("TotalCharactersProcessed")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("StartedAt");

                    b.HasIndex("Status");

                    b.ToTable("ScrapingSessions", (string)null);
                });

            modelBuilder.Entity("ScrapingSessionArticle", b =>
                {
                    b.Property<string>("SessionId")
                        .HasColumnType("TEXT");

                    b.Property<string>("ArticleId")
                        .HasColumnType("TEXT");

                    b.HasKey("SessionId", "ArticleId");

                    b.HasIndex("ArticleId");

                    b.ToTable("ScrapingSessionArticle");
                });

            modelBuilder.Entity("ScrapingSessionArticle", b =>
                {
                    b.HasOne("NYTAudioScraper.Domain.Entities.Article", null)
                        .WithMany()
                        .HasForeignKey("ArticleId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("NYTAudioScraper.Domain.Entities.ScrapingSession", null)
                        .WithMany()
                        .HasForeignKey("SessionId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });
#pragma warning restore 612, 618
        }
    }
}
