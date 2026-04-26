# Cookie & Credential Encryption

TermReader stores per-site session cookies and login credentials in the local SQLite database, encrypted with [ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/introduction). This document covers what's encrypted, how the keys are managed, and how to operate the system safely.

## What's encrypted

| Stored                     | Plain or encrypted?                |
|----------------------------|------------------------------------|
| Bookmarks, collections, settings | Plain — they aren't sensitive |
| Site session cookies       | **Encrypted** at rest              |
| Site credentials (username / password / API key) | **Encrypted** at rest |
| Page content cache         | Plain                              |

Encryption uses ASP.NET DataProtection with a per-user, per-machine key ring stored under the local data directory:

- Linux / macOS: `~/.local/share/TermReader/keys/key-*.xml` (file-permission protected)
- Windows: `%LOCALAPPDATA%\TermReader\keys\` (DPAPI user-scope wrapped)

Keys are **not portable** between machines or users. Copying the database without `keys/` will leave encrypted columns unreadable.

## How cookies are captured

For sites that require authentication, TermReader prompts for a session cookie on first navigation:

1. You log into the site in your normal browser.
2. Copy the relevant session cookie value from DevTools → Application → Cookies.
3. Paste it when TermReader prompts.
4. TermReader encrypts the cookie via `ICookieEncryptionService` and stores it in the SQLite `SiteCredentials` table.

On subsequent visits, the cookie is decrypted, attached to the Patchright browser session, and refreshed via `IHttpCookieRefresher` when expiry approaches.

## Architecture

The relevant interfaces are in `TermReader.Application/Interfaces/`:

| Interface                    | Purpose                                            |
|------------------------------|----------------------------------------------------|
| `ICookieEncryptionService`   | Encrypt / decrypt arbitrary strings via DataProtection. |
| `ICookieManager`             | High-level cookie save / load / clear.              |
| `IHttpCookieRefresher`       | Refresh expiring cookies via HTTP without launching the browser. |
| `ISiteCredentialRepository`  | Persistence of credentials in the EF Core context. |

Concrete implementations live in `TermReader.Infrastructure/Browser/` and `TermReader.Persistence/`.

## Programmatic use

```csharp
public class MyService
{
    private readonly ICookieEncryptionService _encryption;

    public MyService(ICookieEncryptionService encryption)
    {
        _encryption = encryption;
    }

    public string Roundtrip(string plaintext)
    {
        var ciphertext = _encryption.Encrypt(plaintext);
        return _encryption.Decrypt(ciphertext);
    }
}
```

DI is wired in `BrowserDependencyInjection.cs`; just take the interface in your constructor.

## Operations

### Inspecting credentials

Use the `sqlite3` CLI against the local database (see [data-storage.md](data-storage.md)):

```bash
sqlite3 ~/.local/share/TermReader/termreader.db
.tables
SELECT Domain, ExpiresAt FROM SiteCredentials;   -- the value column is encrypted
```

The credential values are always encrypted. Decryption only happens in-process via `ICookieEncryptionService`.

### Clearing credentials

Delete the row(s) directly:

```sql
DELETE FROM SiteCredentials WHERE Domain LIKE '%example.com%';
```

Or wipe everything and start over by removing the DB:

```bash
rm ~/.local/share/TermReader/termreader.db*
```

### Recovering from "decrypt failed"

This usually means the `keys/` directory was deleted, restored from another machine, or the OS user changed. There is no recovery path — the data is encrypted to a key you no longer have. Clear the affected `SiteCredentials` rows and re-authenticate.

## Threat model

What the encryption protects against:

- ✅ A leaked or shared `termreader.db` file alone — the values are unreadable without `keys/`.
- ✅ A misconfigured backup that captures the DB but not the key ring.

What it does **not** protect against:

- ❌ An attacker with code execution as the same OS user — they can call DataProtection and decrypt locally.
- ❌ Malware reading process memory while TermReader is running.
- ❌ A copy of `termreader.db` **and** `keys/` together.

Treat the `keys/` directory as a secret. Don't commit it, don't ship it, don't drop it in cloud sync folders.

## Docker

For container deployments, mount the data directory so keys and credentials persist across restarts:

```yaml
services:
  termreader:
    image: termreader:latest
    volumes:
      - ./data:/root/.local/share/TermReader
```

Recreating the container without persisting `keys/` invalidates all stored credentials. Use environment variables for API keys instead of relying on encrypted on-disk storage when running in CI.

## Security best practices

- ✅ Use OS-level disk encryption (FileVault, LUKS, BitLocker) — DataProtection is not a substitute.
- ✅ Run TermReader as a normal user account, never elevated.
- ✅ Audit `SiteCredentials` periodically and remove stale entries.
- ❌ Don't share `keys/` or `termreader.db`.
- ❌ Don't try to manually edit encrypted columns — they'll fail to decrypt.
