// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// Configuration for the local Chatterbox narration engine (uv-managed Python sidecar).
/// </summary>
public class ChatterboxConfiguration
{
    public const string SectionName = "Chatterbox";

    /// <summary>
    /// Gets the uv executable — a bare name resolved via PATH or an absolute path.
    /// Tests point this at python3 (with <see cref="UvArgs"/> empty) to drive the fake worker.
    /// </summary>
    public string UvPath { get; init; } = "uv";

    /// <summary>
    /// Gets the arguments prepended before the worker script path. Empty/whitespace
    /// means the worker path is the only argument (the test seam for the fake worker).
    /// <c>setuptools&lt;81</c> is required because Chatterbox's Perth watermarker imports
    /// <c>pkg_resources</c>, which setuptools 81+ removed — without it the model init
    /// throws "'NoneType' object is not callable" (the watermarker class silently
    /// imports as None).
    /// </summary>
    public string UvArgs { get; init; } = "run --python 3.11 --with chatterbox-tts==0.1.7 --with \"setuptools<81\"";

    /// <summary>
    /// Gets the worker script path, resolved against <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public string WorkerRelativePath { get; init; } = Path.Combine("chatterbox-worker", "chatterbox_worker.py");

    /// <summary>
    /// Gets the torch device preference: auto, cuda, mps, or cpu.
    /// </summary>
    public string Device { get; init; } = "auto";

    /// <summary>
    /// Gets the classifier-free-guidance weight (pacing). Advanced, config-only — no UI row.
    /// </summary>
    public float CfgWeight { get; init; } = 0.5f;

    /// <summary>
    /// Gets the exaggeration used when the ChatterboxExaggeration settings key is absent.
    /// </summary>
    public float DefaultExaggeration { get; init; } = 0.5f;

    /// <summary>
    /// Gets the maximum characters per generate() call — Chatterbox drifts past ~300.
    /// </summary>
    public int MaxChunkSize { get; init; } = 300;

    /// <summary>
    /// Gets the budget from spawn to the worker's ready banner. Generous because the very
    /// first <c>uv run --with ...</c> builds the Python env (multi-GB torch download) BEFORE
    /// the worker prints its banner; the sidecar streams uv's stderr as progress meanwhile.
    /// </summary>
    public int StartTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Gets the budget for model load including the first-run weight download.
    /// </summary>
    public int LoadTimeoutSeconds { get; init; } = 1800;

    /// <summary>
    /// Gets the per-chunk speak budget once the model is loaded (CPU can be ~40s/100 words).
    /// </summary>
    public int SpeakTimeoutSecondsPerChunk { get; init; } = 600;
}
