namespace DiscForge.Core;

public static class PathGuard
{
    public static bool TryResolveChild(string root, string relativePath, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            error = "Le dossier racine est vide.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath) || IsUnsafeRelative(relativePath))
        {
            error = "Le chemin sort du dossier autorisé.";
            return false;
        }

        string rootFull;
        string combined;
        try
        {
            rootFull = Path.GetFullPath(root);
            combined = Path.GetFullPath(Path.Combine(rootFull, relativePath.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Le chemin est trop long ou invalide.";
            return false;
        }

        if (!IsStrictChild(rootFull, combined))
        {
            error = "Le chemin sort du dossier autorisé.";
            return false;
        }

        fullPath = combined;
        return true;
    }

    public static bool IsStrictChild(string root, string candidate)
    {
        var rootFull = WithTrailingSeparator(Path.GetFullPath(root));
        var candidateFull = Path.GetFullPath(candidate);
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Overlaps(string left, string right)
    {
        var leftFull = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rightFull = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return leftFull.Equals(rightFull, StringComparison.OrdinalIgnoreCase)
            || IsStrictChild(leftFull, rightFull)
            || IsStrictChild(rightFull, leftFull);
    }

    public static string NormalizeRelative(string path)
    {
        var value = path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        while (value.StartsWith(Path.DirectorySeparatorChar))
            value = value[1..];
        while (value.EndsWith(Path.DirectorySeparatorChar))
            value = value[..^1];
        return value;
    }

    public static bool IsUnsafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0'))
            return true;

        if (Path.IsPathRooted(relative))
            return true;

        var invalidNameChars = Path.GetInvalidFileNameChars();
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
                continue;

            if (segment == ".." || segment.IndexOfAny(invalidNameChars) >= 0)
                return true;
        }

        return false;
    }

    private static string WithTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return path;

        return path + Path.DirectorySeparatorChar;
    }
}
