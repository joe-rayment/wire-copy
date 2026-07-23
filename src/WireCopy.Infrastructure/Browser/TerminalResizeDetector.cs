// Licensed under the MIT License. See LICENSE in the repository root.

using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Polls the terminal size at 100ms intervals to detect resizes. Writes to a bounded channel
/// (capacity 1, DropOldest) so rapid resizes coalesce naturally.
///
/// <para>workspace-pxao.2: this runs on a BACKGROUND thread while <see cref="UI.TerminalInputHandler"/>'s
/// key-reader thread owns stdin via <c>Console.ReadKey</c>. The old <c>Console.CursorTop</c> cache-flush
/// workaround for dotnet/runtime#41274 did a DSR (<c>ESC[6n</c>) round-trip that WRITES stdout and READS
/// stdin — racing the key reader for fd 0. After an extension reattach the fresh overlay's xterm did not
/// answer the DSR promptly, so <c>CursorTop</c> blocked holding .NET's stdin lock and starved
/// <c>Console.ReadKey</c>, severing the child's keyboard input (the user's "second article hangs" bug).
/// We now read the size via a direct <c>TIOCGWINSZ</c> ioctl, which never touches stdin and is always
/// fresh (the kernel value), with <c>Console.WindowWidth/Height</c> as a managed fallback.</para>
/// </summary>
public sealed class TerminalResizeDetector : IResizeDetector
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<TerminalResizeDetector> _logger;
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true
        });

    private bool _fallbackLogged;

    public TerminalResizeDetector(ILogger<TerminalResizeDetector> logger)
    {
        _logger = logger;
    }

    public ChannelReader<bool> Resizes => _channel.Reader;

    /// <summary>
    /// Reads the CURRENT terminal column/row count straight from the kernel via <c>TIOCGWINSZ</c>
    /// (workspace-mopz.8/.9). <c>Console.WindowWidth</c> is cached by .NET and does NOT refresh on SIGWINCH
    /// (dotnet/runtime#41274) — the very reason this detector exists — so a resize-triggered re-render that
    /// reads <c>Console.WindowWidth</c> paints at the STALE width (the launcher black-band / clipped bug).
    /// Callers that must render at the live width use this. Returns false on non-Unix or when the ioctl is
    /// unavailable, so the caller falls back to the managed Console properties (fresh on Windows).
    /// </summary>
    public static bool TryReadFreshDimensions(out int width, out int height)
        => Native.TryReadWinSize(out width, out height) && width > 0 && height > 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ReadDimensions(out var lastWidth, out var lastHeight);

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadDimensions(out var width, out var height);

            if (width != lastWidth || height != lastHeight)
            {
                lastWidth = width;
                lastHeight = height;
                _channel.Writer.TryWrite(true);

                // workspace-7htl: wall-clock anchor for the resize→wire latency breakdown —
                // correlate with the [resize-timing] line ViewCommandHandler logs when the
                // rewrap frame has been written.
                _logger.LogInformation("[resize-timing] detected {Width}x{Height}", width, height);
            }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
    }

    private void ReadDimensions(out int width, out int height)
    {
        try
        {
            // workspace-pxao.2: get the size via TIOCGWINSZ on the terminal fd — a pure ioctl that never
            // reads stdin (unlike the old Console.CursorTop DSR workaround, which raced the key reader and
            // severed input after a reattach). Always returns the current kernel value, so no stale-cache
            // workaround is needed. Falls back to the managed Console properties when the ioctl is unavailable.
            if (Native.TryReadWinSize(out var w, out var h) && w > 0 && h > 0)
            {
                width = w;
                height = h;
                return;
            }

            width = Console.WindowWidth;
            height = Console.WindowHeight;
        }
        catch (Exception ex)
        {
            width = 80;
            height = 24;

            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                _logger.LogWarning(ex, "Console dimensions unavailable, using default {Width}x{Height}", width, height);
            }
        }
    }

    /// <summary>
    /// Native terminal-size query via <c>TIOCGWINSZ</c>. Kept in a nested helper so the P/Invoke and its
    /// platform request codes are isolated from the managed polling logic.
    /// </summary>
    private static class Native
    {
        // TIOCGWINSZ request code (Linux). macOS uses a different (_IOR) encoding, but we no longer issue this
        // ioctl on macOS at all (workspace-2mn3) — see TryReadWinSize — so only the Linux code is needed here.
        private const ulong TiocgwinszLinux = 0x5413;

        /// <summary>
        /// Reads the terminal size via <c>TIOCGWINSZ</c> on stdout (fd 1) — a pure ioctl with no stdin
        /// access. Returns false on non-Unix or when the ioctl fails, so the caller falls back to Console.
        /// </summary>
        public static bool TryReadWinSize(out int width, out int height)
        {
            width = 0;
            height = 0;

            // workspace-2mn3 — LINUX ONLY. `ioctl(2)` is VARIADIC (`int ioctl(int, unsigned long, ...)`), and
            // its winsize-pointer argument must be passed through the C varargs ABI. On Apple arm64 variadic
            // args are passed on the STACK (not in registers) — a fixed-signature P/Invoke (`ref WinSize`) put
            // the pointer in register x2, so the kernel wrote the 8-byte winsize to a GARBAGE address =>
            // System.AccessViolationException that crash-LOOPED the child (the workspace-2mn3 / xink crash
            // screenshots, macOS only). Linux/AAPCS64 does NOT split the variadic ABI, so the fixed signature
            // is correct there — and it's the only platform we can headfully verify. So we run this P/Invoke
            // ONLY on Linux; macOS (and Windows) fall through to the managed Console.WindowWidth/Height below,
            // which query the size via the .NET runtime's OWN (correctly-compiled, varargs-safe) TIOCGWINSZ.
            // This follows the project rule: don't ship macOS-fragile native code we can't verify in-env.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return false;
            }

            try
            {
                var ws = default(WinSize);

                // fd 1 (stdout) is the same PTY as stdin here; querying it avoids any interaction with the
                // key-reader thread that owns stdin via Console.ReadKey.
                if (Ioctl(1, TiocgwinszLinux, ref ws) == 0)
                {
                    width = ws.Cols;
                    height = ws.Rows;
                    return true;
                }
            }
            catch (DllNotFoundException)
            {
                // No libc (shouldn't happen on Linux) — fall back to managed Console.
            }
            catch (EntryPointNotFoundException)
            {
                // ioctl unavailable — fall back.
            }

            return false;
        }

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, ulong request, ref WinSize ws);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinSize
        {
            public ushort Rows;
            public ushort Cols;
            public ushort XPixels;
            public ushort YPixels;
        }
    }
}
