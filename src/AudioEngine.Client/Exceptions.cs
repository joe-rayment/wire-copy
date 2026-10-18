using System.Text.Json;

namespace AudioEngine.Client;

/// <summary>The server answered with a non-success status. Carries the RFC 9457 problem details when present.</summary>
public class AudioEngineApiException : Exception
{
    public AudioEngineApiException(int statusCode, string? title, string? detail, string? problemType, string? rawBody,
        IReadOnlyDictionary<string, JsonElement>? extensions = null)
        : base(BuildMessage(statusCode, title, detail))
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        ProblemType = problemType;
        RawBody = rawBody;
        Extensions = extensions ?? new Dictionary<string, JsonElement>();
    }

    /// <summary>HTTP status code (400 validation, 401 auth, 404 unknown job, 409 wrong state, 402 budget).</summary>
    public int StatusCode { get; }
    public string? Title { get; }
    public string? Detail { get; }
    public string? ProblemType { get; }
    public string? RawBody { get; }

    /// <summary>Non-standard problem members (for example <c>estimateUsd</c> on a 402).</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; }

    private static string BuildMessage(int status, string? title, string? detail) =>
        $"audio-engine returned {status}" + (title is null ? "" : $" {title}") + (detail is null ? "" : $": {detail}");
}

/// <summary>402: the job's estimated cost would exceed the configured budget.</summary>
public sealed class BudgetExceededApiException : AudioEngineApiException
{
    public BudgetExceededApiException(string? title, string? detail, string? problemType, string? rawBody,
        IReadOnlyDictionary<string, JsonElement> extensions, decimal estimateUsd, decimal limitUsd, string? reason)
        : base(402, title, detail, problemType, rawBody, extensions)
    {
        EstimateUsd = estimateUsd;
        LimitUsd = limitUsd;
        Reason = reason;
    }

    public decimal EstimateUsd { get; }
    public decimal LimitUsd { get; }
    public string? Reason { get; }
}
