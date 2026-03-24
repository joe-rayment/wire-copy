// Educational and personal use only.

using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser.Themes;

/// <summary>
/// Singleton theme provider that persists the user's theme preference to disk.
/// </summary>
public sealed class ThemeProvider : IThemeProvider
{
    private static readonly ThemeName[] ThemeOrder =
    [
        ThemeName.Phosphor,
        ThemeName.Amber,
        ThemeName.Dracula,
        ThemeName.Light,
    ];

    private static readonly string PersistencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TermReader",
        "theme.txt");

    private ThemeName _currentTheme;

    public ThemeProvider()
    {
        _currentTheme = LoadPersistedTheme();
    }

    /// <inheritdoc />
    public ThemeName CurrentTheme => _currentTheme;

    /// <inheritdoc />
    public ThemeName CycleTheme()
    {
        var currentIndex = Array.IndexOf(ThemeOrder, _currentTheme);
        var nextIndex = (currentIndex + 1) % ThemeOrder.Length;
        var next = ThemeOrder[nextIndex];
        SetTheme(next);
        return next;
    }

    /// <inheritdoc />
    public void SetTheme(ThemeName theme)
    {
        _currentTheme = theme;
        PersistTheme(theme);
    }

    private static ThemeName LoadPersistedTheme()
    {
        try
        {
            if (File.Exists(PersistencePath))
            {
                var text = File.ReadAllText(PersistencePath).Trim();
                if (Enum.TryParse<ThemeName>(text, ignoreCase: true, out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch (IOException)
        {
            // Best-effort: fall through to default.
        }

        return ThemeName.Phosphor;
    }

    private static void PersistTheme(ThemeName theme)
    {
        try
        {
            var directory = Path.GetDirectoryName(PersistencePath)!;
            Directory.CreateDirectory(directory);

            var tempPath = PersistencePath + ".tmp";
            File.WriteAllText(tempPath, theme.ToString());
            File.Move(tempPath, PersistencePath, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort: silently ignore persistence failures.
        }
    }
}
