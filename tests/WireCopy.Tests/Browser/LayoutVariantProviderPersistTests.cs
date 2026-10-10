// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class LayoutVariantProviderPersistTests
{
    // Guards:   when saving the chosen layout variant fails (disk/DB error), cycling still works in
    //           memory AND a Warning carrying the exception is logged instead of failing silently.
    // Fails if: PersistPreference goes back to a bare `catch { }`, or lets the exception escape.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~LayoutVariantProviderPersistTests"
    [Fact]
    public void CycleVariant_WhenPersistFails_StillCyclesAndLogsWarningWithException()
    {
        var failure = new IOException("disk full");
        var store = Substitute.For<IUserSettingsStore>();
        store.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())).Do(_ => throw failure);
        var logger = new RecordingLogger();
        var provider = new LayoutVariantProvider(store, logger);

        var before = provider.GetCurrentVariant(ViewMode.Launcher);
        var after = provider.CycleVariant(ViewMode.Launcher);

        after.Should().NotBe(before, "the in-memory cycle must succeed even though saving failed");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && ReferenceEquals(e.Exception, failure));
    }

    private sealed class RecordingLogger : ILogger<LayoutVariantProvider>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }
}
