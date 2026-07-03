// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-6yb7.5 — drives REAL HEADFUL Chromium (under a self-managed Xvfb
/// when no display is present — the never-headless law applies to the tests
/// too, workspace-9k27) to prove PickScript's contract: while armed, clicking a
/// link never navigates and records exactly one pick (href + text +
/// LinkExtractor-format parent chain); takeover marking (__wcLastUserInput) is
/// suppressed; Disarm removes everything and a post-disarm click navigates
/// again. Skips (page stays null) when neither a display nor Xvfb is available.
/// </summary>
[Trait("Category", "Integration")]
public class PickScriptIntegrationTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private System.Diagnostics.Process? _xvfb;

    private const string TestHtml = """
        <html><body>
          <div id="topcol1">
            <div class="clus extra third">
              <div class="ourh"><a id="lead" href="https://publisher.example/big-story">The Big Story Headline</a></div>
              <a href="https://publisher.example">Publisher</a>
            </div>
          </div>
          <p id="plain">no link here</p>
        </body></html>
        """;

    public async Task InitializeAsync()
    {
        try
        {
            // NEVER headless — same law as the app. Use the ambient display when
            // one exists; otherwise stand up a private Xvfb for this fixture.
            var display = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrEmpty(display) && OperatingSystem.IsLinux())
            {
                display = ":96";
                _xvfb = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "Xvfb",
                    ArgumentList = { display, "-screen", "0", "1280x800x24" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                await Task.Delay(500); // let the server come up
            }

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new()
            {
                Headless = false, // never-headless law (workspace-8ne3 / 9k27)
                Env = new Dictionary<string, string> { ["DISPLAY"] = display ?? string.Empty },
            });
            _page = await _browser.NewPageAsync();
        }
        catch
        {
            // Leave _page null — tests skip below (no display and no Xvfb).
        }
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();

        if (_xvfb is { HasExited: false })
        {
            try
            {
                _xvfb.Kill();
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }

        _xvfb?.Dispose();
    }

    private async Task<IPage> ArmedPageAsync()
    {
        Skip.If(_page == null, "Headless Chromium unavailable in this environment.");
        var page = _page!;
        await page.SetContentAsync(TestHtml);

        // Simulate the session's takeover init script (BrowserSession.cs) —
        // including the pick-aware skip the marker now owns.
        await page.EvaluateAsync(
            """
            () => {
                const mark = () => {
                    if (window.__wcPick && window.__wcPick.active) return;
                    window.__wcLastUserInput = Date.now();
                };
                ['pointerdown', 'keydown', 'wheel', 'touchstart', 'pointermove']
                    .forEach((e) => window.addEventListener(e, mark, { capture: true, passive: true }));
                window.__wcLastUserInput = 12345;
            }
            """);

        var armed = await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Arm);
        armed.Should().Be("armed");
        return page;
    }

    [SkippableFact]
    public async Task Click_WhileArmed_DoesNotNavigate_AndRecordsOnePick()
    {
        var page = await ArmedPageAsync();
        var urlBefore = page.Url;

        await page.ClickAsync("#lead");

        page.Url.Should().Be(urlBefore, "an armed click must never navigate the lens");

        var json = await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Poll);
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("publisher.example/big-story");
        json.Should().Contain("The Big Story Headline");
        json.Should().Contain("div.clus.extra", "parent chain caps at two classes per element");
        json.Should().Contain("div#topcol1");

        var second = await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Poll);
        second.Should().BeEmpty("each pick is observed exactly once");
    }

    [SkippableFact]
    public async Task Click_WhileArmed_DoesNotBumpTakeoverTimestamp()
    {
        var page = await ArmedPageAsync();

        await page.ClickAsync("#lead");

        var ts = await page.EvaluateAsync<double>("() => window.__wcLastUserInput || 0");
        ts.Should().Be(12345, "pick interaction is wizard input, not a human takeover");
    }

    [SkippableFact]
    public async Task Disarm_RemovesEverything_AndRestoresNavigationAndTakeover()
    {
        var page = await ArmedPageAsync();
        await page.ClickAsync("#lead");

        var disarmed = await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Disarm);
        disarmed.Should().Be("disarmed");

        var stateGone = await page.EvaluateAsync<bool>("() => !window['__wcPick']");
        stateGone.Should().BeTrue();
        var outlineGone = await page.EvaluateAsync<bool>("() => !document.querySelector('.__wirecopy-pick-outline')");
        outlineGone.Should().BeTrue();

        // Takeover marking works again after disarm.
        await page.ClickAsync("#plain");
        var ts = await page.EvaluateAsync<double>("() => window.__wcLastUserInput || 0");
        ts.Should().BeGreaterThan(12345, "the takeover patch must not outlive pick mode");

        // Second arm/disarm cycle proves idempotent teardown.
        var rearmed = await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Arm);
        rearmed.Should().Be("armed");
        await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Disarm);
    }

    [SkippableFact]
    public async Task Arm_IsIdempotent()
    {
        var page = await ArmedPageAsync();

        var again = await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Arm);

        again.Should().Be("already-armed");
        await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Disarm);
    }

    [SkippableFact]
    public async Task Click_OnNonLink_RecordsNothing()
    {
        var page = await ArmedPageAsync();

        await page.ClickAsync("#plain");

        var json = await page.EvaluateAsync<string>(WireCopy.Infrastructure.Browser.PickScript.Poll);
        json.Should().BeEmpty();
    }
}
