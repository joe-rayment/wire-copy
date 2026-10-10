// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-up3zx: <see cref="PromptLineMirror"/> wraps the ':' / '/' prompt and
/// pushes its prefix/buffer/cursor to the HTML pane. These tests pin the order of
/// what it exports and that it always clears the pane when the prompt ends.
/// </summary>
[Trait("Category", "Unit")]
public class PromptLineMirrorTests
{
    // Guards:   the pane sees the prompt open (empty buffer), every edit in typing order, then a clear (null) --
    //           and the typed text still comes back to the caller untouched.
    // Fails if: RunAsync skips the opening push, drops an edit callback, reorders them, or forgets the final null.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLineMirrorTests.RunAsync_ExportsOpenThenEachEditThenNull"
    [Fact]
    public async Task RunAsync_ExportsOpenThenEachEditThenNull()
    {
        var sent = new List<PromptLineState?>();

        var result = await PromptLineMirror.RunAsync(
            line =>
            {
                sent.Add(line);
                return Task.CompletedTask;
            },
            ":",
            onChange =>
            {
                onChange!("h", 1);
                onChange("he", 2);
                onChange("hel", 3);
                return Task.FromResult<string?>("hel");
            });

        result.Should().Be("hel");
        sent.Should().HaveCount(5);
        sent[0].Should().Be(new PromptLineState { Prefix = ":", Buffer = "", Cursor = 0 });
        sent[1].Should().Be(new PromptLineState { Prefix = ":", Buffer = "h", Cursor = 1 });
        sent[2].Should().Be(new PromptLineState { Prefix = ":", Buffer = "he", Cursor = 2 });
        sent[3].Should().Be(new PromptLineState { Prefix = ":", Buffer = "hel", Cursor = 3 });
        sent[4].Should().BeNull("the pane's prompt row must clear when the prompt closes");
    }

    // Guards:   if the prompt blows up mid-typing, the pane's prompt row is still cleared -- it can never be left
    //           stuck on screen -- and the original exception reaches the caller.
    // Fails if: the final export(null) is moved out of the finally block so an exception skips it.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLineMirrorTests.RunAsync_WhenPromptThrows_StillExportsNull"
    [Fact]
    public async Task RunAsync_WhenPromptThrows_StillExportsNull()
    {
        var sent = new List<PromptLineState?>();

        var act = () => PromptLineMirror.RunAsync(
            line =>
            {
                sent.Add(line);
                return Task.CompletedTask;
            },
            "/",
            onChange =>
            {
                onChange!("x", 1);
                throw new InvalidOperationException("prompt exploded");
            });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("prompt exploded");
        sent.Should().NotBeEmpty();
        sent[^1].Should().BeNull("the clear must happen even when the prompt throws");
    }

    // Guards:   with mirroring switched off (null export -- terminal mode / no shell) the prompt runs exactly as
    //           before: no on-change callback is handed in and nothing is exported.
    // Fails if: RunAsync passes a callback anyway, or touches export when it is null (null-reference crash).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PromptLineMirrorTests.RunAsync_WithNullExport_RunsPromptWithoutCallback"
    [Fact]
    public async Task RunAsync_WithNullExport_RunsPromptWithoutCallback()
    {
        var calls = 0;
        Action<string, int>? received = (_, _) => { };

        var result = await PromptLineMirror.RunAsync(
            export: null,
            ":",
            onChange =>
            {
                calls++;
                received = onChange;
                return Task.FromResult<string?>("quit");
            });

        result.Should().Be("quit");
        calls.Should().Be(1);
        received.Should().BeNull();
    }
}
