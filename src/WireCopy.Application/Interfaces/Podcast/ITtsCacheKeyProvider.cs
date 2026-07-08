// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces.Podcast;

/// <summary>Supplies the engine+settings fingerprint that partitions the TTS audio cache.</summary>
public interface ITtsCacheKeyProvider
{
    /// <summary>
    /// Stable string describing everything that changes generated audio for the ACTIVE engine
    /// (engine name, voice/model/instructions or sample/exaggeration, speed, format).
    /// </summary>
    string GetTtsConfigCacheComponent();
}
