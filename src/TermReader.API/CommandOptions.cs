// <copyright file="CommandOptions.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using CommandLine;

namespace TermReader.API;

/// <summary>
/// Options for the browse verb - launches terminal browser mode.
/// </summary>
[Verb("browse", HelpText = "Launch terminal browser for interactive web browsing")]
public class BrowseOptions
{
    [Value(0, MetaName = "url", Required = false, HelpText = "Initial URL to load (defaults to Hacker News)")]
    public string? Url { get; set; }

    /// <summary>
    /// Validates the browse options and returns validation errors if any.
    /// </summary>
    /// <returns>List of validation error messages, empty if valid.</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Validate URL format if provided
        if (!string.IsNullOrWhiteSpace(Url))
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"Invalid URL: '{Url}'. Must be a valid HTTP/HTTPS URL.");
            }
        }

        return errors;
    }
}
