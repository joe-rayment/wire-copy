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

    /// <summary>Settings key remembering the last folder (or executable) audio-engine was seen at; never shown in Settings.</summary>
    internal const string KeyLastKnown = "AudioEngineLastKnown";

    /// <summary>The published server executable name without the Windows extension.</summary>
    private const string ServerName = "AudioEngine.Server";

    /// <summary>The project path passed to <c>dotnet run</c> in a source checkout.</summary>
    private const string SourceProject = "src/AudioEngine.Server";

    /// <summary>URLs <see cref="NoteRunning"/> has already looked at in this process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> NotedUrls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long <c>/healthz</c> may take to answer after the spawn.</summary>
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long a source checkout may take to answer: its first <c>dotnet run</c> builds it.</summary>
    private static readonly TimeSpan SourceHealthTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Folder names that hold build or test output inside a source checkout.</summary>
    private static readonly string[] BuildOutputFolders = ["bin", "obj", "tests", ".artifacts"];

    /// <summary>Extra, non-fatal time given to <c>/readyz</c> once healthy.</summary>
    private static readonly TimeSpan ReadyGrace = TimeSpan.FromSeconds(15);

    /// <summary>Pause between probes.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a not-found discovery is reused, so drawing Settings never repeats the home walk.</summary>
    private static readonly TimeSpan NotFoundCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Per-probe request budget.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How often a running engine's age is compared with its code, per URL.</summary>
    private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long a stopped engine may take to stop answering.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(40);

    private readonly IUserSettingsStore _store;
    private readonly AudioEngineConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudioEngineLauncher> _logger;

    /// <summary>Guards <see cref="_attempts"/>; never two spawns for one URL.</summary>
    private readonly object _gate = new();

    /// <summary>In-flight and finished attempts keyed by URL.</summary>
    private readonly Dictionary<string, Task<AudioEngineStartResult>> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each URL's running engine was last compared with its code; guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, DateTime> _staleChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The code change each URL's engine was last replaced for, so one change restarts it once; guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, DateTime> _replacedFor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last not-found discovery and what it saw; guarded by <see cref="_gate"/>.</summary>
    private NotFoundCache? _notFound;

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
    {
        var setting = _store.Get(SettingsCommandHandler.KeyAudioEngineProgram);
        var remembered = _store.Get(KeyLastKnown);
        lock (_gate)
        {
            if (_notFound is { } cached
                && DateTime.UtcNow - cached.At < NotFoundCacheTtl
                && string.Equals(cached.Setting, setting, StringComparison.Ordinal)
                && string.Equals(cached.Remembered, remembered, StringComparison.Ordinal))
            {
                return cached.Result;
            }
        }

        var result = Discover(
            setting,
            Environment.GetEnvironmentVariable,
            Home,
            File.Exists,
            Directory.Exists,
            OperatingSystem.IsWindows(),
            remembered,
            NearAppRoots(),
            AudioEngineSearch.ListDirectories,
            OperatingSystem.IsMacOS() ? AudioEngineSearch.SpotlightCandidates : null);

        lock (_gate)
        {
            // Only a NOT-FOUND result is cached (the home walk is the slow part); a find never is.
            _notFound = result.Program is null ? new NotFoundCache(result, setting, remembered, DateTime.UtcNow) : null;
        }

        return result;
    }

    /// <inheritdoc/>
    public AudioEngineProgram? Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var home = Home;
        var normalized = AudioEnginePathInput.Normalize(path, home, p => File.Exists(p) || Directory.Exists(p));
        return ProbePath(normalized, home, File.Exists, Directory.Exists, OperatingSystem.IsWindows());
    }

    /// <inheritdoc/>
    public void NoteRunning(string url)
    {
        if (!IsLocal(url) || !NotedUrls.TryAdd(url, 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var folder = AudioEngineSearch.RunningEngineFolder(p => ProbeFolderQuiet(p));
                if (folder is not null)
                {
                    SaveLastKnown(folder);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not note where audio-engine is running");
            }
        });
    }

    /// <inheritdoc/>
    public async Task<bool> ReplaceIfStaleAsync(string url, CancellationToken ct)
    {
        if (!IsLocal(url) || OperatingSystem.IsWindows())
        {
            return false;
        }

        lock (_gate)
        {
            if (_staleChecked.TryGetValue(url, out var last) && DateTime.UtcNow - last < StaleCheckInterval)
            {
                return false;
            }

            _staleChecked[url] = DateTime.UtcNow;
        }

        try
        {
            // Which process is behind this address: the one listening on its port when the system
            // can say, otherwise the only engine running. Several engines and no way to tell: none.
            var engines = AudioEngineSearch.RunningEngines(p => ProbeFolderQuiet(p));
            var listeners = AudioEngineSearch.ListenerPids(new Uri(url).Port);
            RunningEngine? running = null;
            if (listeners is not null)
            {
                running = engines.FirstOrDefault(e => listeners.Contains(e.ProcessId));
            }
            else if (engines.Count == 1)
            {
                running = engines[0];
            }

            if (running is null)
            {
                if (engines.Count > 1)
                {
                    _logger.LogInformation(
                        "{Count} audio-engine processes are running here and none could be tied to {Url}, so its age was not checked",
                        engines.Count,
                        url);
                }

                return false;
            }

            // Only the engine WireCopy would itself start: one from another folder is someone else's.
            var wanted = Discover().Program;
            if (wanted is null || !SameFolder(wanted.WorkingDirectory, running.Program.WorkingDirectory))
            {
                return false;
            }

            var changed = AudioEngineSearch.CodeChangedUtc(running.Program);
            if (changed is not { } codeChanged || codeChanged <= running.StartedUtc)
            {
                return false;
            }

            lock (_gate)
            {
                if (_replacedFor.TryGetValue(url, out var done) && done >= codeChanged)
                {
                    return false;
                }
            }

            var client = _httpClientFactory.CreateClient("AudioEngine");
            var baseUrl = url.TrimEnd('/');
            var busy = await ActiveJobsAsync(client, baseUrl, ct).ConfigureAwait(false);
            if (busy is null)
            {
                _logger.LogInformation(
                    "audio-engine at {Url} is older than its code (started {Started:u}, code changed {Changed:u}) but would not say what it is doing - leaving it running",
                    url,
                    running.StartedUtc,
                    codeChanged);
                return false;
            }

            if (busy > 0)
            {
                _logger.LogInformation(
                    "audio-engine at {Url} is older than its code (started {Started:u}, code changed {Changed:u}) but is busy with a job - leaving it running",
                    url,
                    running.StartedUtc,
                    codeChanged);
                return false;
            }

            _logger.LogInformation(
                "audio-engine at {Url} was started {Started:u}, before its code last changed ({Changed:u}) - restarting it so this run uses the current build",
                url,
                running.StartedUtc,
                codeChanged);
            lock (_gate)
            {
                _replacedFor[url] = codeChanged;
            }

            if (!await StopAsync(client, baseUrl, running, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("The older audio-engine (process {Pid}) could not be stopped; carrying on with it", running.ProcessId);
                return false;
            }

            var start = await StartAndWaitAsync(url, ct).ConfigureAwait(false);
            if (!start.Ready)
            {
                _logger.LogWarning("audio-engine did not come back after the restart: {Message}", start.Message);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not tell whether the audio-engine at {Url} is older than its code", url);
            return false;
        }
    }

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
            _notFound = null;
            foreach (var key in _attempts.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList())
            {
                _attempts.Remove(key);
            }
        }
    }

    /// <summary>
    /// Pure discovery, first hit wins: the <c>AudioEngineProgram</c> setting (a file is the executable, a
    /// folder is probed), <c>AUDIO_ENGINE_HOME</c>, the remembered location, <c>&lt;home&gt;/audio-engine</c>,
    /// <c>audio-engine</c> beside the app or working folder, a bounded walk of the home folder, and last any
    /// extra candidates (macOS Spotlight). A folder holding <c>AudioEngine.Server</c> is a published build;
    /// one holding <c>dotnet</c> and <c>src/AudioEngine.Server</c> is a source checkout run with <c>dotnet run</c>.
    /// </summary>
    /// <param name="programSetting">The saved "Audio engine program" setting; null or blank when none.</param>
    /// <param name="getEnv">Reads an environment variable.</param>
    /// <param name="home">The user's home folder.</param>
    /// <param name="fileExists">Whether a path is an existing file.</param>
    /// <param name="dirExists">Whether a path is an existing folder.</param>
    /// <param name="isWindows">True to look for <c>.exe</c> names.</param>
    /// <param name="rememberedLocation">The remembered folder or executable; null when none.</param>
    /// <param name="nearAppRoots">Folders whose ancestors are checked for an <c>audio-engine</c> child; null for none.</param>
    /// <param name="listDirs">Lists the subfolders of a folder for the home walk; null skips the walk.</param>
    /// <param name="extraCandidates">Late candidate executables (e.g. Spotlight results); null for none.</param>
    /// <returns>The program (null when none) and the places a person would recognise, in search order.</returns>
    internal static AudioEngineDiscovery Discover(
        string? programSetting,
        Func<string, string?> getEnv,
        string home,
        Func<string, bool> fileExists,
        Func<string, bool> dirExists,
        bool isWindows,
        string? rememberedLocation = null,
        IEnumerable<string>? nearAppRoots = null,
        Func<string, IEnumerable<string>>? listDirs = null,
        Func<IEnumerable<string>>? extraCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(dirExists);
        var lookedIn = new List<string>();
        var program = DiscoverCore(
            programSetting, getEnv, home, fileExists, dirExists, isWindows, rememberedLocation, nearAppRoots, listDirs, extraCandidates, lookedIn);
        return new AudioEngineDiscovery(program, lookedIn.Distinct(StringComparer.Ordinal).ToList());
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

    /// <summary>The ordered search behind <see cref="Discover()"/>.</summary>
    /// <param name="programSetting">The saved setting.</param>
    /// <param name="getEnv">Reads an environment variable.</param>
    /// <param name="home">The user's home folder.</param>
    /// <param name="fileExists">Whether a path is a file.</param>
    /// <param name="dirExists">Whether a path is a folder.</param>
    /// <param name="isWindows">True to look for <c>.exe</c> names.</param>
    /// <param name="remembered">The remembered location.</param>
    /// <param name="nearAppRoots">Start folders for the ancestor check.</param>
    /// <param name="listDirs">Subfolder lister for the home walk.</param>
    /// <param name="extraCandidates">Late candidate executables.</param>
    /// <param name="lookedIn">Receives every place examined.</param>
    /// <returns>The program found, or null.</returns>
    private static AudioEngineProgram? DiscoverCore(
        string? programSetting,
        Func<string, string?> getEnv,
        string home,
        Func<string, bool> fileExists,
        Func<string, bool> dirExists,
        bool isWindows,
        string? remembered,
        IEnumerable<string>? nearAppRoots,
        Func<string, IEnumerable<string>>? listDirs,
        Func<IEnumerable<string>>? extraCandidates,
        List<string> lookedIn)
    {
        bool Exists(string p) => fileExists(p) || dirExists(p);

        // (a) The explicit setting: a file is the executable, a folder is probed.
        var setting = programSetting?.Trim();
        if (!string.IsNullOrEmpty(setting))
        {
            var path = AudioEnginePathInput.Normalize(setting, home, Exists);
            if (fileExists(path))
            {
                lookedIn.Add(Shorten(path, home));
                if (CheckoutOfBuildOutput(path, dirExists) is { } checkout)
                {
                    // A build or test binary inside a source checkout is never run: use the checkout itself.
                    var viaCheckout = ProbeFolder(checkout, home, fileExists, dirExists, isWindows, lookedIn);
                    if (viaCheckout is not null)
                    {
                        return viaCheckout;
                    }
                }
                else
                {
                    var folder = Path.GetDirectoryName(path) ?? path;
                    return new AudioEngineProgram(path, [], folder, Shorten(path, home));
                }
            }

            if (dirExists(path))
            {
                var found = ProbeFolder(path, home, fileExists, dirExists, isWindows, lookedIn);
                if (found is not null)
                {
                    return found;
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
            var found = ProbeFolder(AudioEnginePathInput.Normalize(envHome, home, Exists), home, fileExists, dirExists, isWindows, lookedIn);
            if (found is not null)
            {
                return found;
            }
        }

        // (c) The remembered location.
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            var path = AudioEnginePathInput.Normalize(remembered, home, Exists);
            lookedIn.Add(Shorten(path, home));
            var found = ProbePath(path, home, fileExists, dirExists, isWindows, new List<string>());
            if (found is not null)
            {
                return found;
            }
        }

        // (d) ~/audio-engine.
        var defaultFolder = Path.Combine(home, "audio-engine");
        var atHome = ProbeFolder(defaultFolder, home, fileExists, dirExists, isWindows, lookedIn);
        if (atHome is not null)
        {
            return atHome;
        }

        // (e) audio-engine beside the app or the working folder.
        var nearApp = false;
        foreach (var candidate in AncestorFolders(nearAppRoots, 8).Select(a => Path.Combine(a, "audio-engine")).Distinct(StringComparer.Ordinal))
        {
            if (candidate.Equals(defaultFolder, StringComparison.Ordinal))
            {
                continue;
            }

            nearApp = true;
            var found = ProbeFolder(candidate, home, fileExists, dirExists, isWindows, new List<string>());
            if (found is not null)
            {
                return found;
            }
        }

        if (nearApp)
        {
            lookedIn.Add("next to WireCopy");
        }

        // (f) A bounded walk of the home folder.
        if (listDirs is not null && !string.IsNullOrEmpty(home))
        {
            lookedIn.Add("folders under ~");
            var found = AudioEngineSearch.WalkFor(
                home,
                listDirs,
                folder => ProbeFolder(folder, home, fileExists, dirExists, isWindows, new List<string>()),
                AudioEngineSearch.WalkBudget,
                AudioEngineSearch.WalkMaxListings);
            if (found is not null)
            {
                return found;
            }
        }

        // (g) Extra candidates (macOS Spotlight): the first of up to five ancestors that probes OK.
        if (extraCandidates is not null)
        {
            lookedIn.Add("Spotlight");
            foreach (var hit in extraCandidates())
            {
                var folder = Path.GetDirectoryName(hit);
                for (var level = 0; level < 8 && !string.IsNullOrEmpty(folder); level++)
                {
                    var found = ProbeFolder(folder, home, fileExists, dirExists, isWindows, new List<string>());
                    if (found is not null)
                    {
                        return found;
                    }

                    folder = Path.GetDirectoryName(folder);
                }
            }
        }

        return null;
    }

    /// <summary>Each start folder and its ancestors, nearest first, never the filesystem root.</summary>
    /// <param name="starts">The start folders; null for none.</param>
    /// <param name="maxLevels">Folders taken per start, the start included.</param>
    /// <returns>Distinct folders.</returns>
    private static IEnumerable<string> AncestorFolders(IEnumerable<string>? starts, int maxLevels)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in starts ?? [])
        {
            var folder = string.IsNullOrWhiteSpace(start) ? null : start.TrimEnd('/', '\\');
            for (var level = 0; level < maxLevels && !string.IsNullOrEmpty(folder); level++)
            {
                var parent = Path.GetDirectoryName(folder);
                if (parent is null)
                {
                    break; // folder is the filesystem root
                }

                if (seen.Add(folder))
                {
                    yield return folder;
                }

                folder = parent;
            }
        }
    }

    /// <summary>The app's own folder and the working folder, where a bundled or sibling audio-engine may sit.</summary>
    /// <returns>The start folders.</returns>
    private static IEnumerable<string> NearAppRoots()
    {
        var roots = new List<string>();
        try
        {
            roots.Add(AppContext.BaseDirectory);
            roots.Add(Directory.GetCurrentDirectory());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Use whatever was gathered.
        }

        return roots;
    }

    /// <summary>A file named like the server is the program; a folder is probed, then its <c>audio-engine</c> child and ancestors.</summary>
    /// <param name="path">An absolute, normalized path.</param>
    /// <param name="home">The user's home folder (for display).</param>
    /// <param name="fileExists">Whether a path is a file.</param>
    /// <param name="dirExists">Whether a path is a folder.</param>
    /// <param name="isWindows">True to look for <c>.exe</c> names.</param>
    /// <param name="lookedIn">Receives folders examined; null for none.</param>
    /// <returns>The program, or null.</returns>
    private static AudioEngineProgram? ProbePath(
        string path,
        string home,
        Func<string, bool> fileExists,
        Func<string, bool> dirExists,
        bool isWindows,
        List<string>? lookedIn = null)
    {
        lookedIn ??= [];
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (fileExists(path))
        {
            // "AudioEngine.Server" has an "extension" on Unix, so compare the full name too.
            var isServer = Path.GetFileName(path).Equals(ServerName, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(path).Equals(ServerName, StringComparison.OrdinalIgnoreCase);
            if (!isServer)
            {
                return null;
            }

            if (CheckoutOfBuildOutput(path, dirExists) is { } checkout)
            {
                return ProbeFolder(checkout, home, fileExists, dirExists, isWindows, lookedIn);
            }

            return new AudioEngineProgram(path, [], Path.GetDirectoryName(path) ?? path, Shorten(path, home));
        }

        if (!dirExists(path))
        {
            lookedIn.Add(Shorten(path, home));
            return null;
        }

        var direct = ProbeFolder(path, home, fileExists, dirExists, isWindows, lookedIn)
            ?? ProbeFolder(Path.Combine(path, "audio-engine"), home, fileExists, dirExists, isWindows, lookedIn);
        if (direct is not null)
        {
            return direct;
        }

        var folder = Path.GetDirectoryName(path);
        for (var level = 0; level < 4 && !string.IsNullOrEmpty(folder); level++)
        {
            var found = ProbeFolder(folder, home, fileExists, dirExists, isWindows, lookedIn);
            if (found is not null)
            {
                return found;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    /// <summary>Probes a folder against the real file system without recording where it looked.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The program, or null.</returns>
    private static AudioEngineProgram? ProbeFolderQuiet(string folder)
        => ProbeFolder(folder, Home, File.Exists, Directory.Exists, OperatingSystem.IsWindows(), []);

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

        // A source checkout wins over any binary inside it.
        var dotnet = Path.Combine(folder, isWindows ? "dotnet.exe" : "dotnet");
        if (fileExists(dotnet) && dirExists(Path.Combine(folder, "src", ServerName)))
        {
            return new AudioEngineProgram(dotnet, ["run", "--project", SourceProject, "-c", "Release"], folder, display);
        }

        var published = Path.Combine(folder, isWindows ? ServerName + ".exe" : ServerName);
        if (fileExists(published) && CheckoutOfBuildOutput(published, dirExists) is null)
        {
            return new AudioEngineProgram(published, [], folder, display);
        }

        return null;
    }

    /// <summary>Tells whether a path is build or test output inside a source checkout.</summary>
    /// <param name="path">A file or folder path.</param>
    /// <param name="dirExists">Whether a path is an existing folder.</param>
    /// <returns>The checkout's root folder when <paramref name="path"/> lies in its bin, obj, tests or .artifacts; otherwise null.</returns>
    private static string? CheckoutOfBuildOutput(string path, Func<string, bool> dirExists)
    {
        var folder = Path.GetDirectoryName(path);
        var insideOutput = false;
        for (var level = 0; level < 12 && !string.IsNullOrEmpty(folder); level++)
        {
            if (dirExists(Path.Combine(folder, "src", ServerName)))
            {
                return insideOutput ? folder : null;
            }

            insideOutput |= BuildOutputFolders.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase);
            folder = Path.GetDirectoryName(folder);
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
    /// <summary>Whether two folder paths name the same place.</summary>
    /// <param name="a">One path.</param>
    /// <param name="b">The other path.</param>
    /// <returns>True when they are the same folder.</returns>
    private static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Remembers a program's location, but only a place that probes OK.</summary>
    /// <param name="program">The program just started.</param>
    private void Remember(AudioEngineProgram program)
    {
        try
        {
            string? candidate = null;
            if (ProbeFolderQuiet(program.WorkingDirectory) is not null)
            {
                candidate = program.WorkingDirectory;
            }
            else if (program.Arguments.Count == 0 && ProbePath(program.Executable, Home, File.Exists, Directory.Exists, OperatingSystem.IsWindows()) is not null)
            {
                candidate = program.Executable;
            }

            if (candidate is not null)
            {
                SaveLastKnown(candidate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remember where audio-engine was started from");
        }
    }

    /// <summary>Writes the breadcrumb when it changed.</summary>
    /// <param name="location">A folder or executable that probed OK.</param>
    private void SaveLastKnown(string location)
    {
        lock (_gate)
        {
            _notFound = null;
        }

        if (!string.Equals(_store.Get(KeyLastKnown), location, StringComparison.Ordinal))
        {
            _store.Set(KeyLastKnown, location);
        }
    }

    /// <summary>Counts the engine's pending and running jobs.</summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="baseUrl">The audio-engine base address without a trailing slash.</param>
    /// <param name="ct">Stops waiting.</param>
    /// <returns>How many jobs are waiting or in progress; null when the engine would not say.</returns>
    private async Task<int?> ActiveJobsAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        var total = 0;
        foreach (var status in new[] { "running", "pending" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ProbeTimeout);
                using var request = NewEngineRequest(HttpMethod.Get, $"{baseUrl}/v1/jobs?status={status}&limit=1");
                using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("items", out var items)
                    || items.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return null;
                }

                total += items.GetArrayLength();
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return null;
            }
        }

        return total;
    }

    /// <summary>
    /// Stops a running engine: its own shutdown call, then its <c>run stop</c> script (builds from
    /// before the shutdown call existed), then the process itself; waits until it stops answering.
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="baseUrl">The audio-engine base address without a trailing slash.</param>
    /// <param name="running">The process to stop.</param>
    /// <param name="ct">Stops waiting.</param>
    /// <returns>True once nothing answers at the address.</returns>
    private async Task<bool> StopAsync(HttpClient client, string baseUrl, RunningEngine running, CancellationToken ct)
    {
        var how = "its shutdown call";
        var asked = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            using var request = NewEngineRequest(HttpMethod.Post, baseUrl + "/v1/shutdown");
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            asked = response.IsSuccessStatusCode;
            if ((int)response.StatusCode == 409)
            {
                // A service manager starts it again by itself; stopping it here would not last.
                _logger.LogInformation("audio-engine says it is restarted automatically when it exits; not stopping it");
                return false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            asked = false;
        }

        if (!asked)
        {
            var script = Path.Combine(running.Program.WorkingDirectory, "run");
            if (File.Exists(script))
            {
                how = "its run stop script";
                asked = await RunStopScriptAsync(script, new Uri(baseUrl).Port, running.Program.WorkingDirectory, ct).ConfigureAwait(false);
            }
        }

        if (!asked)
        {
            how = "ending the process";
            try
            {
                using var process = Process.GetProcessById(running.ProcessId);
                process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "audio-engine process {Pid} could not be ended", running.ProcessId);
            }
        }

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < StopTimeout)
        {
            if (!await ProbeOnceAsync(client, baseUrl + "/healthz").ConfigureAwait(false))
            {
                _logger.LogInformation("The older audio-engine (process {Pid}) was stopped by {How}", running.ProcessId, how);
                return true;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Runs the engine checkout's own <c>run stop &lt;port&gt;</c>.</summary>
    /// <param name="script">The script path.</param>
    /// <param name="port">The port the engine listens on.</param>
    /// <param name="workingDirectory">The checkout folder.</param>
    /// <param name="ct">Stops waiting.</param>
    /// <returns>True when the script reported success.</returns>
    private async Task<bool> RunStopScriptAsync(string script, int port, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("stop");
            psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StopTimeout);
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var said = ((await output.ConfigureAwait(false)) + " " + (await error.ConfigureAwait(false))).Trim();
            _logger.LogInformation("audio-engine's run stop said: {Output:l} (exit code {Code})", said, process.ExitCode);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "audio-engine's run stop script could not be run");
            return false;
        }
    }

    /// <summary>A request to the engine carrying its API key when one is configured.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="requestUrl">The absolute URL.</param>
    /// <returns>The request.</returns>
    private HttpRequestMessage NewEngineRequest(HttpMethod method, string requestUrl)
    {
        var request = new HttpRequestMessage(method, requestUrl);
        var apiKey = AudioEngineSettings.Resolve(_store, _config, Environment.GetEnvironmentVariable).ApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
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
            _logger.LogInformation("audio-engine was not found; looked in: {Places}", string.Join(", ", discovery.LookedIn));
            var places = discovery.LookedIn.Count <= 1
                ? string.Join(string.Empty, discovery.LookedIn)
                : string.Join(", ", discovery.LookedIn.Take(discovery.LookedIn.Count - 1)) + " and " + discovery.LookedIn[^1];

            var message =
                $"audio-engine isn't running at {url} and WireCopy couldn't find it on this computer to start it (looked in {places}). "
                + "audio-engine is the separate program that reads articles aloud.";
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
            var timeout = discovery.Program.Arguments.Count > 0 ? SourceHealthTimeout : HealthTimeout;
            var healthy = await WaitForAsync(client, baseUrl + "/healthz", timeout, process).ConfigureAwait(false);
            if (healthy is not null)
            {
                return healthy with { StartedFrom = discovery.Program.Display };
            }

            // Non-fatal grace: /readyz is best effort — the first request just waits a little longer.
            await WaitForAsync(client, baseUrl + "/readyz", ReadyGrace, process: null).ConfigureAwait(false);
            _logger.LogInformation("audio-engine is up at {Url}", url);
            Remember(discovery.Program);
            return new AudioEngineStartResult(true, null) { StartedFrom = discovery.Program.Display };
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

    /// <summary>A cached not-found discovery with the inputs it was computed from.</summary>
    /// <param name="Result">The not-found result.</param>
    /// <param name="Setting">The program setting it saw.</param>
    /// <param name="Remembered">The remembered location it saw.</param>
    /// <param name="At">When it was computed (UTC).</param>
    private sealed record NotFoundCache(AudioEngineDiscovery Result, string? Setting, string? Remembered, DateTime At);
}
