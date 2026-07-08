// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.Chatterbox;

/// <summary>One speak request: text plus clone/expressiveness knobs and the wav destination.</summary>
internal sealed record ChatterboxSpeakRequest(string Id, string Text, string? SamplePath, float Exaggeration, float CfgWeight, string OutPath);
