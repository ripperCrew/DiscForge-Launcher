using System.Text.Json.Serialization;

namespace DiscForge.Core;

public sealed class MetadataDocument
{
    [JsonPropertyName("formatVersion")]
    public int? FormatVersion { get; set; }

    [JsonPropertyName("gameTitle")]
    public string? GameTitle { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("edition")]
    public string? Edition { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("filesystem")]
    public string? Filesystem { get; set; }

    [JsonPropertyName("artwork")]
    public ArtworkDocument? Artwork { get; set; }

    [JsonPropertyName("gameFiles")]
    public GameFilesDocument? GameFiles { get; set; }

    [JsonPropertyName("manifest")]
    public ManifestRefDocument? Manifest { get; set; }

    [JsonPropertyName("launcher")]
    public LauncherRefDocument? Launcher { get; set; }
}

public sealed class ArtworkDocument
{
    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }
}

public sealed class GameFilesDocument
{
    [JsonPropertyName("directory")]
    public string? Directory { get; set; }

    [JsonPropertyName("fileCount")]
    public long? FileCount { get; set; }

    [JsonPropertyName("totalBytes")]
    public long? TotalBytes { get; set; }
}

public sealed class ManifestRefDocument
{
    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }
}

public sealed class LauncherRefDocument
{
    [JsonPropertyName("file")]
    public string? File { get; set; }
}

public sealed class ManifestDocument
{
    [JsonPropertyName("formatVersion")]
    public int? FormatVersion { get; set; }

    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }

    [JsonPropertyName("root")]
    public string? Root { get; set; }

    [JsonPropertyName("fileCount")]
    public long? FileCount { get; set; }

    [JsonPropertyName("totalBytes")]
    public long? TotalBytes { get; set; }

    [JsonPropertyName("files")]
    public List<ManifestFileDocument>? Files { get; set; }
}

public sealed class ManifestFileDocument
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
