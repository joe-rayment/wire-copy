# Local Data Setup Guide

This guide explains how local data (database, credentials, cookies) is stored and managed.

## Overview

The following data is stored locally and **never committed to the repository**:

1. **Database** (SQLite) - Article content, sessions, audit trail
2. **Credentials** - NYT login, ElevenLabs API key
3. **Cookies** - Encrypted authentication cookies
4. **Data Protection Keys** - Cookie encryption keys
5. **Cache** - Audio and article cache

All these files persist on your local machine and will work across git pulls/pushes.

---

## Local Data Locations

### Windows

```
%LOCALAPPDATA%\TermReader\
├── termreader.db          # SQLite database
├── termreader.db-shm      # SQLite shared memory
├── termreader.db-wal      # SQLite write-ahead log
├── cookies.json                # Encrypted authentication cookies
├── keys\                       # Data Protection API keys
│   └── key-*.xml              # Encryption keys
└── cache\
    └── audio\                 # Cached audio files
        └── *.mp3
```

**Full path example:**
```
C:\Users\YourName\AppData\Local\TermReader\
```

### Linux

```
~/.local/share/TermReader/
├── termreader.db
├── cookies.json
├── keys/
└── cache/
    └── audio/
```

### macOS

```
~/Library/Application Support/TermReader/
├── termreader.db
├── cookies.json
├── keys/
└── cache/
    └── audio/
```

---

## Initial Setup

### 1. Clone Repository

```bash
git clone https://github.com/joe-rayment/newspaper_reader.git
cd newspaper_reader
```

### 2. Configure Credentials

You have **two options** for configuring credentials:

#### Option A: User Secrets (Recommended for Development)

```bash
# Navigate to API project
cd src/TermReader.API

# Initialize user secrets
dotnet user-secrets init

# Set authentication credentials
dotnet user-secrets set "Auth:Email" "your-email@example.com"
dotnet user-secrets set "Auth:Password" "your-password"

# Set ElevenLabs API key
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key"
```

**Where secrets are stored:**
- **Windows:** `%APPDATA%\Microsoft\UserSecrets\<user_secrets_id>\secrets.json`
- **Linux:** `~/.microsoft/usersecrets/<user_secrets_id>/secrets.json`
- **macOS:** `~/.microsoft/usersecrets/<user_secrets_id>/secrets.json`

#### Option B: secrets.json (Recommended for Production)

Create `src/TermReader.API/secrets.json`:

```json
{
  "NYT": {
    "Email": "your-email@example.com",
    "Password": "your-password"
  },
  "ElevenLabs": {
    "ApiKey": "your-api-key"
  }
}
```

**Note:** This file is in `.gitignore` and will never be committed.

### 3. Run Application

```bash
# Restore dependencies
dotnet restore

# Run
dotnet run --project src/TermReader.API
```

**On first run:**
- Database will be created automatically at `%LOCALAPPDATA%\TermReader\termreader.db`
- Migrations will run automatically
- You'll be prompted to authenticate with NYT
- Cookies will be encrypted and saved

---

## What Happens on Git Pull

When you `git pull` to get updates:

✅ **Persists locally (not affected):**
- Database (`termreader.db`)
- Credentials (`secrets.json` or user secrets)
- Cookies (`cookies.json`)
- Encryption keys (`keys/`)
- Cache (`cache/`)

✅ **Gets updated from remote:**
- Source code
- Migrations (new migrations will run automatically)
- Configuration templates (`appsettings.json`)
- Documentation

**Result:** Your local data is preserved, code is updated.

---

## What's in .gitignore

The `.gitignore` file ensures these patterns are never committed:

```gitignore
# Database files (SQLite)
*.db
*.db-shm
*.db-wal
*.db-journal
termreader.db*

# Cookies and authentication
cookies.json
**/cookies.json

# Data Protection keys (cookie encryption)
**/keys/
**/DataProtection-Keys/

# Cache directories
**/cache/
cache/

# User Secrets
secrets.json
**/secrets.json
**/appsettings.*.json
!**/appsettings.json
```

---

## Database Management

### View Database

```bash
# Install sqlite3 (if not already installed)
# Windows: choco install sqlite
# Linux: apt-get install sqlite3
# macOS: brew install sqlite3

# Open database
sqlite3 ~/.local/share/TermReader/termreader.db
# or on Windows:
sqlite3 %LOCALAPPDATA%\TermReader\termreader.db
```

### Query Examples

```sql
-- List all sessions
SELECT Id, StartedAt, Status, EstimatedCost
FROM ScrapingSessions
ORDER BY StartedAt DESC;

-- Articles in a session
SELECT a.Title, a.Url, a.AudioFilePath
FROM Articles a
JOIN ScrapingSessionArticle sa ON a.Id = sa.ArticleId
WHERE sa.SessionId = 'your-session-id';

-- Database statistics
SELECT
    (SELECT COUNT(*) FROM Articles) as TotalArticles,
    (SELECT COUNT(*) FROM ScrapingSessions) as TotalSessions,
    (SELECT SUM(EstimatedCost) FROM ScrapingSessions) as TotalCost;
```

### Backup Database

```bash
# Windows
copy %LOCALAPPDATA%\TermReader\termreader.db backup.db

# Linux/macOS
cp ~/.local/share/TermReader/termreader.db backup.db
```

### Reset Database

If you want to start fresh:

```bash
# Windows
del %LOCALAPPDATA%\TermReader\termreader.db*

# Linux/macOS
rm ~/.local/share/TermReader/termreader.db*
```

Database will be recreated on next run.

---

## Cookie Management

### View Cookie Status

```bash
dotnet run --project src/TermReader.API -- --cookie-info
```

### Clear Cookies

```bash
dotnet run --project src/TermReader.API -- --clear-cookies
```

### Cookie Security

- Cookies are **encrypted at rest** using Data Protection API
- Encryption keys are stored in `keys/` directory
- Keys are **machine-specific** and **user-specific**
- If you lose the `keys/` directory, cookies cannot be decrypted
  - Solution: Clear cookies and re-authenticate

---

## Team Collaboration

### Sharing the Project

When sharing the project with team members:

✅ **Share:**
- Git repository URL
- Documentation (this file!)
- Setup instructions

❌ **Never share:**
- Database files (`.db`)
- Credentials (`secrets.json`, user secrets)
- Cookies (`cookies.json`)
- Encryption keys (`keys/`)

**Each team member should:**
1. Clone the repository
2. Set up their own credentials (user secrets or `secrets.json`)
3. Run the application (database will be created automatically)

### Different Credentials per Environment

You can have different credentials for development/production:

**Development (User Secrets):**
```bash
dotnet user-secrets set "Auth:Email" "dev-account@example.com"
```

**Production (secrets.json):**
```json
{
  "Auth": {
    "Email": "prod-account@example.com"
  }
}
```

---

## Docker Considerations

When running in Docker, mount volumes for persistence:

```yaml
services:
  nyt-scraper:
    image: termreader:latest
    volumes:
      # Database
      - ./data:/root/.local/share/TermReader
      # Credentials
      - ./secrets.json:/app/secrets.json
      # Output
      - ./output:/app/output
    environment:
      # Or use environment variables instead of secrets.json
      - Auth__Email=${AUTH_EMAIL}
      - Auth__Password=${AUTH_PASSWORD}
      - ElevenLabs__ApiKey=${ELEVEN_LABS_API_KEY}
```

**Note:** Docker volumes persist across container recreations.

---

## Troubleshooting

### Database Locked Error

**Problem:** `database is locked` error

**Cause:** Another instance is running or database file is corrupted

**Solution:**
```bash
# Check for running instances
# Windows: tasklist | findstr "TermReader"
# Linux/macOS: ps aux | grep TermReader

# If stuck, delete lock files
# Windows:
del %LOCALAPPDATA%\TermReader\termreader.db-shm
del %LOCALAPPDATA%\TermReader\termreader.db-wal

# Linux/macOS:
rm ~/.local/share/TermReader/termreader.db-shm
rm ~/.local/share/TermReader/termreader.db-wal
```

### Cookie Decryption Failed

**Problem:** `Failed to decrypt cookies`

**Cause:** Encryption keys are missing or changed

**Solution:**
```bash
dotnet run --project src/TermReader.API -- --clear-cookies
```

Then run the application normally to re-authenticate.

### Credentials Not Found

**Problem:** `NYT credentials not configured`

**Solution:**

Check if credentials are set:

```bash
# User secrets
dotnet user-secrets list --project src/TermReader.API

# secrets.json
cat src/TermReader.API/secrets.json
```

If missing, set them using the [Initial Setup](#2-configure-credentials) instructions.

### Database Migration Failed

**Problem:** Migration error on startup

**Solution:**

1. **Backup database first!**
   ```bash
   cp ~/.local/share/TermReader/termreader.db backup.db
   ```

2. Check migration status:
   ```bash
   cd src/TermReader.Infrastructure
   dotnet ef database update --list
   ```

3. If stuck, drop and recreate database (loses all data):
   ```bash
   rm ~/.local/share/TermReader/termreader.db*
   ```

4. Run application to recreate database

---

## Security Best Practices

### ✅ Do:
- Use user secrets for development
- Use environment variables for production (Docker/CI)
- Keep credentials file out of version control (already in `.gitignore`)
- Backup database regularly
- Use strong, unique NYT password

### ❌ Don't:
- Commit `secrets.json` to git
- Share your `keys/` directory
- Share your database file (contains article content)
- Use production credentials in development
- Disable cookie encryption

---

## Summary

**What's in Git:**
- ✅ Source code
- ✅ Migrations
- ✅ Configuration templates
- ✅ Documentation

**What's Local Only:**
- 💾 Database (`termreader.db`)
- 🔑 Credentials (`secrets.json`)
- 🍪 Cookies (`cookies.json`)
- 🔐 Encryption keys (`keys/`)
- 📦 Cache (`cache/`)

**Setup Steps:**
1. Clone repo
2. Set credentials (user secrets or `secrets.json`)
3. Run application
4. Database created automatically

**On Git Pull:**
- Code updates ✅
- Local data preserved ✅
- Migrations run automatically ✅

---

For questions or issues, please open a GitHub issue at:
https://github.com/joe-rayment/newspaper_reader/issues
