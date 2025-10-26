using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Configuration;
using NYTAudioScraper.Infrastructure.Parsing;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace NYTAudioScraper.Infrastructure.Browser;

public class ScraperService : IScraperService
{
    private readonly NYTConfiguration _nytConfig;
    private readonly BrowserConfiguration _browserConfig;
    private readonly NYTAuthService _authService;
    private readonly ArticleParser _articleParser;
    private readonly ILogger<ScraperService> _logger;

    public ScraperService(
        IOptions<NYTConfiguration> nytConfig,
        IOptions<BrowserConfiguration> browserConfig,
        NYTAuthService authService,
        ArticleParser articleParser,
        ILogger<ScraperService> logger)
    {
        _nytConfig = nytConfig.Value;
        _browserConfig = browserConfig.Value;
        _authService = authService;
        _articleParser = articleParser;
        _logger = logger;
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Testing authentication");

        IWebDriver? driver = null;
        try
        {
            driver = CreateWebDriver();
            return await _authService.AuthenticateAsync(driver, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication test");
            return false;
        }
        finally
        {
            driver?.Quit();
            driver?.Dispose();
        }
    }

    public async Task<IEnumerable<Article>> ScrapeArticlesAsync(
        int maxArticles = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting scrape for up to {MaxArticles} articles", maxArticles);

        IWebDriver? driver = null;
        try
        {
            driver = CreateWebDriver();

            // Authenticate first
            var authenticated = await _authService.AuthenticateAsync(driver, cancellationToken);
            if (!authenticated)
            {
                _logger.LogWarning("Authentication failed, continuing without authentication");
            }

            // Navigate to Today's Paper
            var todaysPaperUrl = $"{_nytConfig.BaseUrl}/section/todayspaper";
            _logger.LogInformation("Navigating to {Url}", todaysPaperUrl);
            driver.Navigate().GoToUrl(todaysPaperUrl);
            await Task.Delay(3000, cancellationToken);

            // Extract article URLs
            var articleUrls = ExtractArticleUrls(driver, maxArticles);
            _logger.LogInformation("Found {Count} article URLs", articleUrls.Count);

            var articles = new List<Article>();

            // Scrape each article
            foreach (var url in articleUrls.Take(maxArticles))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    _logger.LogInformation("Scraping article: {Url}", url);
                    driver.Navigate().GoToUrl(url);
                    await Task.Delay(_nytConfig.RateLimitDelayMs, cancellationToken);

                    var pageSource = driver.PageSource;
                    var article = _articleParser.ParseArticle(pageSource, url);

                    if (article != null)
                    {
                        articles.Add(article);
                        _logger.LogInformation("Successfully scraped article: {Title}", article.Title);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse article from {Url}", url);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scraping article {Url}", url);
                }
            }

            _logger.LogInformation("Scraping completed. Successfully scraped {Count} articles", articles.Count);
            return articles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during scraping");
            return Enumerable.Empty<Article>();
        }
        finally
        {
            driver?.Quit();
            driver?.Dispose();
        }
    }

    private IWebDriver CreateWebDriver()
    {
        var options = new ChromeOptions();

        // Anti-detection measures
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);
        options.AddArgument($"user-agent={_browserConfig.UserAgent}");

        if (_browserConfig.Headless)
        {
            options.AddArgument("--headless=new");
        }

        if (_browserConfig.DisableImages)
        {
            options.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);
        }

        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--window-size=1920,1080");

        var driver = new ChromeDriver(options);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(_browserConfig.ImplicitWaitSeconds);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(_browserConfig.PageLoadTimeoutSeconds);

        // Execute script to mask WebDriver detection
        var script = @"
            Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
            window.navigator.chrome = {runtime: {}};
        ";
        ((IJavaScriptExecutor)driver).ExecuteScript(script);

        return driver;
    }

    private List<string> ExtractArticleUrls(IWebDriver driver, int maxArticles)
    {
        try
        {
            _logger.LogInformation("Extracting article URLs from Front Page and Business sections");

            var articleUrls = new List<string>();

            // Find all sections on the page
            var sections = driver.FindElements(By.CssSelector("section, div[data-testid*='section'], div.css-13y0vf2"));

            foreach (var section in sections)
            {
                try
                {
                    // Try to find the section header
                    var headers = section.FindElements(By.CssSelector("h2, h3, [data-testid='section-heading']"));

                    if (headers.Count == 0)
                    {
                        continue;
                    }

                    var sectionTitle = headers[0].Text.Trim();

                    // Check if this is a section we want
                    bool isFrontPage = sectionTitle.Equals("The Front Page", StringComparison.OrdinalIgnoreCase) ||
                                      sectionTitle.Equals("Front Page", StringComparison.OrdinalIgnoreCase);

                    bool isBusinessSection = sectionTitle.Contains("Business", StringComparison.OrdinalIgnoreCase);

                    bool isInternational = sectionTitle.Equals("International", StringComparison.OrdinalIgnoreCase);

                    // If we hit International section, we've passed The Front Page
                    if (isInternational)
                    {
                        _logger.LogDebug("Reached International section, stopping Front Page collection");
                        continue;
                    }

                    if (isFrontPage || isBusinessSection)
                    {
                        _logger.LogInformation("Processing section: {SectionTitle}", sectionTitle);

                        // Find all article links in this section
                        var links = section.FindElements(By.CssSelector("a[href*='/2024/'], a[href*='/2025/']"))
                            .Select(e => e.GetAttribute("href"))
                            .Where(url => !string.IsNullOrWhiteSpace(url) &&
                                        (url.Contains("/article/", StringComparison.OrdinalIgnoreCase) ||
                                         (url.Split('/').Length > 6 && url.EndsWith(".html", StringComparison.OrdinalIgnoreCase))))
                            .Distinct()
                            .ToList();

                        _logger.LogInformation("Found {Count} article(s) in {Section}", links.Count, sectionTitle);
                        articleUrls.AddRange(links);

                        if (articleUrls.Count >= maxArticles)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error processing section");
                    continue;
                }
            }

            // Fallback: If no sections found, try a simpler approach
            if (articleUrls.Count == 0)
            {
                _logger.LogWarning("No sections found, using fallback article extraction");

                // Get all article links on the page
                var allLinks = driver.FindElements(By.CssSelector("a[href*='/2024/'], a[href*='/2025/']"))
                    .Select(e => e.GetAttribute("href"))
                    .Where(url => !string.IsNullOrWhiteSpace(url) &&
                                (url.Contains("/article/", StringComparison.OrdinalIgnoreCase) ||
                                 (url.Split('/').Length > 6 && url.EndsWith(".html", StringComparison.OrdinalIgnoreCase))))
                    .Distinct()
                    .ToList();

                // Take only from the beginning of the page (likely Front Page)
                articleUrls = allLinks.Take(maxArticles).ToList();
            }

            var finalUrls = articleUrls.Distinct().Take(maxArticles).ToList();
            _logger.LogInformation("Total articles selected: {Count}", finalUrls.Count);

            return finalUrls;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting article URLs");
            return new List<string>();
        }
    }
}
