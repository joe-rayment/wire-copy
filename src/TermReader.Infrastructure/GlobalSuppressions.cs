// <copyright file="GlobalSuppressions.cs" company="TermReader">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using System.Diagnostics.CodeAnalysis;

// ============================================================================
// S2139 (exception handling pattern)
// These exceptions are logged and rethrown with the original exception preserved.
// The existing pattern is acceptable for this application's error handling approach.
// ============================================================================
[assembly: SuppressMessage("Major Code Smell", "S2139:Exceptions should be either logged or rethrown but not both", Justification = "Intentional pattern: log for diagnostics, rethrow to bubble up to caller", Scope = "module")]

// ============================================================================
// SA1402 (multiple types per file)
// Result types are kept with their associated service class for cohesion.
// ============================================================================
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Result types are intentionally kept with their service class", Scope = "type", Target = "~T:TermReader.Infrastructure.Browser.CookieImportResult")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Result types are intentionally kept with their service class", Scope = "type", Target = "~T:TermReader.Infrastructure.Browser.CookieInfoResult")]

// ============================================================================
// SA1118 (multi-line parameters)
// Complex logging calls may span multiple lines for readability.
// ============================================================================
[assembly: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1118:Parameter should not span multiple lines", Justification = "Complex logging calls may span lines for readability", Scope = "module")]

// ============================================================================
// SA1214 (readonly field ordering)
// Suppressed at module level - field ordering is consistent within each class.
// ============================================================================
[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1214:Readonly fields should appear before non-readonly fields", Justification = "Field grouping by purpose is preferred over strict readonly-first ordering", Scope = "module")]

// ============================================================================
// SA1636 (copyright header text)
// The copyright text format is intentional and consistent across the project.
// ============================================================================
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1636:File header copyright text should match", Justification = "Copyright format is intentional and consistent", Scope = "module")]

// ============================================================================
// S6667 (logging exception parameter)
// Some catch blocks log warnings without the exception when the exception details
// are not needed (e.g., expected failure cases).
// ============================================================================
[assembly: SuppressMessage("Minor Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter", Justification = "Exception details intentionally omitted for expected failure cases", Scope = "module")]

// ============================================================================
// S6580 (date parsing format provider)
// DateTime.TryParse is used with well-known ISO formats that are culture-invariant.
// ============================================================================
[assembly: SuppressMessage("Minor Code Smell", "S6580:Use a format provider when parsing date and time", Justification = "Parsing ISO 8601 dates which are culture-invariant", Scope = "module")]
