namespace DiscForge.Core;

public static class DiscRootLocator
{
    public static string Locate(string? processPath, IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--root", StringComparison.OrdinalIgnoreCase))
                continue;

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException("L'argument --root exige un dossier.");

            return Path.GetFullPath(args[index + 1]);
        }

        // Single-file publish extracts the app to a temporary folder. AppContext.BaseDirectory
        // points there; the DVD root is the directory of the real executable.
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Impossible de déterminer l'emplacement du programme.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(processPath));
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Impossible de déterminer l'emplacement du programme.");

        return directory;
    }
}
