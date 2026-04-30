# WireCopy Architecture

This document describes the layout and design conventions of the WireCopy codebase. It is reference material for contributors; for a feature overview see the top-level [README](../README.md).

## Overview

WireCopy follows **Clean Architecture** with strict dependency direction: `API → Infrastructure → Persistence → Application → Domain`. Inner layers know nothing about outer layers; concrete implementations live at the edges.

```
┌──────────────────────────────────────────────────────────┐
│  WireCopy.API  (console entry point, DI composition)    │
└──────────────────┬───────────────────────────────────────┘
                   │
┌──────────────────▼───────────────────────────────────────┐
│  WireCopy.Infrastructure                                │
│    Browser/   Patchright automation, link extraction,      │
│               reader-view rendering, terminal UI shell     │
│    Podcast/   OpenAI TTS, FFmpeg, M4B chapter markers,     │
│               GCS feed publishing                          │
│    Configuration/  Options classes + validators            │
└──────────────────┬───────────────────────────────────────┘
                   │
┌──────────────────▼───────────────────────────────────────┐
│  WireCopy.Persistence                                   │
│    AppDbContext (EF Core + SQLite)                         │
│    Repositories (Bookmark, Collection, SiteCredential)     │
│    UnitOfWork                                              │
└──────────────────┬───────────────────────────────────────┘
                   │
┌──────────────────▼───────────────────────────────────────┐
│  WireCopy.Application                                   │
│    Interfaces — service and repository contracts           │
│    DTOs                                                    │
└──────────────────┬───────────────────────────────────────┘
                   │
┌──────────────────▼───────────────────────────────────────┐
│  WireCopy.Domain                                        │
│    Entities — Bookmarks, Browser, Collections, Credentials │
│    ValueObjects, Enums                                     │
└──────────────────────────────────────────────────────────┘
```

## Projects

### `WireCopy.Domain`

Pure C# — no framework dependencies. Contains entities and value objects that model the problem space.

- `Entities/Bookmarks/` — saved URLs and groupings
- `Entities/Browser/` — page state (`Page`, `ReadableContent`, link classifications)
- `Entities/Collections/` — reading lists with read/unread tracking
- `Entities/Credentials/` — encrypted site credentials

Rules:
- No references to other projects.
- No external packages beyond the BCL.

### `WireCopy.Application`

Defines the **interfaces** that outer layers implement. No logic.

- `Interfaces/` — `IBookmarkService`, `ICollectionService`, `ICookieEncryptionService`, `ITtsService`, `IUnitOfWork`, `IRepository<T>`, etc.
- `DTOs/` — transfer types between layers.

Rules:
- Only references `WireCopy.Domain`.

### `WireCopy.Persistence`

EF Core implementation of repositories and unit of work, backed by SQLite.

- `AppDbContext` — model and migrations entry point.
- Repositories — concrete implementations of `IBookmarkRepository`, `ICollectionRepository`, `ISiteCredentialRepository`.
- `UnitOfWork` — wraps `SaveChangesAsync` and transaction control.

Rules:
- References `WireCopy.Application` and `WireCopy.Domain`.
- Database migrations live here.

### `WireCopy.Infrastructure`

Everything that talks to the outside world.

- `Browser/` — Patchright (patched Playwright) automation, navigation, page caching, the Helix-style input router, and the Terminal.Gui shell under `Browser/UI/`.
- `Podcast/` — `OpenAiTtsService`, `M4bAudioAssembler` (FFmpeg), `PodcastFeedGenerator`, `GcsStorageClient` for optional cloud publishing.
- `Configuration/` — strongly typed options classes (`OpenAiTtsConfiguration`, `AnthropicConfiguration`, `BrowserConfiguration`, `PodcastConfiguration`, `GcsConfiguration`) and corresponding `IValidateOptions<T>` validators.

Rules:
- References `WireCopy.Application`, `WireCopy.Persistence`, `WireCopy.Domain`.

### `WireCopy.API`

Composition root and CLI entry point. Wires up DI, configuration, logging, and dispatches commands.

- `Program.cs` — host setup.
- `CommandOptions.cs` — CLI parsing via CommandLineParser.

## Patterns in use

### Repository + Unit of Work

Repositories expose entity-level CRUD; `IUnitOfWork.SaveChangesAsync` commits the EF Core change tracker. Services depend on `I…Repository` and `IUnitOfWork` together rather than touching `DbContext` directly.

### Options pattern with validation

Each external integration has a configuration class bound from `appsettings.json` with an `IValidateOptions<T>` validator registered in DI. Misconfigured keys fail fast at startup rather than at first call.

### Decorator composition

Where appropriate, behavior is layered via decorators (e.g., a budget-aware wrapper around the TTS service) rather than baked into the concrete implementation. This keeps the inner class focused and the cross-cutting concern testable in isolation.

### Caching

- `ITtsAudioCache` — disk-backed cache of generated audio chunks, keyed by content + voice + model.
- `IArticleContentCache` — per-URL extraction cache to avoid re-parsing on back/forward navigation.
- In-memory page cache inside `Browser/Cache/` for fast tab switching.

## Configuration & secrets

Configuration is loaded from `appsettings.json`. Secrets must come from one of:

- `dotnet user-secrets` (preferred for local development),
- environment variables (preferred in Docker / CI; `__` is the section separator, e.g. `OpenAiTts__ApiKey`),
- a local, gitignored `secrets.json` (see `secrets.json.example`).

Cookie material and site credentials are encrypted with ASP.NET DataProtection — see [docs/cookie-encryption.md](cookie-encryption.md).

## Logging

Serilog is configured in `Program.cs` and `appsettings.json`. Logs roll daily under `logs/wirecopy-*.log`. Application code takes `ILogger<T>` via DI; never use `Console.WriteLine` for diagnostic output (terminal-control ANSI sequences in `Browser/UI/` are an intentional exception).

## Testing

Tests live in `tests/WireCopy.Tests/`, organized by feature area (`Browser/`, `Podcast/`, `Bookmarks/`, `Collections/`, etc.). See [TESTING.md](TESTING.md) for how to run them and the project's testing philosophy — in particular, why we avoid mocking layers that would actually fail in production.

## Dependency direction — quick reference

| If you're adding…              | Put it in…                        |
|--------------------------------|-----------------------------------|
| A new entity                   | `WireCopy.Domain`               |
| A new service contract         | `WireCopy.Application`          |
| A new EF Core mapping or repo  | `WireCopy.Persistence`          |
| A new external integration     | `WireCopy.Infrastructure`       |
| A new CLI flag or command      | `WireCopy.API` + handler in Infrastructure |

If you can't decide, the rule of thumb is: it goes one layer further out than what it depends on, and one layer further in than what depends on it.
