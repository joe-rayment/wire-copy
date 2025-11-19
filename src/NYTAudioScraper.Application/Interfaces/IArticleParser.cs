// <copyright file="IArticleParser.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Service for parsing HTML content into structured Article entities
/// </summary>
public interface IArticleParser
{
    /// <summary>
    /// Parses an article from HTML content
    /// </summary>
    /// <param name="html">The HTML content to parse</param>
    /// <param name="url">The article URL</param>
    /// <returns>Parsed article or null if parsing fails</returns>
    Article? ParseArticle(string html, string url);
}
