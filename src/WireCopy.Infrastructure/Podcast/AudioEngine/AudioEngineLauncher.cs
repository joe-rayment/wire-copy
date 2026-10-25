// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser.CommandHandlers;

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <inheritdoc cref="IAudioEngineLauncher"/>
public sealed class AudioEngineLauncher : IAudioEngineLauncher
{
    /// <summary>Environment variable naming the audio-engine folder (published or source checkout).</summary>
    internal const string HomeEnvVar = "AUDIO_ENGINE_HOME";

    /// <summary>The published server executable name without the Windows extension.</summary>
    private const string ServerName = "AudioEngine.Server";

    /// <summary>The project path passed to <c>dotnet run</c> in a source checkout.</summary>
    private const string SourceProject = "src/AudioEngine.Server";

    /// <summary>How long <c>/healthz</c> may take to answer after the spawn.</summary>
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Extra, non-fatal time given to <c>/readyz</c> once healthy.</summary>
    private static readonly TimeSpan ReadyGrace = TimeSpan.FromSeconds(15);

    /// <summary>Pause between probes.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Per-probe request budget.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IUserSettingsStore _store;
    private readonly AudioEngineConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudioEngineLauncher> _logger;

    /// <summary>Guards <see cref="_attempts"/>; never two spawns for one URL.</summary>
    private readonly object _gate = new();

    /// <summary>In-flight and finished attempts keyed by URL.</summary>
    private readonly Dictionary<string, Task<AudioEngineStartResult>> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="AudioEngineLauncher"/> class.</summary>
    /// <param name="store">The user settings store.</param>
    /// <param name="options">The configured audio-engine defaults.</param>
    /// <param name="httpClientFactory">Supplies the "AudioEngine" client used to probe health.</param>
    /// <param name="logger">The logger.</param>
    public AudioEngineLauncher(
        IUserSettingsStore store,
        IOptions<AudioEngineConfiguration> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AudioEngineLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = options.Value;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the absolute path of the file audio-engine's output is appended to.</summary>
    public static string LogPath => Path.Combine(Path.GetFullPath("logs"), "audio-engine.log");

    /// <summary>Gets the user's home folder.</summary>
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Whether <paramref name="url"/> points at this machine (a loopback host).</summary>
    /// <param name="url">The audio-engine base address.</param>
    /// <returns>True for localhost / 127.0.0.1 / ::1 URLs.</returns>
    public static bool IsLocal(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <inheritdoc/>
    public AudioEngineDiscovery Discover()
        => Discover(
            _store.Get(SettingsCommandHandler.KeyAudioEngineProgram),
            Environment.GetEnvironmentVariable,
            Home,
            File.Exists,
            Directory.Exists,
            OperatingSystem.IsWindows());

    /// <inheritdoc/>
    public bool BeginStart(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_gate)
        {
            if (_attempts.ContainsKey(url))
            {
                return false;
            }

            _attempts[url] = Task.Run(() => RunAttemptAsync(url));
            return true;
        }
    }

    /// <inheritdoc/>
    public bool IsStarting(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        lock (_gate)
        {
            return _attempts.TryGetValue(url, out var task) && !task.IsCompleted;
        }
    }

    /// <inheritdoc/>
    public async Task<AudioEngineStartResult> StartAndWaitAsync(string url, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Task<AudioEngineStartResult> attempt;
        lock (_gate)
        {
            if (!_attempts.TryGetValue(url, out var running) || running.IsCompleted)
            {
                running = Task.Run(() => RunAttemptAsync(url));
                _attempts[url] = running;
            }

            attempt = running;
        }

        return await attempt.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void ResetAttempts()
    {
        lock (_gate)
        {
            foreach (var key in _attempts.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList())
            {
                _attempts.Remove(key);
            }
        }
    }

    /// <summary>
    /// Pure discovery: the <c>AudioEngineProgram</c> setting (a file is the published executable, a folder
    /// is probed), then <c>AUDIO_ENGINE_HOME</c>, then <c>&lt;home&gt;/audio-engine</c>. A folder holding
    /// <c>AudioEngine.Server</c> is a published build; one holding <c>dotnet</c> and
    /// <c>src/AudioEngine.Server</c> is a source checkout run with <c>dotnet run</c>.
    /// </summary>
    /// <param name="programSetting">The saved "Audio engine program" setting; null or blank when none.</param>
    /// <param name="getEnv">Reads an environment variable.</param>
    /// <param name="home">The user's home folder.</param>
    /// <param name="fileExists">Whether a path is an existing file.</param>
    /// <param name="dirExists">Whether a path is an existing folder.</param>
    /// <param name="isWindows">True to look for <c>.exe</c> names.</param>
    /// <returns>The program (null when none) and every candidate examined.</returns>
    internal static AudioEngineDiscovery Discover(
        string? programSetting,
        Func<string, string?> getEnv,
        string home,
        Func<string, bool> fileExists,
        Func<string, bool> dirExists,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(dirExists);
        var lookedIn = new List<string>();

        // (a) The explicit setting: a file is the published executable, a folder is probed below.
        var setting = programSetting?.Trim();
        if (!string.IsNullOrEmpty(setting))
        {
            var path = ExpandHome(setting, home);
            if (fileExists(path))
            {
                lookedIn.Add(Shorten(path, home));
                var folder = Path.GetDirectoryName(path) ?? path;
                return new AudioEngineDiscovery(new AudioEngineProgram(path, [], folder, Shorten(path, home)), lookedIn);
            }

            if (dirExists(path))
            {
                var program = ProbeFolder(path, home, fileExists, dirExists, isWindows, lookedIn);
                if (program is not null)
                {
                    return new AudioEngineDiscovery(program, lookedIn);
                }
            }
            else
            {
                lookedIn.Add(Shorten(path, home));
            }
        }

        // (b) AUDIO_ENGINE_HOME.
        var envHome = getEnv(HomeEnvVar)?.Trim();
        if (!string.IsNullOrEmpty(envHome))
        {
            var program = ProbeFolder(ExpandHome(envHome, home), home, fileExists, dirExists, isWindows, lookedIn);
            if (program is not null)
            {
                return new AudioEngineDiscovery(program, lookedIn);
            }
        }

        // (c) ~/audio-engine.
        var defaultFolder = Path.Combine(home, "audio-engine");
        var found = ProbeFolder(defaultFolder, home, fileExists, dirExists, isWindows, lookedIn);
        return new AudioEngineDiscovery(found, lookedIn);
    }

    /// <summary>Expands a leading <c>~</c> to the home folder and makes the path absolute.</summary>
    /// <param name="path">The path as typed.</param>
    /// <param name="home">The user's home folder.</param>
    /// <returns>An absolute path.</returns>
    internal static string ExpandHome(string path, string home)
    {
        if (path == "~")
        {
            return home;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(home, path[2..]);
        }

        return Path.GetFullPath(path);
    }

    /// <summary>Shows a path with the home folder as <c>~</c>.</summary>
    /// <param name="path">An absolute path.</param>
    /// <param name="home">The user's home folder.</param>
    /// <returns>The path for display.</returns>
    internal static string Shorten(string path, string home)
    {
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        var trimmedHome = home.TrimEnd('/', '\\');
        if (path.Equals(trimmedHome, StringComparison.Ordinal))
        {
            return "~";
        }

        if (path.StartsWith(trimmedHome + "/", StringComparison.Ordinal) || path.StartsWith(trimmedHome + "\\", StringComparison.Ordinal))
        {
            return "~" + path[trimmedHome.Length..];
        }

        return path;
    }

    /// <summary>Looks for a published build, then a source checkout, in one folder.</summary>
    /// <param name="folder">The folder to probe.</param>
    /// <param name="home">The user's home folder (for display).</param>
    /// <param name="fileExists">Whether a path is an existing file.</param>
    /// <param name="dirExists">Whether a path is an existing folder.</param>
    /// <param name="isWindows">True to look for <c>.exe</c> names.</param>
    /// <param name="lookedIn">Receives the folder examined.</param>
    /// <returns>The program, or null when the folder holds neither layout.</returns>
    private static AudioEngineProgram? ProbeFolder(
        string folder,
        string home,
        Func<string, bool> fileExists,
        Func<string, bool> dirExists,
        bool isWindows,
        List<string> lookedIn)
    {
        var display = Shorten(folder, home);
        lookedIn.Add(display);
        if (!dirExists(folder))
        {
            return null;
        }

        var published = Path.Combine(folder, isWindows ? ServerName + ".exe" : ServerName);
        if (fileExists(published))
        {
            return new AudioEngineProgram(published, [], folder, display);
        }

        var dotnet = Path.Combine(folder, isWindows ? "dotnet.exe" : "dotnet");
        if (fileExists(dotnet) && dirExists(Path.Combine(folder, "src", ServerName)))
        {
            return new AudioEngineProgram(dotnet, ["run", "--project", SourceProject, "-c", "Release"], folder, display);
        }

        return null;
    }

    /// <summary>One GET with its own short timeout; any failure is "not yet".</summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="probeUrl">The URL to GET.</param>
    /// <returns>True on a 2xx answer.</returns>
    private static async Task<bool> ProbeOnceAsync(HttpClient client, string probeUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Wraps a Windows command-line token in quotes.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The quoted token.</returns>
    private static string Quote(string token) => "\"" + token + "\"";

    /// <summary>Sets an environment variable for the child unless it is already inherited.</summary>
    /// <param name="psi">The start info.</param>
    /// <param name="key">The variable name.</param>
    /// <param name="value">The value.</param>
    private static void SetIfAbsent(ProcessStartInfo psi, string key, string value)
    {
        if (!psi.Environment.ContainsKey(key))
        {
            psi.Environment[key] = value;
        }
    }

    /// <summary>Finds, spawns and waits for audio-engine at <paramref name="url"/>.</summary>
    /// <param name="url">The audio-engine base address.</param>
    /// <returns>The outcome with a user-facing message on failure.</returns>
    private async Task<AudioEngineStartResult> RunAttemptAsync(string url)
    {
        var logPath = LogPath;
        var discovery = Discover();
        if (discovery.Program is null)
        {
            var message =
                $"audio-engine isn't running at {url} and WireCopy couldn't find it to start it (looked in "
                + string.Join(", ", discovery.LookedIn)
                + "). Set its folder in Settings → Audio engine program, or start it yourself.";
            _logger.LogWarning("{Message}", message);
            return new AudioEngineStartResult(false, message);
        }

        Process? process;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            process = Process.Start(BuildStartInfo(discovery.Program, url, logPath));
            if (process is null)
            {
                return new AudioEngineStartResult(false, $"audio-engine couldn't be started from {discovery.Program.Display} — see {logPath}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "audio-engine could not be started from {Program}", discovery.Program.Executable);
            return new AudioEngineStartResult(false, $"audio-engine couldn't be started from {discovery.Program.Display} ({ex.Message}) — see {logPath}");
        }

        _logger.LogInformation(
            "Starting audio-engine: {Executable} {Arguments} in {WorkingDirectory}, output in {Log}",
            discovery.Program.Executable,
            string.Join(' ', discovery.Program.Arguments),
            discovery.Program.WorkingDirectory,
            logPath);

        using (process)
        {
            var client = _httpClientFactory.CreateClient("AudioEngine");
            var baseUrl = url.TrimEnd('/');
            var healthy = await WaitForAsync(client, baseUrl + "/healthz", HealthTimeout, process).ConfigureAwait(false);
            if (healthy is not null)
            {
                return healthy;
            }

            // Non-fatal grace: /readyz is best effort — the first request just waits a little longer.
            await WaitForAsync(client, baseUrl + "/readyz", ReadyGrace, process: null).ConfigureAwait(false);
            _logger.LogInformation("audio-engine is up at {Url}", url);
            return new AudioEngineStartResult(true, null);
        }
    }

    /// <summary>
    /// Polls <paramref name="probeUrl"/> until it answers 2xx or <paramref name="budget"/> runs out.
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="probeUrl">The URL to GET.</param>
    /// <param name="budget">How long to keep trying.</param>
    /// <param name="process">When given, an exit before success fails the attempt.</param>
    /// <returns>Null on success or when the budget ran out without a process; the failure otherwise.</returns>
    private async Task<AudioEngineStartResult?> WaitForAsync(HttpClient client, string probeUrl, TimeSpan budget, Process? process)
    {
        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            if (await ProbeOnceAsync(client, probeUrl).ConfigureAwait(false))
            {
                return null;
            }

            if (process is not null && process.HasExited)
            {
                var message = $"audio-engine exited before it became ready (exit code {process.ExitCode}) — see {LogPath}";
                _logger.LogWarning("{Message}", message);
                return new AudioEngineStartResult(false, message);
            }

            if (DateTime.UtcNow >= deadline)
            {
                if (process is null)
                {
                    return null;
                }

                var message = $"audio-engine didn't start within {(int)budget.TotalSeconds} s — see {LogPath}";
                _logger.LogWarning("{Message}", message);
                return new AudioEngineStartResult(false, message);
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the detached spawn: on Unix <c>sh -c 'trap "" HUP; exec "$@" &lt;/dev/null &gt;&gt;"$0" 2&gt;&amp;1' log exe args…</c>
    /// (the exec keeps the handle on audio-engine itself), on Windows <c>cmd /d /s /c</c> with no window.
    /// </summary>
    /// <param name="program">The program to run.</param>
    /// <param name="url">The address audio-engine must listen on.</param>
    /// <param name="logPath">The file its output is appended to.</param>
    /// <returns>The start info.</returns>
    private ProcessStartInfo BuildStartInfo(AudioEngineProgram program, string url, string logPath)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            var command = string.Join(' ', [Quote(program.Executable), .. program.Arguments.Select(Quote)]);
            psi = new ProcessStartInfo("cmd")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/d /s /c \"{command} >>\"{logPath}\" 2>&1\"",
            };
        }
        else
        {
            psi = new ProcessStartInfo("sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("trap \"\" HUP; exec \"$@\" </dev/null >>\"$0\" 2>&1");
            psi.ArgumentList.Add(logPath);
            psi.ArgumentList.Add(program.Executable);
            foreach (var arg in program.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.WorkingDirectory = program.WorkingDirectory;
        ApplyEnvironment(psi, url);
        return psi;
    }

    /// <summary>
    /// Gives the child its address, data folders and key — only where the inherited environment
    /// does not already say — and, outside Windows, the Homebrew / local bin folders on PATH.
    /// </summary>
    /// <param name="psi">The start info to amend.</param>
    /// <param name="url">The address audio-engine must listen on.</param>
    private void ApplyEnvironment(ProcessStartInfo psi, string url)
    {
        var home = Home;
        var dataDir = Path.Combine(home, "audio-engine-data");
        var voicesDir = Path.Combine(home, "audio-engine-voices");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(voicesDir);

        SetIfAbsent(psi, "ASPNETCORE_URLS", url);
        SetIfAbsent(psi, "AudioEngine__DataDir", dataDir);
        SetIfAbsent(psi, "AudioEngine__VoicesDir", voicesDir);

        var apiKey = AudioEngineSettings.Resolve(_store, _config, Environment.GetEnvironmentVariable).ApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            SetIfAbsent(psi, "ADMIN_API_KEY", apiKey);
        }
        else
        {
            SetIfAbsent(psi, "AUTH_DISABLED", "1");
        }

        if (!OperatingSystem.IsWindows())
        {
            var path = psi.Environment.TryGetValue("PATH", out var current) ? current ?? string.Empty : string.Empty;
            var parts = path.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
            parts.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin" }.Where(extra => !parts.Contains(extra, StringComparer.Ordinal)));

            psi.Environment["PATH"] = string.Join(':', parts);
        }
    }
}
