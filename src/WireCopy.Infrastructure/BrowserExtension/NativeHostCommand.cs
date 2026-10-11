// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// How to start this app again in native-messaging-host mode: either the app's own executable
/// (published apphost) or <c>dotnet &lt;entry.dll&gt;</c> (running from <c>dotnet run</c>/build output).
/// </summary>
/// <param name="Executable">The process to run: the apphost, or the <c>dotnet</c> muxer.</param>
/// <param name="EntryDll">The entry assembly to pass to <c>dotnet exec</c>; null for an apphost.</param>
public sealed record NativeHostCommand(string Executable, string? EntryDll)
{
    /// <summary>Describes how the running process was started.</summary>
    public static NativeHostCommand FromCurrentProcess()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process path is unknown.");

        return string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new NativeHostCommand(processPath, Assembly.GetEntryAssembly()!.Location)
            : new NativeHostCommand(processPath, null);
    }
}
