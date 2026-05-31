// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Tests;

/// <summary>
/// workspace-frpl.7 — shared deterministic <see cref="TimeProvider"/> for tests
/// (promoted from AutoCookieRefresherTests). <see cref="LocalTimeZone"/> is UTC
/// so <c>GetLocalNow()</c> is deterministic.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTime utcNow)
        : this(new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)))
    {
    }

    public FakeTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now = _now.Add(span);

    public void Set(DateTimeOffset now) => _now = now;
}
