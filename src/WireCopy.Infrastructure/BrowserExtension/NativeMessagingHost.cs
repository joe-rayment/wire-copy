// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// The message loop behind <c>WireCopy.API native-messaging-host</c> (workspace-m7aml): reads
/// framed JSON requests from the browser extension on stdin, answers each on stdout, and
/// returns on end of input. One bad message gets an error reply and never ends the loop.
/// </summary>
internal sealed class NativeMessagingHost
{
    private const string GenericSaveError = "Wire Copy couldn't save this page.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadingListCapture _capture;
    private readonly string _connectionsPath;
    private readonly string _browserId;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="NativeMessagingHost"/> class.</summary>
    /// <param name="capture">Saves pages to the Reading List and counts it.</param>
    /// <param name="connectionsPath">Where <see cref="BrowserConnections"/> records which browser reached the host.</param>
    /// <param name="browserId">The id of the browser that launched the host (see <see cref="ParentBrowser"/>).</param>
    /// <param name="logger">Log sink (a file; stdout belongs to the protocol).</param>
    public NativeMessagingHost(IReadingListCapture capture, string connectionsPath, string browserId, ILogger logger)
    {
        _capture = capture;
        _connectionsPath = connectionsPath;
        _browserId = browserId;
        _logger = logger;
    }

    /// <summary>Answers requests until the input ends.</summary>
    /// <param name="input">The browser's side of the pipe (stdin).</param>
    /// <param name="output">Our side of the pipe (stdout); nothing but framed replies may be written to it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the browser closes the pipe or sends an unreadable frame.</returns>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? raw;
            try
            {
                raw = await NativeMessagingFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (NativeMessagingFrameException ex)
            {
                // The stream is no longer in step with the protocol, so answer once and stop.
                _logger.LogWarning(ex, "Unreadable native-messaging frame; closing the connection");
                await TryWriteAsync(output, ErrorReply("unknown", ex.Message), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (raw is null)
            {
                return;
            }

            var reply = await HandleAsync(raw, cancellationToken).ConfigureAwait(false);
            await NativeMessagingFraming.WriteAsync(output, reply, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ErrorReply(string type, string error) =>
        JsonSerializer.Serialize(new { ok = false, type, error }, JsonOptions);

    private static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "dev";

    private async Task TryWriteAsync(Stream output, string json, CancellationToken cancellationToken)
    {
        try
        {
            await NativeMessagingFraming.WriteAsync(output, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NativeMessagingFrameException)
        {
            _logger.LogDebug(ex, "Could not deliver the error reply; the browser has gone");
        }
    }

    private async Task<string> HandleAsync(string raw, CancellationToken cancellationToken)
    {
        var type = "unknown";
        try
        {
            var request = JsonSerializer.Deserialize<Request>(raw, JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.Type))
            {
                return ErrorReply(type, "Wire Copy didn't understand that message.");
            }

            type = request.Type;
            switch (type)
            {
                case "hello":
                    RecordConnection(request.ExtensionVersion);
                    return JsonSerializer.Serialize(
                        new
                        {
                            ok = true,
                            type,
                            app = "Wire Copy",
                            appVersion = AppVersion(),
                            readingListCount = await _capture.ReadingListCountAsync(cancellationToken).ConfigureAwait(false),
                        },
                        JsonOptions);

                case "save":
                    RecordConnection(request.ExtensionVersion);
                    var saved = await _capture
                        .SaveAsync(request.Url ?? string.Empty, request.Title, request.Html, cancellationToken)
                        .ConfigureAwait(false);
                    return JsonSerializer.Serialize(
                        new
                        {
                            ok = true,
                            type,
                            title = saved.Title,
                            words = saved.Words,
                            source = saved.Source,
                            alreadySaved = saved.AlreadySaved,
                        },
                        JsonOptions);

                default:
                    return ErrorReply(type, "Wire Copy didn't understand that message.");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Native-messaging message was not valid JSON for this host");
            return ErrorReply(type, "Wire Copy couldn't read that message.");
        }
        catch (ArgumentException ex)
        {
            return ErrorReply(type, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Native-messaging '{Type}' request failed", type);
            return ErrorReply(type, GenericSaveError);
        }
    }

    private void RecordConnection(string? extensionVersion)
    {
        try
        {
            BrowserConnections.Record(_connectionsPath, _browserId, extensionVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the browser connection in {Path}", _connectionsPath);
        }
    }

    private sealed record Request(string? Type, string? Url, string? Title, string? Html, string? ExtensionVersion);
}
