namespace DiscForge.Core;

public static class InventoryScanner
{
    public static InventoryScan Scan(string gameFilesDirectory)
    {
        string root;
        try
        {
            root = Path.GetFullPath(gameFilesDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InventoryScan.Failed("Le dossier des fichiers du jeu est invalide.");
        }

        if (!Directory.Exists(root))
            return InventoryScan.Failed("Le dossier des fichiers du jeu est introuvable ou inaccessible.");

        var notices = new List<DiscNotice>();
        var files = new List<InventoryFile>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return InventoryScan.Failed(ReadError(current, exception));
            }

            foreach (var entry in entries)
            {
                var included = Include(root, entry, files, directories, pending, notices);
                if (included.Error != null)
                    return InventoryScan.Failed(included.Error);
            }
        }

        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        directories.Sort(StringComparer.OrdinalIgnoreCase);
        return InventoryScan.Ok(new FileInventory(files, directories), notices);
    }

    private static Step Include(
        string root,
        string entry,
        List<InventoryFile> files,
        List<string> directories,
        Stack<string> pending,
        List<DiscNotice> notices)
    {
        string full;
        FileAttributes attributes;
        try
        {
            full = Path.GetFullPath(entry);
            attributes = File.GetAttributes(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Step.Fail(ReadError(entry, exception));
        }

        if (!PathGuard.IsStrictChild(root, full))
        {
            notices.Add(DiscNotice.Warning($"Chemin ignoré, hors du dossier du jeu : {entry}"));
            return Step.Done();
        }

        var relative = PathGuard.NormalizeRelative(Path.GetRelativePath(root, full));
        if (relative.Length == 0 || PathGuard.IsUnsafeRelative(relative))
        {
            notices.Add(DiscNotice.Warning($"Chemin ignoré : {entry}"));
            return Step.Done();
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (!LinkStaysInside(full, root, out var linkError))
            {
                notices.Add(DiscNotice.Warning(linkError ?? $"Lien ignoré : {relative}"));
                return Step.Done();
            }

            if (isDirectory)
            {
                notices.Add(DiscNotice.Warning($"Lien de dossier ignoré pour éviter d'en suivre la cible : {relative}"));
                return Step.Done();
            }
        }

        if (isDirectory)
        {
            directories.Add(relative);
            pending.Push(full);
            return Step.Done();
        }

        long length;
        try
        {
            length = new FileInfo(full).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Step.Fail($"Impossible de lire « {relative} ». {Describe(exception)}");
        }

        files.Add(new InventoryFile(relative, length));
        return Step.Done();
    }

    private static bool LinkStaysInside(string linkPath, string root, out string? error)
    {
        error = null;
        FileSystemInfo? target;
        try
        {
            var info = (File.GetAttributes(linkPath) & FileAttributes.Directory) != 0
                ? (FileSystemInfo)new DirectoryInfo(linkPath)
                : new FileInfo(linkPath);
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"Lien illisible : {linkPath}. {Describe(exception)}";
            return false;
        }

        if (target == null)
        {
            error = $"Lien ignoré, cible introuvable : {linkPath}";
            return false;
        }

        string targetFull;
        try
        {
            targetFull = Path.GetFullPath(target.FullName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Lien ignoré, cible invalide : {linkPath}";
            return false;
        }

        if (!PathGuard.IsStrictChild(root, targetFull))
        {
            error = $"Lien ignoré, sa cible sort du dossier du jeu : {Path.GetFileName(linkPath)}";
            return false;
        }

        return true;
    }

    private static string ReadError(string path, Exception exception)
    {
        if (!DriveReady(path))
            return "Lecture impossible. Le disque a peut-être été retiré.";

        return $"Lecture impossible dans le dossier du jeu. {Describe(exception)}";
    }

    internal static bool DriveReady(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return true;

            return new DriveInfo(root).IsReady;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string Describe(Exception exception)
    {
        if (exception is PathTooLongException)
            return "Le chemin est trop long.";

        if (exception is UnauthorizedAccessException)
            return "Accès refusé.";

        var message = exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return "Erreur d'entrée/sortie.";

        return message.Length > 180 ? message[..180] : message;
    }

    private readonly struct Step
    {
        private Step(string? error) => Error = error;

        public string? Error { get; }

        public static Step Done() => new(null);

        public static Step Fail(string error) => new(error);
    }
}
