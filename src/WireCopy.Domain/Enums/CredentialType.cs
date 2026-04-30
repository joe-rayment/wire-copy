// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums;

/// <summary>
/// Type of credential used for site authentication.
/// </summary>
public enum CredentialType
{
    /// <summary>
    /// Login via an HTML form with username/password fields.
    /// </summary>
    FormLogin,

    /// <summary>
    /// HTTP Basic Authentication.
    /// </summary>
    BasicAuth,
}
