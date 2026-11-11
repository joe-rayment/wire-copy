# Setup

## Prerequisites

- **FFmpeg** — required for podcast / audio assembly
  ```bash
  # macOS
  brew install ffmpeg
  # Debian/Ubuntu
  sudo apt-get install ffmpeg
  ```
- **Chromium** — Patchright will download its own browser the first time you run WireCopy. On Linux, the host needs the usual Chromium dependencies (the [`Dockerfile`](../Dockerfile) lists them).

You do **not** need a system-wide .NET install. The `./dotnet` wrapper at the repo root bootstraps a workspace-local .NET 10 SDK into `./.dotnet/` (~600 MB, gitignored) on first invocation via `scripts/bootstrap-dotnet.sh`.

## Build and run

```bash
git clone https://github.com/joe-rayment/wire-copy.git
cd wire-copy
./run
```

`./run` builds the app (Release), installs the Electron desktop shell on first run (a one-time `npm install` that downloads Electron), and launches it. It needs **Node.js** on your PATH. On Linux with no display it starts a virtual display (Xvfb, via `xvfb-run`; install the `xvfb` package) automatically, so the browser stays headful, never headless. Running `./dotnet run --project src/WireCopy.API` directly is refused: the interactive app only runs inside the shell.

The first `./dotnet` call (made by `./run`) downloads the .NET 10 SDK. Subsequent calls reuse it. The first app run launches the bookmark grid. Press `?` from any screen for a help overlay.

> **Prefer plain `dotnet`?** Add the repo root to your PATH: `export PATH="$PWD:$PATH"`. The wrapper still forwards to the vendored SDK so you don't pick up a stale system install.

## CLI

```bash
# Default — open the launcher in the desktop shell
./run

# Unattended verbs (no shell, no TUI)
./run run-recipe <name>
./run resolve-section <args>
```

`./run` takes no other arguments. All interactive navigation (including opening a URL) happens inside the app.

## Credentials

Both keys are optional — WireCopy runs as a browser without them. Podcast narration and publishing are configured in audio-engine, not here (see below).

| Setting              | Purpose                                              |
|----------------------|------------------------------------------------------|
| `OpenAiTts:ApiKey`   | OpenAI key for AI page layout and article extraction |
| `Anthropic:ApiKey`   | Page-structure analysis                              |

You can provide them in any of three ways. They're checked in this order — later sources override earlier ones:

1. `appsettings.json` (don't put real secrets here; gitignored variants like `appsettings.Development.json` are fine).
2. Environment variables, using `__` as the section separator:
   ```bash
   export OpenAiTts__ApiKey="sk-..."
   export Anthropic__ApiKey="sk-ant-..."
   ```
3. `dotnet user-secrets` — recommended for local development:
   ```bash
   cd src/WireCopy.API
   dotnet user-secrets init
   dotnet user-secrets set "OpenAiTts:ApiKey" "sk-..."
   dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
   ```
4. A local `secrets.json` (gitignored — see [`secrets.json.example`](../secrets.json.example)).

## Podcasts: set up audio-engine

Narration and podcast publishing are done by [audio-engine](https://github.com/joe-rayment/audio-engine), a separate local service. Configure them there:

1. **Start it:** in its checkout run `./run` (serves http://127.0.0.1:8080, no API key; data in `~/audio-engine-data`; needs `ffmpeg` and `uv`). Wire Copy also finds a checkout itself (`~/audio-engine`, `AUDIO_ENGINE_HOME`, or a search of your home folder) and starts it.
2. **Narration:** in audio-engine's **Settings** pick the engine (Chatterbox, local and free, is the default; or OpenAI) and the voice. The first Chatterbox run downloads model weights (a few GB).
3. **Publishing (optional):** follow audio-engine's "Private podcast feeds" to set up the bucket, create a feed in its **Feeds** tab, pick it as the **Default feed**, and click **Test publishing**. Subscribe in a podcast app with the feed's link.

Wire Copy keeps three settings for it (`c` on the launcher): the URL (`AudioEngineUrl`), the optional API key (`AudioEngineApiKey`, asked only if audio-engine answers 401), and the program or folder used to launch it (`AudioEngineProgram`). Then press `p` on a reading list. With a Default feed the episode is published and you get the subscribe link; without one it is saved on this computer only.

## Site logins (paywalled content)

For sites requiring authentication, paste a session cookie when prompted on first navigation. The cookie is encrypted with ASP.NET DataProtection and stored in the local SQLite database. Details in [docs/cookie-encryption.md](cookie-encryption.md).

## Troubleshooting

### Patchright can't find Chromium

Patchright downloads its browser on first run. If that fails, try:

```bash
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium
```

### FFmpeg not found

Audio generation will fail at the assembly step. Install via your package manager (see Prerequisites).

### Audio costs

Narration cost and caching are handled by audio-engine; Wire Copy shows its estimate before an expensive run. The `Anthropic` configuration has a `MaxBudgetUsd` per session in `appsettings.json`.

## Security notes

- `secrets.json`, `appsettings.*.json` (except the base `appsettings.json`), `cookies.json`, `*.db*`, and `keys/` are all gitignored.
- Don't commit real credentials. CI / public builds should source secrets from environment variables or vault integrations.
