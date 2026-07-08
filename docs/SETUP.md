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
./dotnet restore
./dotnet build
./dotnet run --project src/WireCopy.API
```

The first `./dotnet` call downloads the .NET 10 SDK. Subsequent calls reuse it. The first app run launches the bookmark grid. Press `?` from any screen for a help overlay.

> **Prefer plain `dotnet`?** Add the repo root to your PATH: `export PATH="$PWD:$PATH"`. The wrapper still forwards to the vendored SDK so you don't pick up a stale system install.

## CLI

```bash
# Default — open the launcher
./dotnet run --project src/WireCopy.API

# Open a specific URL directly
./dotnet run --project src/WireCopy.API -- browse https://news.ycombinator.com
```

The single `browse` verb takes an optional URL argument. All other navigation happens inside the TUI.

## Credentials

Two API keys unlock podcast generation. Both are optional — WireCopy runs as a browser without them.

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
   cd src/WireCopy.API
   dotnet user-secrets init
   dotnet user-secrets set "OpenAiTts:ApiKey" "sk-..."
   dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
   ```
4. A local `secrets.json` (gitignored — see [`secrets.json.example`](../secrets.json.example)).

Optional: a Google Cloud Storage service account JSON for publishing podcast feeds. See [docs/data-storage.md](data-storage.md).

## Local narration (Chatterbox) — no API key

Podcast narration can run entirely on your machine with the open-source **Chatterbox** engine instead of OpenAI TTS. No key, no per-run cost, nothing leaves the machine.

1. **Install `uv`** (the only prerequisite):
   ```bash
   curl -LsSf https://astral.sh/uv/install.sh | sh
   ```
   Wire Copy runs the narration worker with `uv run` and manages the Python environment itself.
2. **Switch engines:** press `c` on the launcher → **Narration engine** → pick **Chatterbox (local)**.
3. **(Optional) clone a voice:** drop a ~10-second clean, single-speaker clip (wav/mp3/flac/m4a/ogg) into `voices/` (gitignored), then Settings → **Voice sample** → type the filename. Without a sample, Chatterbox uses its built-in voice. Tone is set by the sample plus the **Expressiveness** knob — Chatterbox takes no text style instructions (those apply to OpenAI only).
4. **Test it:** Settings → **Local engine** row → Enter. The first run downloads the model weights (one-time, a few GB); after that it generates a short clip and plays it so you can hear the voice.

Then press `p` on a reading list as usual — generation is local and free.

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

Both `OpenAiTts` and `Anthropic` configurations have a `MaxBudgetUsd` per session in `appsettings.json`. The defaults (`$1.00` and `$0.10` respectively) are conservative; bump them if you're generating long podcasts. Generated audio is cached on disk by content + engine + voice/sample + model so re-runs are free. (The **Chatterbox** local engine is always `$0.00` — its cost is time, not money.)

### Local narration fails with "'NoneType' object is not callable"

Chatterbox's Perth watermarker imports `pkg_resources`, which `setuptools` 81+ removed. Wire Copy pins `setuptools<81` in the worker's `uv` launch line (`Chatterbox:UvArgs` in `appsettings.json`) to avoid this — if you've overridden `UvArgs`, keep the `--with "setuptools<81"` fragment.

## Security notes

- `secrets.json`, `appsettings.*.json` (except the base `appsettings.json`), `cookies.json`, `*.db*`, and `keys/` are all gitignored.
- Don't commit real credentials. CI / public builds should source secrets from environment variables or vault integrations.
