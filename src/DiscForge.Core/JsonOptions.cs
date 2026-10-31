using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
