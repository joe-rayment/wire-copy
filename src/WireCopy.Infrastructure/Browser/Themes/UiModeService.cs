// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser.Themes;

/// <summary>Singleton holder of the current <see cref="UiMode"/>.</summary>
public sealed class UiModeService : IUiModeService
{
    private volatile UiMode _current = UiMode.Normal;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public UiMode Current => _current;

    /// <inheritdoc />
    public void Set(UiMode mode)
    {
        if (_current == mode)
        {
            return;
        }

        _current = mode;
        Changed?.Invoke();
    }
}
