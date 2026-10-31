using System.Text;

namespace DiscForge.Core;

public static class FolderNames
{
    public const int MaxLength = 80;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? title)
    {
        var source = string.IsNullOrWhiteSpace(title) ? GameText.FallbackTitle : title.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;

        foreach (var character in source)
        {
            var replaced = character < 32 || Array.IndexOf(invalid, character) >= 0;
            if (replaced || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > MaxLength)
            name = name[..MaxLength].Trim().TrimEnd('.', ' ');

        if (name.Length == 0 || IsReservedName(name))
            return GameText.FallbackTitle;

        return name;
    }

    public static bool ContainsReservedSegment(string relativePath)
    {
        var normalized = PathGuard.NormalizeRelative(relativePath);
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(IsReservedName);
    }

    public static bool IsReservedName(string name)
    {
        var baseName = name;
        var dot = name.IndexOf('.');
        if (dot >= 0)
            baseName = name[..dot];

        return baseName.Length > 0 && Reserved.Contains(baseName);
    }
}
