// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WireCopy.Application.DTOs;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class TtsValidationTests
{
    #region TtsValidationResult

    [Fact]
    public void Valid_ReturnsIsValidTrue()
    {
        var result = TtsValidationResult.Valid();

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Invalid_SetsAllFields()
    {
        var result = TtsValidationResult.Invalid("Key is bad", "invalid_key");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Key is bad");
        result.ErrorCode.Should().Be("invalid_key");
    }

    #endregion

    #region OpenAiKeyValidator — No Key

    // Guards:   A missing OpenAI key is reported as "no_key" with a plain message,
    //           without ever contacting OpenAI.
    // Fails if: OpenAiKeyValidator.ValidateAsync(null) tries a network call, returns
    //           Valid, or uses a different error code / message for the blank case.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~TtsValidationTests.OpenAiKeyValidator_NullKey_ReturnsNoKeyError"
    [Fact]
    public async Task OpenAiKeyValidator_NullKey_ReturnsNoKeyError()
    {
        var validator = new OpenAiKeyValidator();

        var result = await validator.ValidateAsync(null);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("no_key");
        result.ErrorMessage.Should().Be("No API key configured.");
    }

    // Guards:   An empty or whitespace-only key counts as "not configured" (no_key),
    //           the same as a null key.
    // Fails if: ValidateAsync("") or ValidateAsync("   ") is sent to OpenAI as a real
    //           key, or is classified as anything other than no_key.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~TtsValidationTests.OpenAiKeyValidator_BlankKey_ReturnsNoKeyError"
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OpenAiKeyValidator_BlankKey_ReturnsNoKeyError(string apiKey)
    {
        var validator = new OpenAiKeyValidator();

        var result = await validator.ValidateAsync(apiKey);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("no_key");
    }

    #endregion

    #region OpenAiKeyValidator — Cancellation

    // Guards:   Cancelling the key check surfaces as OperationCanceledException so the
    //           settings screen can tell "user backed out" from "key is bad".
    // Fails if: ValidateAsync swallows cancellation and turns it into a
    //           network_error / server_error result instead of rethrowing.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~TtsValidationTests.OpenAiKeyValidator_CancelledToken_ThrowsOperationCancelledException"
    [Fact]
    public async Task OpenAiKeyValidator_CancelledToken_ThrowsOperationCancelledException()
    {
        var validator = new OpenAiKeyValidator();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => validator.ValidateAsync("sk-test", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region OpenAiKeyValidator — HTTP status classification (loopback fake server)

    // Guards:   A 401 from the OpenAI endpoint is classified as "invalid_key" — the one
    //           code the settings screen treats as "definitely wrong, clear the key".
    // Fails if: the validator stops mapping HTTP 401 to invalid_key (e.g. falls through
    //           to network_error), or ConfigureClientOptions no longer redirects the SDK
    //           to the configured endpoint.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~TtsValidationTests.OpenAiKeyValidator_Http401_ReturnsInvalidKey"
    [Fact]
    public async Task OpenAiKeyValidator_Http401_ReturnsInvalidKey()
    {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        // Answer every request with 401 and a minimal OpenAI-shaped error body.
        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            var body = "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}"u8.ToArray();
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        var validator = new OpenAiKeyValidator
        {
            ConfigureClientOptions = o => o.Endpoint = new Uri(prefix),
        };

        var result = await validator.ValidateAsync("sk-definitely-wrong");

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_key");
        result.ErrorMessage.Should().Be("Invalid API key.");
        await serverTask;
        listener.Stop();
    }

    #endregion

    #region Error Code Classification

    [Theory]
    [InlineData("invalid_key", true)]
    [InlineData("insufficient_credits", true)]
    [InlineData("rate_limited", false)]
    [InlineData("server_error", false)]
    [InlineData("network_error", false)]
    [InlineData("no_key", false)]
    public void ErrorCode_IsDefinitiveFailure_MatchesExpectation(string errorCode, bool shouldClear)
    {
        // This mirrors the hydration logic: only clear on definitive failures
        var isDefinitiveFailure = errorCode is "invalid_key" or "insufficient_credits";
        isDefinitiveFailure.Should().Be(shouldClear);
    }

    #endregion

    #region Helpers

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    #endregion
}
