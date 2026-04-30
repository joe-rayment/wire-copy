# Testing

## Quick reference

```bash
./scripts/test.sh              # Fast unit tests (~15s) — recommended for iteration
./scripts/test.sh --all        # Full suite incl. integration (~90s)
./scripts/test.sh --filter "PageLoaderTests"   # Single test class
./scripts/test.sh --browser    # Browser test group
./scripts/test.sh --podcast    # Podcast test group
```

If `test.sh` is unavailable (Windows, etc.):

```bash
dotnet test --filter Category=Unit         # Unit tests only
dotnet test --filter Category=Integration  # Integration tests
dotnet test                                # Everything
```

## Layout

Tests live under `tests/WireCopy.Tests/` and mirror the source structure:

```
tests/WireCopy.Tests/
├── Bookmarks/
├── Browser/
├── Collections/
├── Podcast/
├── Security/
├── Storage/
├── DependencyInjectionTests.cs
├── TestDatabaseFixture.cs
└── ...
```

Tests are tagged with `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`. Integration tests that hit external services use `Skip = "..."` until credentials are present; CI runs only `Category=Unit` by default.

## Tooling

- **xUnit** — test framework
- **NSubstitute** — mocking
- **FluentAssertions** — readable assertions
- **Coverlet** — coverage collection

## Philosophy: don't mock the layer you're testing

Tests must reflect real-world usage. Avoid mocking the very component that would actually fail in production — if your test passes only because the failing dependency is mocked away, the test gives false confidence.

A real example from this project: `GcsStorageClient` initialization throws `InvalidOperationException` when GCP credentials are missing. The original tests mocked `ICloudStorageClient` entirely, so missing-credential crashes shipped to production undetected. The current tests in `tests/WireCopy.Tests/Podcast/GcsStorageClientUnitTests.cs` exercise the real exception types instead.

Guidelines:

- Mock external network calls (never call real APIs in tests).
- Don't mock at the boundary you're trying to verify — use realistic fakes or integration tests.
- When testing error handling, exercise the actual exception types your dependency throws, not just the ones you expect.
- Store HTML / response fixtures under the test project rather than hitting live sites.

## Coverage

Run with coverage:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

Reports land under `tests/WireCopy.Tests/TestResults/<guid>/coverage.cobertura.xml`. Target is >80% on changed code; treat as a guide, not a quality gate.

## Watch mode

```bash
cd tests/WireCopy.Tests
dotnet watch test
```

## Troubleshooting

- **Tests not discovered** — `dotnet clean && dotnet build && dotnet test`.
- **Stale build artifacts** — `rm -rf tests/WireCopy.Tests/{bin,obj}` then restore.
- **Locked SQLite file from a prior run** — kill any leftover `dotnet` / `WireCopy.API` processes; `TestDatabaseFixture` uses unique temp paths but a crashed run can leave handles open.
