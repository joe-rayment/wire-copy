// Educational and personal use only.

using TermReader.Domain.Enums;

namespace TermReader.Domain.ValueObjects.Credentials;

/// <summary>
/// Describes a single step in a multi-step login flow.
/// Each step fills an input field with a credential value and clicks a submit button.
/// </summary>
public class LoginStep
{
    /// <summary>
    /// CSS selector for the input field to fill in this step.
    /// </summary>
    public string FieldSelector { get; set; } = string.Empty;

    /// <summary>
    /// Which credential value to enter (Username or Password).
    /// </summary>
    public StepValueType ValueType { get; set; }

    /// <summary>
    /// CSS selector for the button to click after filling the field.
    /// If null, presses Enter on the input field instead.
    /// </summary>
    public string? SubmitSelector { get; set; }

    // Parameterless constructor for JSON deserialization
    public LoginStep() { }

    public LoginStep(string fieldSelector, StepValueType valueType, string? submitSelector = null)
    {
        FieldSelector = fieldSelector;
        ValueType = valueType;
        SubmitSelector = submitSelector;
    }
}
