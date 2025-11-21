// <copyright file="GlobalSuppressions.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using System.Diagnostics.CodeAnalysis;

// ============================================================================
// S101 (naming convention) - NYT acronym
// NYT is a well-known acronym that should retain its capitalization.
// ============================================================================
[assembly: SuppressMessage("Naming", "S101:Types should be named in PascalCase", Justification = "NYT is a well-known acronym that should retain its capitalization", Scope = "type", Target = "~T:NYTAudioScraper.Infrastructure.Browser.INYTAuthService")]
[assembly: SuppressMessage("Naming", "S101:Types should be named in PascalCase", Justification = "NYT is a well-known acronym that should retain its capitalization", Scope = "type", Target = "~T:NYTAudioScraper.Infrastructure.Browser.NYTAuthService")]
[assembly: SuppressMessage("Naming", "S101:Types should be named in PascalCase", Justification = "NYT is a well-known acronym that should retain its capitalization", Scope = "type", Target = "~T:NYTAudioScraper.Infrastructure.Configuration.NYTConfiguration")]
[assembly: SuppressMessage("Naming", "S101:Types should be named in PascalCase", Justification = "NYT is a well-known acronym that should retain its capitalization", Scope = "type", Target = "~T:NYTAudioScraper.Infrastructure.Configuration.Validation.NYTConfigurationValidator")]

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
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Result types are intentionally kept with their service class", Scope = "type", Target = "~T:NYTAudioScraper.Infrastructure.Browser.CookieImportResult")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:File may only contain a single type", Justification = "Result types are intentionally kept with their service class", Scope = "type", Target = "~T:NYTAudioScraper.Infrastructure.Browser.CookieInfoResult")]

// ============================================================================
// SA1401 (protected fields)
// Protected fields in base Repository class are intentional for derived class access.
// ============================================================================
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1401:Fields should be private", Justification = "Protected fields are intentional for derived class access in Repository pattern", Scope = "type", Target = "~T:NYTAudioScraper.Infrastructure.Persistence.Repositories.Repository`1")]

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
