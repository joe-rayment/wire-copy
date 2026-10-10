// Licensed under the MIT License. See LICENSE in the repository root.

using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-y3eg7: failure-handling tests for OpenAiTtsService, run through the REAL OpenAI SDK
/// pointed at a loopback <see cref="HttpListener"/> (OpenAI itself is never contacted).
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiTtsServiceFailureTests
{
    private static readonly byte[] FakeAudio = [0x49, 0x44, 0x33, 0x04, 0x00, 0x01, 0x02, 0x03];

    // Guards:   OpenAI's permanent "insufficient_quota" 429 (billing exhausted) is NOT retried — one request, a clear
    //           "quota exhausted" message, and the result is flagged fatal so the podcast run can stop.
    // Fails if: the 429 retry arm is allowed to swallow insufficient_quota (MaxRetries=2 would then send 3 requests),
    //           or the fatal flag is dropped on the way out of GenerateAudioAsync.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~InsufficientQuota429_FailsOnFirstAttempt"
    [Fact]
    public async Task InsufficientQuota429_FailsOnFirstAttempt_AsFatalQuotaExhausted()
    {
        const string body = """{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","param":null,"code":"insufficient_quota"}}""";
        await using var server = LoopbackServer.Start((_, ctx) => Respond(ctx, 429, body, "application/json"));
        var sut = CreateSut(server);

        var result = await sut.GenerateAudioAsync("Hello world.", "Title");

        server.RequestCount.Should().Be(1, "a billing-exhausted 429 can never succeed on retry");
        result.Success.Should().BeFalse();
        result.IsFatal.Should().BeTrue();
        result.ErrorMessage.Should().Contain("quota exhausted");
    }

    // Guards:   an ordinary rate-limit 429 (not billing) is still retried with backoff and can succeed — the quota
    //           special-case must not swallow transient throttling.
    // Fails if: IsInsufficientQuota matches every 429, so the first throttle becomes a fatal failure.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~TransientRateLimit429_IsStillRetried"
    [Fact]
    public async Task TransientRateLimit429_IsStillRetried_ThenSucceeds()
    {
        const string body = """{"error":{"message":"Rate limit reached for tts-1 in organization org-x on requests per min.","type":"requests","code":"rate_limit_exceeded"}}""";
        await using var server = LoopbackServer.Start((n, ctx) => n == 1
            ? Respond(ctx, 429, body, "application/json")
            : Respond(ctx, 200, FakeAudio, "audio/mpeg"));
        var sut = CreateSut(server);

        var result = await sut.GenerateAudioAsync("Hello world.", "Title");

        server.RequestCount.Should().Be(2);
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.IsFatal.Should().BeFalse();
        result.AudioData.Should().Equal(FakeAudio);
    }

    // Guards:   a revoked/invalid API key (HTTP 401) is reported as a fatal failure so the run stops instead of
    //           repeating the doomed request for every remaining article.
    // Fails if: the 401/403 arm stops setting isFatal, or the flag is lost between chunk result and TTS result.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~Unauthorized401_IsFatal"
    [Fact]
    public async Task Unauthorized401_IsFatal_AndNotRetried()
    {
        await using var server = LoopbackServer.Start((_, ctx) =>
            Respond(ctx, 401, """{"error":{"message":"Incorrect API key provided.","code":"invalid_api_key"}}""", "application/json"));
        var sut = CreateSut(server);

        var result = await sut.GenerateAudioAsync("Hello world.", "Title");

        server.RequestCount.Should().Be(1);
        result.Success.Should().BeFalse();
        result.IsFatal.Should().BeTrue();
        result.ErrorMessage.Should().Contain("API key");
    }

    // Guards:   the SDK's own network timeout (surfaced as a TaskCanceledException while the caller's token is NOT
    //           cancelled) is treated as a transient transport failure and retried — not as a user cancellation.
    // Fails if: the retry loop lets a bare OperationCanceledException escape (the orchestrator would then report a
    //           user-cancelled run), or retries are skipped for timeouts.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~SdkNetworkTimeout_IsRetried"
    [Fact]
    public async Task SdkNetworkTimeout_IsRetried_NotReportedAsUserCancel()
    {
        await using var server = LoopbackServer.Start(async (n, ctx) =>
        {
            if (n == 1)
            {
                // Hold the first request open well past the SDK's 150 ms NetworkTimeout.
                await Task.Delay(TimeSpan.FromSeconds(3));
                return;
            }

            await Respond(ctx, 200, FakeAudio, "audio/mpeg");
        });
        var sut = CreateSut(server, o => o.NetworkTimeout = TimeSpan.FromMilliseconds(150));

        var result = await sut.GenerateAudioAsync("Hello world.", "Title", cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        server.RequestCount.Should().BeGreaterThanOrEqualTo(2, "the timed-out first attempt must be retried");
        result.AudioData.Should().Equal(FakeAudio);
    }

    // Guards:   a REAL cancellation (the caller's token fired) still propagates as cancellation even though timeouts
    //           are now retried — only an un-cancelled token makes an OperationCanceledException "transient".
    // Fails if: IsSdkTransportFailure ignores the token, so a user cancel gets retried/swallowed as a failure.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~IsSdkTransportFailure_OnlyClaimsCancellation"
    [Fact]
    public void IsSdkTransportFailure_OnlyClaimsCancellation_WhenCallerTokenIsLive()
    {
        using var cts = new CancellationTokenSource();
        var timeout = new TaskCanceledException("The operation was canceled.");
        var wrapped = new AggregateException("Retry failed after 3 tries.", timeout);

        OpenAiTtsService.IsSdkTransportFailure(timeout, cts.Token).Should().BeTrue();
        OpenAiTtsService.IsSdkTransportFailure(wrapped, cts.Token).Should().BeTrue();
        OpenAiTtsService.IsSdkTransportFailure(new ArgumentException("nope"), cts.Token).Should().BeFalse();

        cts.Cancel();

        OpenAiTtsService.IsSdkTransportFailure(timeout, cts.Token).Should().BeFalse();
        OpenAiTtsService.IsSdkTransportFailure(wrapped, cts.Token).Should().BeFalse();
    }

    private static OpenAiTtsService CreateSut(LoopbackServer server, Action<OpenAIClientOptions>? extra = null)
    {
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = "test-key",
            MaxRetries = 2,
            RetryBaseDelayMs = 1,
            InterChunkDelayMs = 0,
        });
        return new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance)
        {
            ConfigureClientOptions = o =>
            {
                o.Endpoint = new Uri($"{server.BaseUrl}v1");

                // No SDK-level retries: every request the server sees is one OUR retry loop sent.
                o.RetryPolicy = new ClientRetryPolicy(0);
                extra?.Invoke(o);
            },
        };
    }

    private static Task Respond(HttpListenerContext ctx, int status, string body, string contentType) =>
        Respond(ctx, status, Encoding.UTF8.GetBytes(body), contentType);

    private static async Task Respond(HttpListenerContext ctx, int status, byte[] body, string contentType)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }

    /// <summary>Minimal loopback HTTP server; the handler receives the 1-based request number.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<int, HttpListenerContext, Task> _handler;
        private readonly Task _loop;
        private int _count;

        private LoopbackServer(Func<int, HttpListenerContext, Task> handler)
        {
            _handler = handler;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public int RequestCount => Volatile.Read(ref _count);

        public static LoopbackServer Start(Func<int, HttpListenerContext, Task> handler) => new(handler);

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // listener torn down while accepting — expected
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var n = Interlocked.Increment(ref _count);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _handler(n, ctx);
                    }
                    catch (Exception)
                    {
                        // client hung up (e.g. SDK timeout) — fine for a test server
                    }
                });
            }
        }
    }
}
