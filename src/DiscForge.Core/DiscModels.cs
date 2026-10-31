namespace DiscForge.Core;

public enum DiscNoticeSeverity
{
    Warning,
}

public sealed record DiscNotice(DiscNoticeSeverity Severity, string Message)
{
    public static DiscNotice Warning(string message) => new(DiscNoticeSeverity.Warning, message);
}

public sealed record GamePresentation(
    string Title,
    string FolderName,
    string? Subtitle,
    string? Edition,
    string? Filesystem,
    string Instructions,
    string? ArtworkPath);

public sealed record InventoryFile(string RelativePath, long Length);

public sealed class FileInventory
{
    public FileInventory(IReadOnlyList<InventoryFile> files, IReadOnlyList<string> directories)
    {
        Files = files;
        Directories = directories;
        TotalBytes = files.Aggregate(0L, (sum, file) => sum + file.Length);
    }

    public IReadOnlyList<InventoryFile> Files { get; }

    public IReadOnlyList<string> Directories { get; }

    public long TotalBytes { get; }
}

public sealed class InventoryScan
{
    private InventoryScan(FileInventory? inventory, IReadOnlyList<DiscNotice> notices, string? error)
    {
        Inventory = inventory;
        Notices = notices;
        Error = error;
    }

    public FileInventory? Inventory { get; }

    public IReadOnlyList<DiscNotice> Notices { get; }

    public string? Error { get; }

    public static InventoryScan Ok(FileInventory inventory, IReadOnlyList<DiscNotice> notices) => new(inventory, notices, null);

    public static InventoryScan Failed(string error) => new(null, [], error);
}

public enum VerificationMode
{
    Sha256,
    SizeOnly,
}

public sealed record ManifestEntry(string RelativePath, long Size, string? Sha256);

public sealed class VerificationPlan
{
    private VerificationPlan(VerificationMode mode, IReadOnlyList<ManifestEntry> entries)
    {
        Mode = mode;
        Entries = entries;
    }

    public VerificationMode Mode { get; }

    public IReadOnlyList<ManifestEntry> Entries { get; }

    public static VerificationPlan SizeOnly() => new(VerificationMode.SizeOnly, []);

    public static VerificationPlan Hashes(IReadOnlyList<ManifestEntry> entries) => new(VerificationMode.Sha256, entries);
}

public sealed class LoadedDisc
{
    public LoadedDisc(
        string discRoot,
        GamePresentation? presentation,
        string? gameFilesDirectory,
        FileInventory? inventory,
        VerificationPlan verification,
        IReadOnlyList<DiscNotice> notices,
        string? blockingError)
    {
        DiscRoot = discRoot;
        Presentation = presentation;
        GameFilesDirectory = gameFilesDirectory;
        Inventory = inventory;
        Verification = verification;
        Notices = notices;
        BlockingError = blockingError;
    }

    public string DiscRoot { get; }

    public GamePresentation? Presentation { get; }

    public string? GameFilesDirectory { get; }

    public FileInventory? Inventory { get; }

    public VerificationPlan Verification { get; }

    public IReadOnlyList<DiscNotice> Notices { get; }

    public string? BlockingError { get; }

    public bool CanCopy => BlockingError == null && GameFilesDirectory != null && Inventory != null;
}
