// <copyright file="AudioMetadata.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Domain.ValueObjects;

/// <summary>
/// Value object representing audio file metadata
/// </summary>
public record AudioMetadata(
    string Codec,
    int BitRate,
    int SampleRate,
    int Channels,
    int DurationMs,
    long FileSizeBytes)
{
    public double DurationSeconds => DurationMs / 1000.0;
    public double DurationMinutes => DurationSeconds / 60.0;
    public double FileSizeMB => FileSizeBytes / (1024.0 * 1024.0);

    public static AudioMetadata Default => new(
        Codec: "aac",
        BitRate: 64000,
        SampleRate: 44100,
        Channels: 1,
        DurationMs: 0,
        FileSizeBytes: 0
    );
}
