# Local Data Storage

TermReader stores everything locally — no central server. This document explains what lives where, what's gitignored, and how to manage it.

## What is stored locally

| Data                    | Format                  | Encrypted?                |
|-------------------------|-------------------------|---------------------------|
| Bookmarks, collections, settings | SQLite (`termreader.db`) | No                        |
| Site session cookies    | Inside the SQLite DB    | Yes — DataProtection      |
| DataProtection keys     | XML files in `keys/`    | Machine-bound (DPAPI on Windows, file perms elsewhere) |
| TTS audio cache         | M4A/MP3 chunks          | No                        |
| Page content cache      | Inside the SQLite DB    | No                        |
| Logs                    | Daily-rolling text files | No                        |

## Locations

The defaults follow each platform's convention for app data:

- **Linux:** `~/.local/share/TermReader/`
- **macOS:** `~/Library/Application Support/TermReader/`
- **Windows:** `%LOCALAPPDATA%\TermReader\`

Layout:

```
TermReader/
├── termreader.db          # SQLite database
├── termreader.db-shm      # SQLite shared memory
├── termreader.db-wal      # SQLite write-ahead log
├── keys/                  # ASP.NET DataProtection keys
│   └── key-*.xml
└── cache/
    ├── audio/             # Generated TTS chunks
    └── articles/          # Parsed page content
```

Logs roll into `./logs/termreader-<date>.log` relative to where you launch the app.

## What `.gitignore` covers

```
*.db, *.db-shm, *.db-wal, *.db-journal, termreader.db*
cookies.json, **/cookies.json
**/keys/, **/DataProtection-Keys/
**/cache/
secrets.json, **/secrets.json
**/appsettings.*.json   (the base appsettings.json is tracked)
output/, logs/
```

## Configuring credentials

See [SETUP.md](SETUP.md#credentials) for the full credential setup. Briefly: use `dotnet user-secrets` for local development, environment variables in Docker/CI, and the local `secrets.json` (gitignored) when neither is convenient.

## Surviving `git pull`

Local data lives outside the repo, so `git pull` never touches it:

- ✅ Source code, migrations, and `appsettings.json` update from remote.
- ✅ `termreader.db`, `keys/`, and `cache/` stay where they are.
- ✅ Database migrations run automatically on next launch.

## Database management

Inspect the database with the `sqlite3` CLI:

```bash
# Linux
sqlite3 ~/.local/share/TermReader/termreader.db
# macOS
sqlite3 "$HOME/Library/Application Support/TermReader/termreader.db"
# Windows
sqlite3 %LOCALAPPDATA%\TermReader\termreader.db
```

Useful queries:

```sql
.tables
SELECT * FROM Bookmarks ORDER BY CreatedAt DESC LIMIT 10;
SELECT name FROM sqlite_master WHERE type='table';
```

### Backup

```bash
cp ~/.local/share/TermReader/termreader.db ~/termreader-backup.db
```

### Reset

```bash
rm ~/.local/share/TermReader/termreader.db*   # plus the -shm/-wal siblings
```

Database and migrations are recreated on next launch. Saved cookies and bookmarks are lost.

## Troubleshooting

### `database is locked`

Another TermReader process is holding the file, or a previous run crashed mid-write. Kill the lingering process; the WAL/SHM files clean up on next clean launch. As a last resort:

```bash
rm ~/.local/share/TermReader/termreader.db-shm
rm ~/.local/share/TermReader/termreader.db-wal
```

### Cookies fail to decrypt

DataProtection keys are machine- and user-bound. Copying the database between machines without copying `keys/` will leave cookies unreadable. The fix is to clear the affected site cookies and re-authenticate.

### Migration error on launch

Back up the DB, then check pending migrations:

```bash
cd src/TermReader.Persistence
dotnet ef database update --list
```

If you don't need the data, deleting `termreader.db*` lets the app rebuild from scratch.

## Docker

Mount the data directory and pass secrets via environment variables:

```yaml
services:
  termreader:
    image: termreader:latest
    environment:
      - OpenAiTts__ApiKey=${OPENAI_API_KEY}
      - Anthropic__ApiKey=${ANTHROPIC_API_KEY}
    volumes:
      - ./data:/root/.local/share/TermReader
      - ./output:/app/output
      - ./logs:/app/logs
```

## Security checklist

- ✅ Use `dotnet user-secrets` (dev) or env vars (prod). Never commit real keys.
- ✅ Back up `termreader.db` periodically — it holds your bookmarks and reading state.
- ✅ Treat `keys/` as a secret. Don't share it; don't commit it.
- ❌ Don't share `cookies.json`, `secrets.json`, or the database in support tickets — strip credentials first.
