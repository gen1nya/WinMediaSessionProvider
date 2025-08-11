namespace NativeMediaSession;

public record FullMediaState
{
    public string title { get; init; } = string.Empty;
    public string artist { get; init; } = string.Empty;
    public string albumTitle { get; init; } = string.Empty;
    public string? albumArtBase64 { get; init; }
    public string status { get; init; } = string.Empty;
    public double duration { get; init; }
    public double position { get; init; }
}
