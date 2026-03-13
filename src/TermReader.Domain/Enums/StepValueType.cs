// Educational and personal use only.

namespace TermReader.Domain.Enums;

/// <summary>
/// Identifies which decrypted credential value to use for a login step's input field.
/// </summary>
public enum StepValueType
{
    /// <summary>
    /// Use the decrypted username/email value.
    /// </summary>
    Username,

    /// <summary>
    /// Use the decrypted password value.
    /// </summary>
    Password,
}
