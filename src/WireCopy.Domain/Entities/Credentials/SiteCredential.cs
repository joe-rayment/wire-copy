// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using WireCopy.Domain.Enums;
using WireCopy.Domain.ValueObjects.Credentials;

namespace WireCopy.Domain.Entities.Credentials;

/// <summary>
/// Represents encrypted credentials for authenticating with a specific website.
/// </summary>
public class SiteCredential
{
    public Guid Id { get; private set; }

    /// <summary>
    /// The hostname for which these credentials apply (e.g. "nytimes.com").
    /// </summary>
    public string Domain { get; private set; }

    /// <summary>
    /// The type of authentication these credentials are used for.
    /// </summary>
    public CredentialType CredentialType { get; private set; }

    /// <summary>
    /// The username, encrypted via DPAPI / Data Protection API.
    /// </summary>
    public byte[] EncryptedUsername { get; private set; }

    /// <summary>
    /// The password, encrypted via DPAPI / Data Protection API.
    /// </summary>
    public byte[] EncryptedPassword { get; private set; }

    /// <summary>
    /// CSS selector for the login form username field.
    /// </summary>
    public string? UsernameSelector { get; private set; }

    /// <summary>
    /// CSS selector for the login form password field.
    /// </summary>
    public string? PasswordSelector { get; private set; }

    /// <summary>
    /// CSS selector for the login form submit button.
    /// </summary>
    public string? SubmitSelector { get; private set; }

    /// <summary>
    /// URL to navigate to for login.
    /// </summary>
    public string? LoginUrl { get; private set; }

    /// <summary>
    /// JSON-serialized list of login steps for multi-step login flows.
    /// When non-null, overrides UsernameSelector/PasswordSelector/SubmitSelector.
    /// </summary>
    public string? LoginStepsJson { get; private set; }

    /// <summary>
    /// Timestamp when the credential was created.
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Timestamp when the credential was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// Gets the deserialized login steps, or null if not configured.
    /// </summary>
    public List<LoginStep>? GetLoginSteps()
    {
        if (string.IsNullOrEmpty(LoginStepsJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<LoginStep>>(LoginStepsJson);
    }

    /// <summary>
    /// Returns true when this credential uses a multi-step login flow.
    /// </summary>
    public bool IsMultiStep => !string.IsNullOrEmpty(LoginStepsJson);

    private SiteCredential(
        string domain,
        CredentialType credentialType,
        byte[] encryptedUsername,
        byte[] encryptedPassword,
        string? usernameSelector,
        string? passwordSelector,
        string? submitSelector,
        string? loginUrl,
        string? loginStepsJson = null)
    {
        Id = Guid.NewGuid();
        Domain = domain;
        CredentialType = credentialType;
        EncryptedUsername = encryptedUsername;
        EncryptedPassword = encryptedPassword;
        UsernameSelector = usernameSelector;
        PasswordSelector = passwordSelector;
        SubmitSelector = submitSelector;
        LoginUrl = loginUrl;
        LoginStepsJson = loginStepsJson;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    // EF Core constructor
    private SiteCredential()
    {
        Domain = string.Empty;
        EncryptedUsername = Array.Empty<byte>();
        EncryptedPassword = Array.Empty<byte>();
    }

    /// <summary>
    /// Creates a new site credential with encrypted username and password.
    /// </summary>
    public static SiteCredential Create(
        string domain,
        CredentialType credentialType,
        byte[] encryptedUsername,
        byte[] encryptedPassword,
        string? usernameSelector = null,
        string? passwordSelector = null,
        string? submitSelector = null,
        string? loginUrl = null,
        List<LoginStep>? loginSteps = null)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArgumentException("Domain cannot be empty", nameof(domain));
        }

        if (encryptedUsername == null || encryptedUsername.Length == 0)
        {
            throw new ArgumentException("Encrypted username cannot be null or empty", nameof(encryptedUsername));
        }

        if (encryptedPassword == null || encryptedPassword.Length == 0)
        {
            throw new ArgumentException("Encrypted password cannot be null or empty", nameof(encryptedPassword));
        }

        string? loginStepsJson = loginSteps is { Count: > 0 }
            ? JsonSerializer.Serialize(loginSteps)
            : null;

        return new SiteCredential(
            domain.Trim().ToLowerInvariant(),
            credentialType,
            encryptedUsername,
            encryptedPassword,
            usernameSelector?.Trim(),
            passwordSelector?.Trim(),
            submitSelector?.Trim(),
            loginUrl?.Trim(),
            loginStepsJson);
    }

    /// <summary>
    /// Updates the encrypted credentials and optional form selectors.
    /// </summary>
    public void Update(
        byte[] encryptedUsername,
        byte[] encryptedPassword,
        string? usernameSelector = null,
        string? passwordSelector = null,
        string? submitSelector = null,
        string? loginUrl = null,
        List<LoginStep>? loginSteps = null)
    {
        if (encryptedUsername == null || encryptedUsername.Length == 0)
        {
            throw new ArgumentException("Encrypted username cannot be null or empty", nameof(encryptedUsername));
        }

        if (encryptedPassword == null || encryptedPassword.Length == 0)
        {
            throw new ArgumentException("Encrypted password cannot be null or empty", nameof(encryptedPassword));
        }

        EncryptedUsername = encryptedUsername;
        EncryptedPassword = encryptedPassword;
        UsernameSelector = usernameSelector?.Trim();
        PasswordSelector = passwordSelector?.Trim();
        SubmitSelector = submitSelector?.Trim();
        LoginUrl = loginUrl?.Trim();
        LoginStepsJson = loginSteps is { Count: > 0 }
            ? JsonSerializer.Serialize(loginSteps)
            : null;
        UpdatedAt = DateTime.UtcNow;
    }
}
