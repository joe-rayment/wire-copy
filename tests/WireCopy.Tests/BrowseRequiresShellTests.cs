// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.API;
using WireCopy.Infrastructure.Browser.Shell;
using Xunit;

namespace WireCopy.Tests;

/// <summary>
/// workspace-lizq.2: the interactive reader runs only inside the desktop shell. Without
/// WIRECOPY_SHELL_CHANNEL, browse must refuse with a clear error and a nonzero exit —
/// the terminal reader is unreachable, not half-supported. Env vars are process-global,
/// hence the serialized collection.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class BrowseRequiresShellTests
{
    public static TheoryData<string[]> BrowseArgForms => new()
    {
        Array.Empty<string>(),
        new[] { "browse" },
        new[] { "browse", "https://example.com" },
        new[] { "https://example.com" },
    };

    [Theory]
    [MemberData(nameof(BrowseArgForms))]
    public async Task Browse_WithoutShellChannel_PrintsErrorAndExitsNonzero(string[] args)
    {
        var savedChannel = Environment.GetEnvironmentVariable(ShellChannel.EnvVar);
        var originalErr = Console.Error;
        try
        {
            Environment.SetEnvironmentVariable(ShellChannel.EnvVar, null);
            using var err = new StringWriter();
            Console.SetError(err);

            var exitCode = await Program.Main(args);

            exitCode.Should().NotBe(0);
            err.ToString().Should().Contain("WireCopy is a desktop app — launch it with ./run");
        }
        finally
        {
            Console.SetError(originalErr);
            Environment.SetEnvironmentVariable(ShellChannel.EnvVar, savedChannel);
        }
    }
}
