# Setup

## Prerequisites

- **.NET 9.0 SDK** — [download](https://dotnet.microsoft.com/download)
- **FFmpeg** — required for podcast / audio assembly
  ```bash
  # macOS
  brew install ffmpeg
  # Debian/Ubuntu
  sudo apt-get install ffmpeg
  ```
- **Chromium** — Patchright will download its own browser the first time you run TermReader. On Linux, the host needs the usual Chromium dependencies (the [`Dockerfile`](../Dockerfile) lists them).

## Build and run

```bash
git clone https://github.com/joe-rayment/wire-copy.git
cd wire-copy
dotnet restore
dotnet build
dotnet run --project src/TermReader.API
```

The first run launches the bookmark grid. Press `?` from any screen for a help overlay.

## CLI

```bash
# Default — open the launcher
dotnet run --project src/TermReader.API

# Open a specific URL directly
dotnet run --project src/TermReader.API -- browse https://news.ycombinator.com
```

The single `browse` verb takes an optional URL argument. All other navigation happens inside the TUI.

## Credentials

Two API keys unlock podcast generation. Both are optional — TermReader runs as a browser without them.

| Setting              | Purpose                       |
|----------------------|-------------------------------|
| `OpenAiTts:ApiKey`   | Text-to-speech for podcasts   |
| `Anthropic:ApiKey`   | Page-structure analysis       |

You can provide them in any of three ways. They're checked in this order — later sources override earlier ones:

1. `appsettings.json` (don't put real secrets here; gitignored variants like `appsettings.Development.json` are fine).
2. Environment variables, using `__` as the section separator:
   ```bash
   export OpenAiTts__ApiKey="sk-..."
   export Anthropic__ApiKey="sk-ant-..."
   ```
3. `dotnet user-secrets` — recommended for local development:
   ```bash
   cd src/TermReader.API
   dotnet user-secrets init
   dotnet user-secrets set "OpenAiTts:ApiKey" "sk-..."
   dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
   ```
4. A local `secrets.json` (gitignored — see [`secrets.json.example`](../secrets.json.example)).

Optional: a Google Cloud Storage service account JSON for publishing podcast feeds. See [docs/data-storage.md](data-storage.md).

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

Both `OpenAiTts` and `Anthropic` configurations have a `MaxBudgetUsd` per session in `appsettings.json`. The defaults (`$1.00` and `$0.10` respectively) are conservative; bump them if you're generating long podcasts. Generated audio is cached on disk by content + voice + model so re-runs are free.

## Security notes

- `secrets.json`, `appsettings.*.json` (except the base `appsettings.json`), `cookies.json`, `*.db*`, and `keys/` are all gitignored.
- Don't commit real credentials. CI / public builds should source secrets from environment variables or vault integrations.
- See [SECURITY.md](../SECURITY.md) for vulnerability reporting.
