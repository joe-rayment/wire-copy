# Security Policy

## Reporting a vulnerability

If you discover a security issue, please **do not** open a public GitHub issue. Instead, report it privately via GitHub's [private vulnerability reporting](https://github.com/joe-rayment/newspaper_reader/security/advisories/new).

You should expect an initial response within a few days. Once a fix is available, we'll coordinate disclosure.

## Scope

In-scope concerns include:

- Credential leakage (API keys, session cookies) through logs, files, or error messages.
- Path traversal in file output.
- Code execution via crafted page content during scraping.
- Cookie / DataProtection key handling.

Out of scope:

- Issues that require physical access to an unlocked machine.
- Denial of service against the local terminal application.
- Vulnerabilities in upstream dependencies (please report those upstream).

## Handling secrets

TermReader expects credentials via:

- `dotnet user-secrets` (preferred for local development),
- Environment variables (preferred for Docker / CI),
- Or a local `secrets.json` (gitignored — see `secrets.json.example`).

Never commit credentials. The repository's `.gitignore` covers `secrets.json`, `appsettings.*.json` (except the base `appsettings.json`), `cookies.json`, the DataProtection `keys/` directory, and `*.db` files.
