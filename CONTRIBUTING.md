# Contributing to TermReader

Thanks for your interest. TermReader is a personal/educational project, but contributions are welcome.

## Getting started

See [docs/SETUP.md](docs/SETUP.md) for environment setup and [docs/TESTING.md](docs/TESTING.md) for the test workflow.

```bash
dotnet restore
dotnet build
./scripts/test.sh        # fast unit tests
./scripts/test.sh --all  # full suite including integration
```

## Branching

- Trunk-based development off `main`.
- Branch naming: `feat/<short-description>`, `fix/<short-description>`, `docs/<short-description>`.

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/) format:

- `feat(browser): add reader-view focus indicator`
- `fix(podcast): handle empty chapter title`
- `docs: clarify OpenAI TTS setup`

Keep commits atomic — one logical change per commit.

## Pull requests

1. Open a PR against `main`.
2. Make sure `./scripts/test.sh --all` passes.
3. Run `dotnet format` before pushing.
4. Describe what changed and why; reference any related issue.

## Code style

- Nullable reference types are enabled project-wide. Prefer real null checks over `!`.
- Avoid sync-over-async (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`); make the call chain async.
- Use `ConfigureAwait(false)` in `TermReader.Application` and `TermReader.Infrastructure` library code.
- StyleCop and SonarAnalyzer rules are enforced in Release builds.

## Reporting issues

Open a GitHub issue with steps to reproduce, expected vs actual behavior, and your platform.

## Security

Please report security issues privately — see [SECURITY.md](SECURITY.md).
