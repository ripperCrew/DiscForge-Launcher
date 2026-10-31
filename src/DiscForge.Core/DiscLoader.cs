using System.Text.Json;

namespace DiscForge.Core;

public static class DiscLoader
{
    public const string DefaultGameDirectory = "GameFiles";
    public const string DefaultManifestFile = "manifest.json";
    public const string DefaultArtworkFile = "artwork.png";
    public const int SupportedFormatVersion = 1;

    public static LoadedDisc Load(string discRoot)
    {
        if (string.IsNullOrWhiteSpace(discRoot) || !Directory.Exists(discRoot))
            return Blocked(discRoot ?? string.Empty, null, [], "Le dossier du disque est introuvable.");

        var root = Path.GetFullPath(discRoot);
        var notices = new List<DiscNotice>();
        var metadataPath = Path.Combine(root, "metadata.json");
        if (!File.Exists(metadataPath))
            return Blocked(root, null, notices, "Le fichier metadata.json est introuvable à côté de ce programme.");

        MetadataDocument document;
        try
        {
            var json = File.ReadAllText(metadataPath);
            document = JsonSerializer.Deserialize<MetadataDocument>(json, JsonOptions.Relaxed)
                ?? throw new JsonException("Le document est vide.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Blocked(root, null, notices, $"Le fichier metadata.json est illisible. {InventoryScanner.Describe(exception)}");
        }

        if (document.FormatVersion is int version && version != SupportedFormatVersion)
        {
            notices.Add(DiscNotice.Warning(
                $"Version de format {version} non reconnue pour metadata.json. Les champs connus sont utilisés."));
        }

        var title = BlankToNull(document.GameTitle) ?? GameText.FallbackTitle;
        var presentation = new GamePresentation(
            title,
            FolderNames.Sanitize(title),
            BlankToNull(document.Subtitle),
            BlankToNull(document.Edition),
            BlankToNull(document.Filesystem),
            BlankToNull(document.Instructions) ?? GameText.DefaultInstructions,
            ResolveArtwork(root, document, notices));

        var gameRelative = BlankToNull(document.GameFiles?.Directory) ?? DefaultGameDirectory;
        if (!PathGuard.TryResolveChild(root, gameRelative, out var gameFull, out var gameError))
        {
            return Blocked(
                root,
                presentation,
                notices,
                $"Le dossier des fichiers du jeu est refusé ({gameRelative}). {gameError}");
        }

        if (!Directory.Exists(gameFull))
        {
            return Blocked(
                root,
                presentation,
                notices,
                $"Le dossier des fichiers du jeu est introuvable ou inaccessible : {gameRelative}.");
        }

        var scan = InventoryScanner.Scan(gameFull);
        if (scan.Error != null || scan.Inventory == null)
            return Blocked(root, presentation, notices, scan.Error ?? "Le dossier des fichiers du jeu est inaccessible.");

        notices.AddRange(scan.Notices);
        CompareDeclaredCounts(document.GameFiles?.FileCount, document.GameFiles?.TotalBytes, scan.Inventory, "metadata.json", notices);

        var verification = LoadManifest(root, gameRelative, gameFull, document, scan.Inventory, notices);
        return new LoadedDisc(root, presentation, gameFull, scan.Inventory, verification, notices, null);
    }

    private static VerificationPlan LoadManifest(
        string discRoot,
        string gameRelative,
        string gameFull,
        MetadataDocument document,
        FileInventory inventory,
        List<DiscNotice> notices)
    {
        var explicitFile = BlankToNull(document.Manifest?.File);
        var manifestRelative = explicitFile ?? DefaultManifestFile;
        if (!PathGuard.TryResolveChild(discRoot, manifestRelative, out var manifestPath, out var pathError))
        {
            notices.Add(DiscNotice.Warning($"Le chemin du manifeste est refusé. {pathError}"));
            return VerificationPlan.SizeOnly();
        }

        if (!File.Exists(manifestPath))
        {
            if (explicitFile != null)
            {
                notices.Add(DiscNotice.Warning(
                    $"Le manifeste « {manifestRelative} » est introuvable. Seules la présence et la taille seront contrôlées."));
            }

            return VerificationPlan.SizeOnly();
        }

        ManifestDocument manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOptions.Relaxed)
                ?? throw new JsonException("Le document est vide.");
        }
        catch (JsonException exception)
        {
            notices.Add(DiscNotice.Warning(
                $"Le manifeste est malformé. Seules la présence et la taille seront contrôlées. {InventoryScanner.Describe(exception)}"));
            return VerificationPlan.SizeOnly();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            notices.Add(DiscNotice.Warning(
                $"Le manifeste est illisible. Seules la présence et la taille seront contrôlées. {InventoryScanner.Describe(exception)}"));
            return VerificationPlan.SizeOnly();
        }

        if (manifest.FormatVersion is int version && version != SupportedFormatVersion)
        {
            notices.Add(DiscNotice.Warning(
                $"Version de format {version} non reconnue pour le manifeste. Les champs connus sont utilisés."));
        }

        var algorithm = BlankToNull(manifest.Algorithm) ?? BlankToNull(document.Manifest?.Algorithm);
        if (!string.Equals(algorithm, "SHA-256", StringComparison.OrdinalIgnoreCase))
        {
            notices.Add(DiscNotice.Warning(
                "L'algorithme du manifeste n'est pas SHA-256. Seules la présence et la taille seront contrôlées."));
            return VerificationPlan.SizeOnly();
        }

        var manifestRoot = BlankToNull(manifest.Root);
        if (manifestRoot == null)
        {
            notices.Add(DiscNotice.Warning(
                "Le manifeste n'indique pas de racine. Seules la présence et la taille seront contrôlées."));
            return VerificationPlan.SizeOnly();
        }

        if (!SameDirectory(discRoot, gameRelative, manifestRoot))
        {
            notices.Add(DiscNotice.Warning(
                "La racine du manifeste ne correspond pas au dossier des fichiers du jeu. Les empreintes sont ignorées."));
            return VerificationPlan.SizeOnly();
        }

        if (manifest.Files == null)
        {
            notices.Add(DiscNotice.Warning(
                "Le manifeste ne contient pas de liste de fichiers. Seules la présence et la taille seront contrôlées."));
            return VerificationPlan.SizeOnly();
        }

        var entries = new List<ManifestEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredSum = 0;
        var usableHashes = 0;

        foreach (var file in manifest.Files)
        {
            var relative = file.Path ?? string.Empty;
            if (file.Size < 0)
            {
                notices.Add(DiscNotice.Warning($"Taille refusée dans le manifeste : {relative}"));
                continue;
            }

            declaredSum += file.Size;
            if (!PathGuard.TryResolveChild(gameFull, relative, out _, out _))
            {
                notices.Add(DiscNotice.Warning($"Entrée de manifeste refusée : {relative}"));
                continue;
            }

            var normalized = PathGuard.NormalizeRelative(relative);
            if (!seen.Add(normalized))
            {
                notices.Add(DiscNotice.Warning($"Doublon dans le manifeste : {normalized}"));
                continue;
            }

            var hash = NormalizeHash(file.Sha256);
            if (hash == null)
                notices.Add(DiscNotice.Warning($"Empreinte SHA-256 absente ou invalide : {normalized}"));
            else
                usableHashes++;

            entries.Add(new ManifestEntry(normalized, file.Size, hash));
        }

        if (manifest.FileCount is long fileCount && fileCount != manifest.Files.Count)
        {
            notices.Add(DiscNotice.Warning(
                $"Le manifeste annonce {fileCount} fichier(s), sa liste en contient {manifest.Files.Count}."));
        }

        if (manifest.TotalBytes is long totalBytes && totalBytes != declaredSum)
        {
            notices.Add(DiscNotice.Warning(
                $"Le manifeste annonce {totalBytes} octets, la somme de ses fichiers est {declaredSum}."));
        }

        CompareDeclaredCounts(manifest.FileCount, manifest.TotalBytes, inventory, "le manifeste", notices);

        if (usableHashes == 0)
        {
            notices.Add(DiscNotice.Warning(
                "Aucune empreinte SHA-256 exploitable. Seules la présence et la taille seront contrôlées."));
            return VerificationPlan.SizeOnly();
        }

        return VerificationPlan.Hashes(entries);
    }

    private static void CompareDeclaredCounts(long? fileCount, long? totalBytes, FileInventory inventory, string source, List<DiscNotice> notices)
    {
        if (fileCount is long count && count != inventory.Files.Count)
        {
            notices.Add(DiscNotice.Warning(
                $"{source} annonce {count} fichier(s), {inventory.Files.Count} ont été trouvés dans le dossier."));
        }

        if (totalBytes is long bytes && bytes != inventory.TotalBytes)
        {
            notices.Add(DiscNotice.Warning(
                $"{source} annonce {bytes} octets, {inventory.TotalBytes} ont été mesurés dans le dossier."));
        }
    }

    private static string? ResolveArtwork(string discRoot, MetadataDocument document, List<DiscNotice> notices)
    {
        var relative = BlankToNull(document.Artwork?.File) ?? DefaultArtworkFile;
        if (!PathGuard.TryResolveChild(discRoot, relative, out var fullPath, out var error))
        {
            notices.Add(DiscNotice.Warning($"L'illustration est refusée. {error}"));
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    private static bool SameDirectory(string discRoot, string leftRelative, string rightRelative)
    {
        if (!PathGuard.TryResolveChild(discRoot, leftRelative, out var left, out _))
            return false;

        if (!PathGuard.TryResolveChild(discRoot, rightRelative, out var right, out _))
            return false;

        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var hex = value.Trim().ToLowerInvariant();
        if (hex.Length != 64 || hex.Any(character => !Uri.IsHexDigit(character)))
            return null;

        return hex;
    }

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LoadedDisc Blocked(
        string discRoot,
        GamePresentation? presentation,
        IReadOnlyList<DiscNotice> notices,
        string error)
        => new(discRoot, presentation, null, null, VerificationPlan.SizeOnly(), notices, error);
}
