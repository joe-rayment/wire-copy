// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.Chatterbox;

/// <summary>Worker verdict for one speak request; <see cref="Error"/> is set when <see cref="Ok"/> is false.</summary>
internal sealed record ChatterboxSpeakResult(bool Ok, string? OutPath, double AudioSeconds, string? Error);
