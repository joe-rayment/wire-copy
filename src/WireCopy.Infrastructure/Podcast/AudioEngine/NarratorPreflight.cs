// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Podcast;

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>
/// workspace-b6cmm — the narrator readiness check shared by the manual podcast flow and
/// routine runs, plus the "start a local audio-engine that isn't answering" step.
/// </summary>
internal static class NarratorPreflight
{
    /// <summary>Asks the narrator whether it is ready, with a 5 s budget; a timeout is a synthetic not-ready status.</summary>
    public static async Task<NarratorStatus> CheckAsync(IPodcastNarrator narrator, CancellationToken ct)
    {
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromSeconds(5));
            return await narrator.CheckAsync(checkCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NarratorStatus
            {
                Ready = false,
                ErrorKind = NarrationErrorKind.Unreachable,
                Message = "audio-engine isn't running or isn't answering (no reply within 5 seconds). "
                    + "Start it (audio-engine README: Run natively) or change its address in Settings.",
            };
        }
    }

    /// <summary>Checks; when a local engine is unreachable and a launcher exists, starts it and checks again.</summary>
    public static async Task<NarratorStatus> EnsureReadyAsync(
        IPodcastNarrator narrator, IAudioEngineLauncher? launcher, CancellationToken ct)
    {
        var status = await CheckAsync(narrator, ct).ConfigureAwait(false);

        // An engine that answers at all tells us where it lives, even when it is not usable yet
        // (engine not ready, wants an API key) — remember it for the day it is not running.
        if (status.Reachable && launcher != null && AudioEngineLauncher.IsLocal(status.Url))
        {
            launcher.NoteRunning(status.Url);
        }

        if (!status.Ready
            && status.ErrorKind == NarrationErrorKind.Unreachable
            && launcher != null
            && AudioEngineLauncher.IsLocal(status.Url))
        {
            var start = await launcher.StartAndWaitAsync(status.Url, ct).ConfigureAwait(false);
            if (start.Ready)
            {
                status = await CheckAsync(narrator, ct).ConfigureAwait(false);
                if (status.Reachable)
                {
                    launcher.NoteRunning(status.Url);
                }
            }
            else if (!string.IsNullOrWhiteSpace(start.Message))
            {
                status = status with { Message = start.Message };
            }
        }

        return status;
    }
}
