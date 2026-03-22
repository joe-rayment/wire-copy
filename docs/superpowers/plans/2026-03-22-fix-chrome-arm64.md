# Fix Chrome Crashing on ARM64 Docker — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix Chrome "starting and immediately crashing" on ARM64 Docker by correcting binary detection, removing the broken CDP fallback, and adding graceful degradation when Selenium is unavailable.

**Architecture:** BrowserSession.CreateChromeDriver() is the single choke-point for all Chrome startup. We fix it in three phases: (1) detect ARM64 and use the correct Chrome binary, (2) replace the broken CDP fallback with a clean "Selenium unavailable" state, (3) make all consumers handle that state gracefully. No new dependencies needed — the app already has HTTP-first loading; Selenium is a fallback for JS-heavy sites.

**Tech Stack:** .NET 9, Selenium.WebDriver 4.41, xUnit, NSubstitute, FluentAssertions

---

## Root Cause Summary

Three stacked failures on ARM64 (aarch64) Docker:

1. **Binary mismatch:** `CreateChromeDriver()` (BrowserSession.cs:337-348) sets `options.BinaryLocation` to `~/.cache/selenium/chrome/linux64/` — an x86_64 binary that can't execute on ARM64.

2. **No ARM64 chromedriver:** Google publishes zero `linux-arm64` chromedriver builds. The system `chromedriver` is a snap stub that exits immediately in Docker. Selenium Manager downloads x86_64 chromedriver.

3. **Broken CDP fallback:** `LaunchChromeViaCdp()` (BrowserSession.cs:431-514) launches Playwright's ARM64 Chrome correctly, then creates `new RemoteWebDriver(URI, options)` — RemoteWebDriver expects W3C WebDriver protocol, not Chrome DevTools Protocol. Always fails with "The newSession command returned an unexpected error." Also leaks an orphaned Chrome process on failure (no try/finally cleanup).

**Working component:** Playwright's ARM64 Chromium at `~/.cache/ms-playwright/chromium-*/chrome-linux/chrome` runs and renders pages correctly. The HTTP-first page loading path works fine.

---

## File Map

| Action | File | Responsibility |
|--------|------|---------------|
| Modify | `src/TermReader.Infrastructure/Browser/BrowserSession.cs` | Fix Chrome binary detection, remove CDP fallback, add Selenium availability check |
| Modify | `src/TermReader.Infrastructure/Browser/IBrowserSession.cs` | Add `IsSeleniumAvailable` property |
| Modify | `src/TermReader.Infrastructure/Browser/PageLoader.cs` | Check Selenium availability before BrowserFetchAsync |
| Modify | `src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs` | Guard GetOrCreateDriver calls behind availability check |
| Modify | `src/TermReader.Infrastructure/Browser/WebDriverQueue.cs` | Fail fast when Selenium unavailable |
| Modify | `Dockerfile` | Remove snap stubs, keep shared libs, architecture-conditional Chrome install |
| Create | `tests/TermReader.Tests/Browser/BrowserSessionArm64Tests.cs` | Unit tests for ARM64 detection and graceful degradation |

---

### Task 1: Fix Chrome Binary Detection for ARM64

**Beads:** `docs-rrl`

**Files:**
- Modify: `src/TermReader.Infrastructure/Browser/BrowserSession.cs:330-429`
- Modify: `src/TermReader.Infrastructure/Browser/IBrowserSession.cs`
- Create: `tests/TermReader.Tests/Browser/BrowserSessionArm64Tests.cs`

This task fixes `CreateChromeDriver()` to detect ARM64 and use the correct Chrome binary instead of the x86_64 Selenium-managed one.

- [ ] **Step 1: Write the failing test — ARM64 availability and GetOrCreateDriver guard**

Create `tests/TermReader.Tests/Browser/BrowserSessionArm64Tests.cs`:

```csharp
// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

public class BrowserSessionArm64Tests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void IsSeleniumAvailable_DoesNotThrow()
    {
        // Arrange
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);

        // Act — property access should not throw regardless of platform
        bool available = false;
        var act = () => available = session.IsSeleniumAvailable;

        // Assert
        act.Should().NotThrow();
        // On ARM64 Docker: false. On x86_64 CI: true.
        // We can't assert value in a cross-platform test, but we CAN verify
        // the ARM64 path on this machine:
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 &&
            OperatingSystem.IsLinux())
        {
            available.Should().BeFalse("ARM64 Linux has no chromedriver");
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetOrCreateDriver_WhenSeleniumUnavailable_ThrowsInvalidOperationWithClearMessage()
    {
        // Arrange
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);

        // Only meaningful on ARM64 — on x86_64 this would try to launch Chrome
        if (!session.IsSeleniumAvailable)
        {
            // Act
            var act = () => session.GetOrCreateDriver(headless: true);

            // Assert — clear message, not a cryptic ChromeDriver crash
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Selenium*unavailable*");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TermReader.Tests --filter "FullyQualifiedName~BrowserSessionArm64Tests" -v n`
Expected: Compilation error — `IsSeleniumAvailable` doesn't exist on `BrowserSession` yet.

- [ ] **Step 3: Add `IsSeleniumAvailable` to IBrowserSession interface**

In `src/TermReader.Infrastructure/Browser/IBrowserSession.cs`, add after `HasActiveDriver` (line 17):

```csharp
    /// <summary>
    /// Gets a value indicating whether Selenium WebDriver is available on this platform.
    /// False on ARM64 Linux where chromedriver is not available.
    /// When false, GetOrCreateDriver will throw InvalidOperationException.
    /// </summary>
    bool IsSeleniumAvailable { get; }
```

- [ ] **Step 4: Add architecture detection and `IsSeleniumAvailable` to BrowserSession**

In `src/TermReader.Infrastructure/Browser/BrowserSession.cs`:

**Add field** after `_disposed` (around line 29):
```csharp
    private readonly bool _seleniumAvailable;
```

**Add property** after `HasActiveDriver` (around line 50):
```csharp
    public bool IsSeleniumAvailable => _seleniumAvailable;
```

**Initialize in constructor** (end of constructor, after line 38):
```csharp
        _seleniumAvailable = ProbeSeleniumAvailability();
```

**Add helper methods** after `FindPlaywrightChrome` (around line 203):

```csharp
    private static bool IsArm64Linux()
    {
        return OperatingSystem.IsLinux() &&
               System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
               System.Runtime.InteropServices.Architecture.Arm64;
    }

    private bool ProbeSeleniumAvailability()
    {
        if (string.Equals(_browserConfig.BrowserType, "firefox", StringComparison.OrdinalIgnoreCase))
        {
            return true; // Firefox/geckodriver may work — let it try
        }

        // On ARM64 Linux, Google provides no chromedriver binary.
        // Selenium Manager downloads x86_64 chromedriver which can't run.
        // The system chromedriver is a snap stub that doesn't work in Docker.
        if (IsArm64Linux())
        {
            _logger.LogWarning(
                "ARM64 Linux detected — no compatible chromedriver available. " +
                "Selenium features disabled; pages will load via HTTP only. " +
                "JS-heavy sites may not render correctly.");
            return false;
        }

        return true;
    }
```

- [ ] **Step 5: Fix `CreateChromeDriver` to use correct Chrome binary on ARM64**

Replace the Selenium-managed Chrome binary detection block (lines 335-349 of BrowserSession.cs — the `seleniumChrome` / `linux64` / `BinaryLocation` block) with a call to the new `FindChromeBinary` method. The first ~15 lines of `CreateChromeDriver` become:

```csharp
    private IWebDriver CreateChromeDriver(bool headless)
    {
        _logger.LogDebug("Creating Chrome driver with headless={Headless}", headless);
        var options = new ChromeOptions();

        // Find Chrome binary: prefer CHROME_BIN, then Playwright, then Selenium-managed
        var chromeBin = FindChromeBinary();
        if (chromeBin != null)
        {
            options.BinaryLocation = chromeBin;
            _logger.LogDebug("Using Chrome binary: {Path}", chromeBin);
        }

        // Anti-detection (existing code unchanged from here)
        options.AddArgument("--disable-blink-features=AutomationControlled");
        // ... etc ...
```

**Remove** the old detection block entirely (lines 335-349: the `seleniumChrome`, `linux64`, `latestDir`, `chromeBin` block).

Add the `FindChromeBinary` method after `IsArm64Linux`:

```csharp
    private string? FindChromeBinary()
    {
        // 1. Explicit environment variable takes precedence
        var envChrome = Environment.GetEnvironmentVariable("CHROME_BIN");
        if (!string.IsNullOrEmpty(envChrome) && File.Exists(envChrome))
        {
            return envChrome;
        }

        // 2. Playwright's Chromium (ARM64-native when installed on ARM64)
        var playwrightChrome = FindPlaywrightChrome();
        if (playwrightChrome != null)
        {
            return playwrightChrome;
        }

        // 3. Selenium-managed Chrome (only on matching architecture — x86_64 binaries
        //    can't run on ARM64 without qemu)
        if (!IsArm64Linux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var seleniumChrome = Path.Combine(home, ".cache", "selenium", "chrome", "linux64");
            if (Directory.Exists(seleniumChrome))
            {
                var latestDir = Directory.GetDirectories(seleniumChrome)
                    .OrderByDescending(d => d)
                    .FirstOrDefault();
                var chromePath = latestDir != null ? Path.Combine(latestDir, "chrome") : null;
                if (chromePath != null && File.Exists(chromePath))
                {
                    return chromePath;
                }
            }
        }

        // 4. Let Selenium Manager figure it out (may fail on ARM64)
        return null;
    }
```

- [ ] **Step 6: Guard `GetOrCreateDriver` with availability check**

In `GetOrCreateDriver` (line 56), right after the `ObjectDisposedException.ThrowIf` line, add:

```csharp
            if (!_seleniumAvailable)
            {
                throw new InvalidOperationException(
                    "Selenium is unavailable on this platform (ARM64 Linux — no compatible chromedriver). " +
                    "Pages are loaded via HTTP. JS-heavy sites may not render correctly.");
            }
```

- [ ] **Step 7: Run tests**

Run: `dotnet test tests/TermReader.Tests --filter "FullyQualifiedName~BrowserSessionArm64Tests" -v n`
Expected: PASS

- [ ] **Step 8: Run full test suite to check for regressions**

Run: `dotnet test tests/TermReader.Tests -v n`
Expected: All existing tests pass. NSubstitute mocks of `IBrowserSession` will need `IsSeleniumAvailable` stubbed — check if any test creates a mock of `IBrowserSession` without setting this property (NSubstitute returns `false` by default for unstubbed `bool` properties, which is correct for ARM64 tests but may affect existing tests that expect Selenium to be available). If existing tests fail, add `.IsSeleniumAvailable.Returns(true)` to their mocks.

- [ ] **Step 9: Commit**

```bash
git add src/TermReader.Infrastructure/Browser/BrowserSession.cs \
        src/TermReader.Infrastructure/Browser/IBrowserSession.cs \
        tests/TermReader.Tests/Browser/BrowserSessionArm64Tests.cs
git commit -m "fix(browser): detect ARM64 and use correct Chrome binary

Add architecture detection to BrowserSession so it uses Playwright's
ARM64 Chromium instead of Selenium Manager's x86_64 binary. Add
IsSeleniumAvailable property that returns false on ARM64 Linux where
no chromedriver is available. GetOrCreateDriver throws a clear
InvalidOperationException instead of crashing."
```

---

### Task 2: Remove Broken CDP Fallback

**Beads:** `docs-skc` (partial — removes the broken code; PuppeteerSharp replacement deferred to future)

**Files:**
- Modify: `src/TermReader.Infrastructure/Browser/BrowserSession.cs:384-514`

The `LaunchChromeViaCdp` method can never work (RemoteWebDriver doesn't speak CDP) and leaks orphaned Chrome processes on failure. Remove it.

- [ ] **Step 1: Simplify CreateChromeDriver catch blocks and remove LaunchChromeViaCdp**

In `BrowserSession.cs`, replace the try/catch in `CreateChromeDriver` (lines 384-403). The ARM64 guard in `GetOrCreateDriver` (Task 1) means this code only runs on architectures where chromedriver should work. Keep the original specificity for WebDriverException:

```csharp
        IWebDriver driver;
        try
        {
            var service = ChromeDriverService.CreateDefaultService();
            service.SuppressInitialDiagnosticInformation = true;
            service.HideCommandPromptWindow = true;

            driver = new ChromeDriver(service, options);
            _driverServicePid = service.ProcessId;
        }
        catch (DriverServiceNotFoundException ex)
        {
            _logger.LogError(ex, "ChromeDriver not found. Install chromedriver matching your Chrome version.");
            throw new InvalidOperationException(
                "ChromeDriver binary not found. Install chromedriver or set it on PATH.", ex);
        }
        catch (WebDriverException ex) when (ex.Message.Contains("exited unexpectedly"))
        {
            _logger.LogError(ex,
                "ChromeDriver exited unexpectedly — possible architecture mismatch or missing dependencies.");
            throw new InvalidOperationException(
                "ChromeDriver crashed on startup. Check architecture compatibility and Chrome version.", ex);
        }
```

- [ ] **Step 2: Delete the `LaunchChromeViaCdp` method entirely (lines 431-514)**

Remove the entire method and its `using` for `System.Diagnostics.Process` if it becomes unused (but `ForceKillBrowserProcesses` also uses `Process`, so it stays).

Also remove the `using OpenQA.Selenium.Remote;` if `RemoteWebDriver` is no longer used anywhere in the file.

- [ ] **Step 3: Build and run tests**

Run: `dotnet build src/TermReader.Infrastructure && dotnet test tests/TermReader.Tests -v n`
Expected: Build succeeds, all tests pass. Run `grep -r "LaunchChromeViaCdp" src/` to verify no remaining references.

- [ ] **Step 4: Commit**

```bash
git add src/TermReader.Infrastructure/Browser/BrowserSession.cs
git commit -m "fix(browser): remove broken CDP fallback that leaked Chrome processes

LaunchChromeViaCdp used RemoteWebDriver which speaks W3C WebDriver
protocol, not Chrome DevTools Protocol — it could never work. It also
leaked orphaned Chrome processes (no try/finally on Process.Start).
ARM64 is now handled by the IsSeleniumAvailable check upstream."
```

---

### Task 3: Graceful Degradation in PageLoader and Consumers

**Beads:** `docs-23e`

**Files:**
- Modify: `src/TermReader.Infrastructure/Browser/PageLoader.cs:508-555`
- Modify: `src/TermReader.Infrastructure/Browser/WebDriverQueue.cs:60,91`
- Modify: `src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs:1301,1347,1388`
- Modify: `tests/TermReader.Tests/Browser/BrowserSessionArm64Tests.cs`

When Selenium is unavailable, consumers should skip browser fallback instead of crashing.

- [ ] **Step 1: Write failing test — PageLoader returns HTTP result when Selenium unavailable**

Add to `tests/TermReader.Tests/Browser/BrowserSessionArm64Tests.cs`:

```csharp
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PageLoader_WhenSeleniumUnavailable_SkipsBrowserFetch()
    {
        // Arrange
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsSeleniumAvailable.Returns(false);

        var config = Options.Create(new BrowserConfiguration());
        var logger = Substitute.For<ILogger<PageLoader>>();

        // PageLoader constructor: (IOptions<BrowserConfiguration>, ILogger<PageLoader>, IBrowserSession, HttpClient?)
        var httpClient = new HttpClient(new FakeHttpHandler(
            "<html><head><title>Test</title></head><body><article><p>Content here.</p></article></body></html>"));
        var pageLoader = new PageLoader(config, logger, browserSession, httpClient);

        // Act
        var result = await pageLoader.LoadAsync(
            new Application.DTOs.Browser.PageLoadRequest { Url = "https://example.com" },
            CancellationToken.None);

        // Assert — loaded via HTTP, never called GetOrCreateDriver
        result.Success.Should().BeTrue();
        browserSession.DidNotReceive().GetOrCreateDriver(Arg.Any<bool>());
    }

    private class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _html;
        public FakeHttpHandler(string html) => _html = html;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_html, System.Text.Encoding.UTF8, "text/html"),
                RequestMessage = request,
            });
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TermReader.Tests --filter "FullyQualifiedName~PageLoader_WhenSeleniumUnavailable" -v n`
Expected: Fails — PageLoader still calls BrowserFetchAsync → GetOrCreateDriver when Selenium unavailable.

- [ ] **Step 3: Guard BrowserFetchAsync in PageLoader**

In `src/TermReader.Infrastructure/Browser/PageLoader.cs`, add an early return at the top of `BrowserFetchAsync` (line 508):

```csharp
    private async Task<PageLoadResult> BrowserFetchAsync(PageLoadRequest request, CancellationToken cancellationToken)
    {
        if (!_browserSession.IsSeleniumAvailable)
        {
            _logger.LogDebug("Selenium unavailable on this platform, skipping browser fetch for {Url}", request.Url);
            return PageLoadResult.Failure("Selenium unavailable (ARM64 — no chromedriver)");
        }

        try
        {
            var driver = _browserSession.GetOrCreateDriver(request.Headless);
            // ... existing code unchanged ...
```

Also add `InvalidOperationException` to the existing catch chain (after the `ObjectDisposedException` catch at line 550):

```csharp
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Selenium unavailable for page load: {Url}", request.Url);
            return PageLoadResult.Failure("Selenium unavailable");
        }
```

The `LoadSeleniumFirstAsync` method (line 381) calls `BrowserFetchAsync` internally, so it's implicitly guarded.

- [ ] **Step 4: Guard WebDriverQueue acquire methods — BEFORE semaphore**

In `src/TermReader.Infrastructure/Browser/WebDriverQueue.cs`:

In `AcquireForegroundAsync` (line 60), add the check **before** `_mainLock.WaitAsync` (before line 74):

```csharp
    private async Task<WebDriverLease> AcquireForegroundAsync(bool headless, CancellationToken cancellationToken)
    {
        if (!_browserSession.IsSeleniumAvailable)
        {
            throw new InvalidOperationException("Selenium is unavailable on this platform");
        }

        _logger.LogDebug("Foreground requesting WebDriver access");
        // ... existing code ...
```

In `AcquireBackgroundAsync` (line 91), add the same check **before** `_mainLock.WaitAsync` (before line 99):

```csharp
    private async Task<WebDriverLease> AcquireBackgroundAsync(bool headless, CancellationToken cancellationToken)
    {
        if (!_browserSession.IsSeleniumAvailable)
        {
            throw new InvalidOperationException("Selenium is unavailable on this platform");
        }

        _logger.LogDebug("Background requesting WebDriver access");
        // ... existing code ...
```

Note: `WebDriverQueue` field `_browserSession` is typed as `IBrowserSession` (verified in code), so `IsSeleniumAvailable` is accessible.

- [ ] **Step 5: Guard BrowserOrchestrator's direct GetOrCreateDriver calls**

In `src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs`, the three call sites need site-specific guards:

**Line 1301-1303 (challenge polling):** Already behind `if (_browserSession is IBrowserSession session)`. Add `IsSeleniumAvailable`:
```csharp
                if (_browserSession is IBrowserSession session && session.IsSeleniumAvailable)
                {
                    var driver = session.GetOrCreateDriver(headless);
```

**Line 1347-1352 (SaveBrowserCookiesAsync):** Uses a negative pattern `is not`. This guard already returns early when not `IBrowserSession` or no active driver. Add Selenium check:
```csharp
            if (_browserSession is not IBrowserSession session || !session.HasActiveDriver || !session.IsSeleniumAvailable)
            {
                return;
            }
```

**Line 1388-1390 (WaitForManualLoginAsync):** Already behind positive pattern. Add `IsSeleniumAvailable`:
```csharp
                if (_browserSession is IBrowserSession session && session.HasActiveDriver && session.IsSeleniumAvailable)
                {
                    var driver = session.GetOrCreateDriver(false);
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/TermReader.Tests -v n`
Expected: All tests pass including the new one. If any existing tests mock `IBrowserSession` and now fail because `IsSeleniumAvailable` defaults to `false` (NSubstitute default for `bool`), add `.IsSeleniumAvailable.Returns(true)` to those mocks.

- [ ] **Step 7: Verify by running the app**

Run: `script -qec "dotnet run --project src/TermReader.API -- browse https://example.com" /dev/null`
Expected: App loads example.com via HTTP without crashing. Check `logs/termreader-*.log` for "ARM64 Linux detected" warning.

- [ ] **Step 8: Commit**

```bash
git add src/TermReader.Infrastructure/Browser/PageLoader.cs \
        src/TermReader.Infrastructure/Browser/WebDriverQueue.cs \
        src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs \
        tests/TermReader.Tests/Browser/BrowserSessionArm64Tests.cs
git commit -m "fix(browser): graceful degradation when Selenium unavailable

PageLoader, WebDriverQueue, and BrowserOrchestrator now check
IsSeleniumAvailable before attempting Selenium operations. On ARM64,
pages load via HTTP only. JS-heavy sites show a clear message instead
of crashing with cryptic ChromeDriver errors."
```

---

### Task 4: Fix Dockerfile for ARM64 Chrome

**Beads:** `docs-6wb`

**Files:**
- Modify: `Dockerfile`

Remove snap stub packages that don't work on ARM64 Docker. Keep real shared libraries Chrome needs. Make the install architecture-conditional.

- [ ] **Step 1: Update Dockerfile with architecture-conditional Chrome install**

Replace the Chrome installation block (lines 32-63) with:

```dockerfile
# Install shared libraries that Chrome/Chromium needs at runtime.
# On ARM64, the apt 'chromium' and 'chromium-driver' packages are snap
# transitional stubs that don't work in Docker (no snapd).
# On x86_64, they install real binaries.
RUN apt-get update && \
    ARCH=$(dpkg --print-architecture) && \
    if [ "$ARCH" = "amd64" ]; then \
        apt-get install -y --no-install-recommends \
            chromium chromium-driver; \
    fi && \
    apt-get install -y --no-install-recommends \
        ca-certificates \
        xvfb \
        fonts-liberation \
        libasound2t64 \
        libatk-bridge2.0-0 \
        libatk1.0-0 \
        libatspi2.0-0 \
        libcups2t64 \
        libdbus-1-3 \
        libdrm2 \
        libgbm1 \
        libgtk-3-0t64 \
        libnspr4 \
        libnss3 \
        libwayland-client0 \
        libxcomposite1 \
        libxdamage1 \
        libxfixes3 \
        libxkbcommon0 \
        libxrandr2 \
        xdg-utils \
    && rm -rf /var/lib/apt/lists/*

# Set Chrome path only on x86_64 where the apt packages are real
RUN ARCH=$(dpkg --print-architecture) && \
    if [ "$ARCH" = "amd64" ]; then \
        echo "CHROME_BIN=/usr/bin/chromium" >> /etc/environment; \
    fi
```

Note: The `libasound2t64`, `libcups2t64`, `libgtk-3-0t64` names are for Ubuntu 25.10+. If the runtime base image is Debian 12 (bookworm), use `libasound2`, `libcups2`, `libgtk-3-0` without the `t64` suffix. Check the base image version and adjust.

- [ ] **Step 2: Remove stale ENV variables**

Remove these lines (62-63):
```dockerfile
# DELETE these — they point to snap stubs on ARM64:
ENV CHROME_BIN=/usr/bin/chromium
ENV CHROMEDRIVER_PATH=/usr/bin/chromedriver
```

The `CHROMEDRIVER_PATH` variable isn't used by the app code (Selenium Manager finds chromedriver itself). `CHROME_BIN` is read by `FindChromeBinary()` and should only be set when pointing to a real binary.

- [ ] **Step 3: Verify Dockerfile builds**

Run: `docker build -t termreader:test .` (if Docker is available in this environment)
If Docker is not available, verify the Dockerfile is syntactically valid: `docker buildx create --use 2>/dev/null; docker buildx build --platform linux/arm64 -t termreader:test . --load` or just validate syntax.

- [ ] **Step 4: Commit**

```bash
git add Dockerfile
git commit -m "fix(docker): architecture-conditional Chrome install for ARM64

On ARM64, the apt chromium/chromium-driver packages are snap
transitional stubs that don't work in Docker. Only install them
on amd64. Keep shared library dependencies on all architectures.
Remove hardcoded CHROME_BIN/CHROMEDRIVER_PATH env vars."
```

---

### Task 5: Clean Up Selenium Manager x86_64 Cache (Optional)

**Beads:** `docs-9pe`

**Files:**
- Modify: `src/TermReader.Infrastructure/Browser/BrowserSession.cs` (ProbeSeleniumAvailability method)

On ARM64, Selenium Manager downloads ~200MB of x86_64 binaries that can never run. Log a suggestion to clean up.

- [ ] **Step 1: Augment ProbeSeleniumAvailability with cache warning**

In the `ProbeSeleniumAvailability` method (added in Task 1), expand the ARM64 block:

```csharp
        if (IsArm64Linux())
        {
            _logger.LogWarning(
                "ARM64 Linux detected — no compatible chromedriver available. " +
                "Selenium features disabled; pages will load via HTTP only. " +
                "JS-heavy sites may not render correctly.");

            // Warn about wasted Selenium Manager cache (x86_64 binaries on ARM64)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var seleniumCache = Path.Combine(home, ".cache", "selenium");
            if (Directory.Exists(seleniumCache))
            {
                _logger.LogInformation(
                    "Selenium Manager cached x86_64 binaries at {Path} that cannot run on ARM64. " +
                    "Consider deleting this directory to save disk space, or set " +
                    "SE_MANAGER_OFFLINE=true to prevent re-downloads.",
                    seleniumCache);
            }

            return false;
        }
```

- [ ] **Step 2: Run tests and verify**

Run: `dotnet test tests/TermReader.Tests -v n`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/TermReader.Infrastructure/Browser/BrowserSession.cs
git commit -m "chore(browser): warn about wasted x86_64 Selenium cache on ARM64

Selenium Manager downloads ~200MB of x86_64 Chrome and chromedriver
on ARM64 where they can never run. Log a message suggesting cleanup."
```

---

## Execution Order

Tasks must be executed in order (1 → 2 → 3 → 5). Task 4 (Dockerfile) can run in parallel with Tasks 2-3.

| Task | Beads | Priority | Steps |
|------|-------|----------|-------|
| 1. Fix Chrome binary detection | `docs-rrl` | P1 | 9 |
| 2. Remove broken CDP fallback | `docs-skc` | P1 | 4 |
| 3. Graceful degradation | `docs-23e` | P2 | 8 |
| 4. Fix Dockerfile | `docs-6wb` | P1 | 4 |
| 5. Clean up cache (optional) | `docs-9pe` | P3 | 3 |

## Important Notes for Implementers

1. **NSubstitute default for `bool` is `false`**: After adding `IsSeleniumAvailable` to `IBrowserSession`, any existing test that mocks `IBrowserSession` will get `false` by default. If a test expects Selenium to be available (e.g., `PageLoaderTests`, `WebDriverQueueTests`, `AutoLoginServiceTests`), add `mockSession.IsSeleniumAvailable.Returns(true)` to its setup.

2. **The `is not` pattern at BrowserOrchestrator:1347**: This line uses `if (_browserSession is not IBrowserSession session || !session.HasActiveDriver)` — add `|| !session.IsSeleniumAvailable` to the condition, NOT `&& session.IsSeleniumAvailable`.

3. **The Dockerfile base image**: The runtime stage uses `mcr.microsoft.com/dotnet/runtime:9.0` which is Debian-based. Some library packages may use different names than Ubuntu 25.10 (no `t64` suffix). Run `apt-cache search libasound` inside the build to verify names.

## Future Work (Not In This Plan)

**PuppeteerSharp adapter for full ARM64 JS support:** The current fix makes the app work on ARM64 in HTTP-only mode. For full JS rendering support on ARM64, a future task should add PuppeteerSharp as a CDP-native alternative to Selenium. This would require implementing an `IWebDriver` adapter wrapping PuppeteerSharp's `IPage` API. Filed as the remaining scope of `docs-skc`.
