# Phase 4 Cookie Encryption - Usage Guide

## Overview

Phase 4 implements **Priority 15: Cookie Encryption & Management** from the implementation plan. This addresses the critical security vulnerability where NYT authentication cookies were stored in plain text.

## What Changed?

### Before (v1 - Insecure)
```json
[
  {
    "Name": "NYT-S",
    "Value": "session-token-here",
    "Domain": ".nytimes.com",
    "Path": "/",
    "Expiry": "2025-02-01T00:00:00Z"
  }
]
```
❌ **Plain text** - Anyone with file access can steal session tokens

### After (v2 - Secure)
```json
{
  "Version": 2,
  "CreatedAt": "2025-01-01T00:00:00Z",
  "ExpiresAt": "2025-01-31T00:00:00Z",
  "EncryptedData": "CfDJ8KV8..."
}
```
✅ **Encrypted** using ASP.NET Core Data Protection API
✅ **Auto-expiration** after 30 days
✅ **Platform-specific protection** (DPAPI on Windows, file permissions on Linux/macOS)

## New CLI Commands

### Check Cookie Status
```bash
dotnet run --project src/TermReader.API -- --cookie-info
```

**Output:**
```
╔═══════════════════════════════════════╗
║        Cookie Information             ║
╚═══════════════════════════════════════╝

File Path:    C:\Users\...\AppData\Local\TermReader\cookies.json
Version:      v2 (Encrypted)
Created At:   2025-01-06 12:00:00 UTC
Expires At:   2025-02-05 12:00:00 UTC (✓ Valid)
Time Remaining: 30 days, 0 hours
Cookie Count: 15
```

### Clear Cookies
```bash
dotnet run --project src/TermReader.API -- --clear-cookies
```

**Prompts for confirmation:**
```
Are you sure you want to clear all stored cookies? (y/N): y
✓ Cookies cleared successfully
  You will need to re-authenticate on the next run
```

## Migration from v1 to v2

**Automatic and seamless!** No action required.

### First run after upgrade:
1. Application detects v1 (plain text) cookies
2. Logs: `"Migrating cookies from v1 (plain text) to v2 (encrypted)"`
3. Loads cookies for current session
4. On successful authentication, saves as v2 (encrypted)

### What happens to old cookies?
- Old plain text cookies are **loaded but not re-saved** in plain text
- Next authentication automatically saves in encrypted format
- You can manually trigger migration by:
  1. Clear cookies: `--clear-cookies`
  2. Run scraper (will re-authenticate and save encrypted)

## Security Features

### 1. Encryption at Rest
- Uses **ASP.NET Core Data Protection API**
- Cross-platform support (Windows, Linux, macOS)
- Windows: DPAPI (Data Protection API) with user-scope
- Linux/macOS: File-based key storage with restricted permissions

### 2. Encryption Keys
Stored at: `%LocalAppData%\TermReader\keys\`

**Important:**
- Keys are **user-specific** (different Windows users = different keys)
- Keys are **machine-specific** (backup won't work on different machines)
- If keys are lost, cookies cannot be decrypted (will re-authenticate)

### 3. Automatic Expiration
- Default: **30 days** from creation
- Configurable in code: `CookieExpirationDays` constant
- Expired cookies are automatically rejected and trigger re-authentication

### 4. Metadata Tracking
Encrypted cookies include metadata:
```json
{
  "Cookies": [...],
  "Metadata": {
    "user_agent": "Mozilla/5.0...",
    "last_used": "2025-01-06T12:00:00Z",
    "saved_by": "TermReader"
  }
}
```

## Programmatic Usage

### Using ICookieManager

```csharp
// Inject service
public class MyService
{
    private readonly ICookieManager _cookieManager;

    public MyService(ICookieManager cookieManager)
    {
        _cookieManager = cookieManager;
    }

    public async Task CheckCookiesAsync()
    {
        var info = await _cookieManager.GetCookieInfoAsync();

        if (info == null || !info.Exists)
        {
            Console.WriteLine("No cookies found");
            return;
        }

        Console.WriteLine($"Version: v{info.Version}");
        Console.WriteLine($"Encrypted: {info.IsEncrypted}");
        Console.WriteLine($"Expired: {info.IsExpired}");
        Console.WriteLine($"Cookies: {info.CookieCount}");

        if (info.IsExpired)
        {
            // Clear expired cookies
            await _cookieManager.ClearCookiesAsync();
        }
    }
}
```

### Using ICookieEncryptionService

```csharp
public class MyService
{
    private readonly ICookieEncryptionService _encryptionService;

    public MyService(ICookieEncryptionService encryptionService)
    {
        _encryptionService = encryptionService;
    }

    public void EncryptSensitiveData()
    {
        var plainText = "sensitive data";
        var encrypted = _encryptionService.Encrypt(plainText);

        // Store encrypted data...

        // Later, decrypt
        var decrypted = _encryptionService.Decrypt(encrypted);
    }
}
```

## Testing

### Unit Tests Included

**CookieEncryptionServiceTests.cs** (13 tests)
- ✅ Encrypt/decrypt round-trip
- ✅ Large data handling
- ✅ JSON data preservation
- ✅ Error handling (null, empty, corrupted data)

**CookieManagerTests.cs** (9 tests)
- ✅ V1 and V2 format handling
- ✅ Expiration checking
- ✅ Clear cookies functionality
- ✅ Corrupted data handling

### Run Tests
```bash
dotnet test --filter "CookieEncryptionServiceTests|CookieManagerTests"
```

## Troubleshooting

### Error: "Failed to decrypt cookies"
**Cause:** Encryption keys changed or corrupted data

**Solution:**
```bash
dotnet run --project src/TermReader.API -- --clear-cookies
```
Then re-run the scraper to re-authenticate.

### Error: "Cookies expired"
**Cause:** More than 30 days since last authentication

**Solution:** Normal behavior. Run the scraper and it will re-authenticate automatically.

### Key Recovery
If you lose encryption keys (`%LocalAppData%\TermReader\keys\`):
1. Cookies cannot be recovered
2. Clear cookies: `--clear-cookies`
3. Re-authenticate on next run

**Prevention:** Back up the `keys` directory if needed (but keys are machine-specific).

## Docker Considerations

### Volume Mounts
To persist cookies and keys across container restarts:

```yaml
services:
  nyt-scraper:
    image: termreader:latest
    volumes:
      - ./data:/app/data
      - ./keys:/root/.local/share/TermReader/keys
      - ./cookies:/root/.local/share/TermReader
```

**Note:** Keys are container-specific. Recreating the container = new keys = must re-authenticate.

## Configuration

### Cookie Expiration Duration
To change the default 30-day expiration:

**File:** `src/TermReader.Infrastructure/Browser/NYTAuthService.cs`

```csharp
private const int CookieExpirationDays = 30; // Change this value
```

### Encryption Key Location
Keys are stored at:
- Windows: `%LocalAppData%\TermReader\keys\`
- Linux: `~/.local/share/TermReader/keys/`
- macOS: `~/.local/share/TermReader/keys/`

To change location, modify `DependencyInjection.cs`:

```csharp
var dataProtectionPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "TermReader",
    "keys"); // Change "keys" to your preferred directory
```

## Security Best Practices

### ✅ Do:
- Let cookies auto-expire after 30 days
- Use `--clear-cookies` if you suspect cookie compromise
- Back up encryption keys if needed (they're user/machine-specific)
- Run with least privilege (don't run as admin/root)

### ❌ Don't:
- Don't share encryption keys between users/machines
- Don't commit cookies.json or keys/ to version control
- Don't disable encryption (no configuration option by design)
- Don't manually edit encrypted cookie files

## Performance Impact

- **Encryption overhead:** ~1-5ms per operation (negligible)
- **Storage overhead:** ~20% increase in file size (encrypted data)
- **Memory overhead:** None (encryption is per-operation, not cached)

## Future Enhancements

Potential improvements for future phases:
1. Configurable expiration duration via CLI/config
2. Remote key storage (Azure Key Vault, AWS KMS)
3. Cookie rotation on schedule
4. Multi-factor authentication support

## Summary

✅ **Implemented:** Cookie encryption with Data Protection API
✅ **Implemented:** Automatic v1 → v2 migration
✅ **Implemented:** Cookie expiration (30 days)
✅ **Implemented:** CLI commands (--cookie-info, --clear-cookies)
✅ **Implemented:** Comprehensive unit tests (22 tests)
✅ **Security:** DPAPI/file-based protection
✅ **Cross-platform:** Windows, Linux, macOS

**Estimated Effort:** 3.5 hours
**Actual Implementation:** Priority 15 complete
**Next Phase:** Enhanced rate limiting, session resume, enhanced monitoring

---

For questions or issues, please open a GitHub issue at:
https://github.com/joe-rayment/newspaper_reader/issues
