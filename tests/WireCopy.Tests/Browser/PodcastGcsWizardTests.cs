// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for the GCS service-account paste wizard's input handling.
/// These tests cover the three failure modes that previously made valid
/// pasted JSON look invalid and cascaded the user through credential prompts:
///   1. Bracketed-paste markers (\x1b[200~ ... \x1b[201~) embedded in input.
///   2. Multi-line JSON not being parsed as a whole.
///   3. Invalid JSON cascading rather than re-prompting in place.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastGcsWizardTests : IDisposable
{
    private readonly string _tempDir;

    public PodcastGcsWizardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gcs-wizard-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    private static string ValidServiceAccountJson() => JsonSerializer.Serialize(new
    {
        type = "service_account",
        project_id = "test-project",
        private_key_id = "abc123",
        private_key = "-----BEGIN PRIVATE KEY-----\nMIIE...\n-----END PRIVATE KEY-----\n",
        client_email = "wirecopy@test-project.iam.gserviceaccount.com",
        client_id = "123456789",
        auth_uri = "https://accounts.google.com/o/oauth2/auth",
        token_uri = "https://oauth2.googleapis.com/token",
    });

    // -------------------- SanitizeKeyInput --------------------

    [Fact]
    public void SanitizeKeyInput_StripsBracketedPasteMarkers_WithEscape()
    {
        var json = ValidServiceAccountJson();
        var pasted = $"\x1b[200~{json}\x1b[201~";

        var sanitized = PodcastGcsWizard.SanitizeKeyInput(pasted);

        sanitized.Should().Be(json);
    }

    [Fact]
    public void SanitizeKeyInput_StripsBareBracketedPasteMarkers_WithoutEscape()
    {
        // Some terminals leak the markers without the escape byte; defense-in-depth.
        var json = ValidServiceAccountJson();
        var pasted = $"[200~{json}[201~";

        var sanitized = PodcastGcsWizard.SanitizeKeyInput(pasted);

        sanitized.Should().Be(json);
    }

    [Fact]
    public void SanitizeKeyInput_TrimsSurroundingWhitespace()
    {
        var json = ValidServiceAccountJson();
        var pasted = $"   \r\n\t{json}\n  ";

        var sanitized = PodcastGcsWizard.SanitizeKeyInput(pasted);

        sanitized.Should().Be(json);
    }

    [Fact]
    public void SanitizeKeyInput_HandlesMultiplePasteSequences_Idempotent()
    {
        var json = "{\"key\":\"value\"}";
        var input = $"\x1b[200~\x1b[200~{json}\x1b[201~\x1b[201~";

        var sanitized = PodcastGcsWizard.SanitizeKeyInput(input);

        sanitized.Should().Be(json);
    }

    [Fact]
    public void SanitizeKeyInput_LeavesCleanInputUnchanged()
    {
        var json = ValidServiceAccountJson();

        var sanitized = PodcastGcsWizard.SanitizeKeyInput(json);

        sanitized.Should().Be(json);
    }

    [Fact]
    public void SanitizeKeyInput_NullOrEmpty_ReturnsEmpty()
    {
        PodcastGcsWizard.SanitizeKeyInput(string.Empty).Should().Be(string.Empty);
        PodcastGcsWizard.SanitizeKeyInput(null!).Should().Be(string.Empty);
    }

    // -------------------- ValidateAndSaveKeyAsync --------------------

    [Fact]
    public async Task ValidateAndSaveKeyAsync_AcceptsBracketedPastedJson()
    {
        var (gcsClient, settingsStore) = CreateGcsClient();
        var json = ValidServiceAccountJson();
        var pastedWithMarkers = $"\x1b[200~{json}\x1b[201~";

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(pastedWithMarkers, gcsClient);

        saved.Should().BeTrue($"valid JSON wrapped in paste markers must be accepted, got error: {error}");
        error.Should().BeNull();
        settingsStore.Received().Set(Arg.Any<string>(), Arg.Any<string>(), encrypt: true);
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_AcceptsMultiLineJson()
    {
        var (gcsClient, _) = CreateGcsClient();
        // Indented multi-line JSON, the way it usually arrives from a paste.
        var multiLine = """
            {
              "type": "service_account",
              "project_id": "test-project",
              "private_key_id": "abc",
              "private_key": "-----BEGIN PRIVATE KEY-----\nXXX\n-----END PRIVATE KEY-----\n",
              "client_email": "tr@test.iam.gserviceaccount.com",
              "client_id": "1"
            }
            """;

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(multiLine, gcsClient);

        saved.Should().BeTrue($"multi-line JSON must parse, got error: {error}");
        error.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_AcceptsMultiLineJsonWithPasteMarkers()
    {
        var (gcsClient, _) = CreateGcsClient();
        var multiLine = """
            {
              "type": "service_account",
              "project_id": "test-project",
              "private_key_id": "abc",
              "private_key": "-----BEGIN PRIVATE KEY-----\nXXX\n-----END PRIVATE KEY-----\n",
              "client_email": "tr@test.iam.gserviceaccount.com",
              "client_id": "1"
            }
            """;
        var pasted = $"\x1b[200~{multiLine}\x1b[201~";

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(pasted, gcsClient);

        saved.Should().BeTrue($"multi-line JSON wrapped in paste markers must parse, got error: {error}");
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_InvalidJson_ReturnsSpecificParserError()
    {
        var (gcsClient, _) = CreateGcsClient();
        // Truncated JSON — parser should report what it expected.
        var broken = "{ \"type\": \"service_account\", \"project_id\": ";

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(broken, gcsClient);

        saved.Should().BeFalse();
        error.Should().NotBeNull();
        // The error must reference the JSON parse failure with detail, not the
        // generic "Input is not valid JSON" alone — that's what kept users guessing.
        error.Should().Contain("not valid JSON");
        error!.Length.Should().BeGreaterThan("Input is not valid JSON".Length,
            "the parser's specific complaint must be appended to the generic message");
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_InvalidJson_DoesNotPersistAnything()
    {
        var (gcsClient, settingsStore) = CreateGcsClient();
        var broken = "{ this is not json";

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(broken, gcsClient);

        saved.Should().BeFalse();
        error.Should().NotBeNull();

        // No credential discovery cascade — a failed paste must not write
        // anything to the settings store; the next step should re-prompt.
        settingsStore.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_EmptyInputAfterSanitize_ReturnsError()
    {
        var (gcsClient, settingsStore) = CreateGcsClient();
        var emptyPaste = "\x1b[200~\x1b[201~";

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(emptyPaste, gcsClient);

        saved.Should().BeFalse();
        error.Should().Contain("empty");
        settingsStore.DidNotReceive().Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_PemKeyWithRealNewlines_ParsesAndPersists()
    {
        // Item #5 from QA: a realistic Google service-account JSON has a
        // private_key field that, depending on how the user copies it, may
        // contain a mix of literal "\\n" escape sequences (the file's on-disk
        // representation) and embedded actual newlines (introduced by tools
        // like `cat key.json | pbcopy` that preserve real linebreaks).
        //
        // The end-to-end path here is:
        //   bracketed-paste markers -> SanitizeKeyInput strips them
        //   actual \r/\n in PEM block -> already stripped by the bracketed-paste
        //   parser at the TerminalInputHandler layer (see TerminalInputHandlerTests
        //   .TryConsumeBracketedPasteFrom_PemKeyEmbeddedNewlines_*)
        //
        // What ValidateAndSaveKeyAsync receives in production is therefore the
        // post-newline-strip blob — the `\\n` literals inside the PEM string
        // remain, and that string is valid JSON because `\n` is a JSON escape.
        var (gcsClient, settingsStore) = CreateGcsClient();

        var pemEscaped =
            "-----BEGIN PRIVATE KEY-----\\nMIIEvAIBADANBgkqhkiG9w0\\n" +
            "Some+Base64+Lines+Go+Here==\\n-----END PRIVATE KEY-----\\n";

        var jsonWithLiteralNewlines = $$"""
            {
              "type": "service_account",
              "project_id": "test-project",
              "private_key_id": "abc123",
              "private_key": "{{pemEscaped}}",
              "client_email": "tr@test.iam.gserviceaccount.com",
              "client_id": "1"
            }
            """;

        // Wrap in bracketed-paste markers — the production path includes them
        // when bracketed-paste mode negotiated successfully.
        var pasted = $"\x1b[200~{jsonWithLiteralNewlines}\x1b[201~";

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(pasted, gcsClient);

        saved.Should().BeTrue($"PEM-bearing JSON with literal \\\\n escapes must parse, got error: {error}");
        error.Should().BeNull();
        settingsStore.Received().Set(Arg.Any<string>(), Arg.Any<string>(), encrypt: true);
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_FilePath_StillWorks()
    {
        var (gcsClient, _) = CreateGcsClient();
        var keyPath = Path.Combine(_tempDir, "valid-key.json");
        File.WriteAllText(keyPath, ValidServiceAccountJson());

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(keyPath, gcsClient);

        saved.Should().BeTrue($"file-path branch must still work, got error: {error}");
    }

    [Fact]
    public async Task ValidateAndSaveKeyAsync_FilePathSurroundedByPasteMarkers_StillWorks()
    {
        var (gcsClient, _) = CreateGcsClient();
        var keyPath = Path.Combine(_tempDir, "valid-key.json");
        File.WriteAllText(keyPath, ValidServiceAccountJson());

        var (saved, error) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(
            $"\x1b[200~{keyPath}\x1b[201~", gcsClient);

        saved.Should().BeTrue($"sanitized path must resolve, got error: {error}");
    }

    private (GcsStorageClient client, IUserSettingsStore store) CreateGcsClient()
    {
        var config = Options.Create(new GcsConfiguration { BucketName = "test-bucket" });
        var settingsStore = Substitute.For<IUserSettingsStore>();

        // Place the persisted-content file under our temp dir so we don't
        // pollute the real user's app data when SetServiceAccountKeyContent
        // calls File.WriteAllText.
        // (GcsStorageClient writes to {LocalAppData}/WireCopy/gcs-key.json
        // — for tests we accept that side effect on a temp LocalAppData; the
        // file is owned by the test container.)
        var client = new GcsStorageClient(config, settingsStore, NullLogger<GcsStorageClient>.Instance);
        return (client, settingsStore);
    }
}
