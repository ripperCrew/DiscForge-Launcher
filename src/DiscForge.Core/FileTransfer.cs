using System.Buffers;
using System.Security.Cryptography;

namespace DiscForge.Core;

public enum ExistingFilePolicy
{
    Replace,
    Keep,
}

public enum TransferPhase
{
    Copying,
    Verifying,
}

public readonly record struct TransferProgress(
    TransferPhase Phase,
    string? CurrentRelativePath,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes);

public enum TransferOutcome
{
    Verified,
    SizeChecked,
    Warnings,
    Failed,
    Cancelled,
}

public enum TransferIssueKind
{
    ReadFailed,
    WriteFailed,
    PathRejected,
    MissingFile,
    SizeMismatch,
    HashMismatch,
    NotInManifest,
    MissingOnDisc,
    ExtraFile,
}

public sealed record TransferIssue(TransferIssueKind Kind, string? RelativePath, string Detail);

public sealed class TransferRequest
{
    public required string GameFilesDirectory { get; init; }

    public required string DestinationDirectory { get; init; }

    public string? DiscRoot { get; init; }

    public required FileInventory Inventory { get; init; }

    public required ExistingFilePolicy Policy { get; init; }

    public required VerificationPlan Verification { get; init; }

    public IReadOnlyList<DiscNotice> DiscNotices { get; init; } = [];

    public Func<string, long?>? FreeSpace { get; init; }
}

public sealed class TransferResult
{
    public required TransferOutcome Outcome { get; init; }

    public required string DestinationDirectory { get; init; }

    public required IReadOnlyList<TransferIssue> Issues { get; init; }

    public required IReadOnlyList<DiscNotice> DiscNotices { get; init; }

    public required bool HashesChecked { get; init; }
}

public static class FileTransfer
{
    private const int BufferSize = 1024 * 1024;

    public static bool HasContent(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        return Directory.EnumerateFileSystemEntries(directory).Any();
    }

    public static async Task<TransferResult> RunAsync(
        TransferRequest request,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(request.DestinationDirectory);
        var issues = new List<TransferIssue>();

        if (!string.IsNullOrWhiteSpace(request.DiscRoot)
            && (PathsEqual(request.DiscRoot, destinationRoot) || PathGuard.IsStrictChild(request.DiscRoot, destinationRoot)))
        {
            return Finish(
                request,
                TransferOutcome.Failed,
                destinationRoot,
                [new TransferIssue(TransferIssueKind.WriteFailed, null, "Choisissez un dossier sur votre ordinateur, en dehors du disque source.")],
                hashesChecked: false);
        }

        var required = DiskSpace.BytesRequired(request.Inventory, destinationRoot, request.Policy);
        var space = DiskSpace.Evaluate(destinationRoot, required, request.FreeSpace);
        if (space.Status == DiskSpaceStatus.Unknown)
        {
            return Finish(
                request,
                TransferOutcome.Failed,
                destinationRoot,
                [new TransferIssue(TransferIssueKind.WriteFailed, null, "Impossible de connaître l'espace libre à l'emplacement choisi.")],
                hashesChecked: false);
        }

        if (space.Status == DiskSpaceStatus.Insufficient)
        {
            return Finish(
                request,
                TransferOutcome.Failed,
                destinationRoot,
                [new TransferIssue(
                    TransferIssueKind.WriteFailed,
                    null,
                    $"Espace disque insuffisant. Il faut {ByteText.Format(space.RequiredBytes)}, {ByteText.Format(space.FreeBytes ?? 0)} sont disponibles.")],
                hashesChecked: false);
        }

        try
        {
            Directory.CreateDirectory(destinationRoot);
            foreach (var directory in request.Inventory.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PathGuard.TryResolveChild(destinationRoot, directory, out var fullDirectory, out var error))
                {
                    issues.Add(new TransferIssue(TransferIssueKind.PathRejected, directory, error ?? "Chemin refusé."));
                    continue;
                }

                Directory.CreateDirectory(fullDirectory);
            }
        }
        catch (OperationCanceledException)
        {
            return Finish(request, TransferOutcome.Cancelled, destinationRoot, issues, hashesChecked: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new TransferIssue(TransferIssueKind.WriteFailed, null, InventoryScanner.Describe(exception)));
            return Finish(request, TransferOutcome.Failed, destinationRoot, issues, hashesChecked: false);
        }

        var planned = new List<(InventoryFile File, string Source, string Destination)>();
        foreach (var file in request.Inventory.Files)
        {
            if (FolderNames.ContainsReservedSegment(file.RelativePath))
            {
                issues.Add(new TransferIssue(TransferIssueKind.PathRejected, file.RelativePath, "Ce nom est réservé par Windows."));
                continue;
            }

            if (!PathGuard.TryResolveChild(request.GameFilesDirectory, file.RelativePath, out var source, out var sourceError))
            {
                issues.Add(new TransferIssue(TransferIssueKind.PathRejected, file.RelativePath, sourceError ?? "Chemin refusé."));
                continue;
            }

            if (!PathGuard.TryResolveChild(destinationRoot, file.RelativePath, out var destination, out var destinationError))
            {
                issues.Add(new TransferIssue(TransferIssueKind.PathRejected, file.RelativePath, destinationError ?? "Chemin refusé."));
                continue;
            }

            if (request.Policy == ExistingFilePolicy.Keep && File.Exists(destination))
                continue;

            planned.Add((file, source, destination));
        }

        long bytesToCopy = planned.Sum(item => item.File.Length);
        long copiedBytes = 0;
        var completedFiles = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            foreach (var item in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, TransferPhase.Copying, item.File.RelativePath, completedFiles, planned.Count, copiedBytes, bytesToCopy);

                try
                {
                    await CopyOneAsync(item.Source, item.Destination, buffer, read =>
                    {
                        copiedBytes += read;
                        Report(progress, TransferPhase.Copying, item.File.RelativePath, completedFiles, planned.Count, copiedBytes, bytesToCopy);
                    }, cancellationToken).ConfigureAwait(false);
                    completedFiles++;
                }
                catch (OperationCanceledException)
                {
                    return Finish(request, TransferOutcome.Cancelled, destinationRoot, issues, hashesChecked: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var kind = IsSourceFailure(item.Source) ? TransferIssueKind.ReadFailed : TransferIssueKind.WriteFailed;
                    issues.Add(new TransferIssue(kind, item.File.RelativePath, DescribeCopyFailure(exception, item.Source)));
                    return Finish(request, TransferOutcome.Failed, destinationRoot, issues, hashesChecked: false);
                }
            }

            if (issues.Exists(issue => issue.Kind == TransferIssueKind.PathRejected))
                return Finish(request, Decide(request, issues, hashesChecked: false), destinationRoot, issues, hashesChecked: false);

            var hashesChecked = await VerifyAsync(
                request,
                destinationRoot,
                buffer,
                progress,
                issues,
                cancellationToken).ConfigureAwait(false);

            CollectExtraFiles(request, destinationRoot, issues);
            return Finish(request, Decide(request, issues, hashesChecked), destinationRoot, issues, hashesChecked);
        }
        catch (OperationCanceledException)
        {
            return Finish(request, TransferOutcome.Cancelled, destinationRoot, issues, hashesChecked: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyOneAsync(
        string source,
        string destination,
        byte[] buffer,
        Action<int> onWritten,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (File.Exists(destination))
        {
            var attributes = File.GetAttributes(destination);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
        }

        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                onWritten(read);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            TryDelete(destination);
            throw;
        }

        await output.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<bool> VerifyAsync(
        TransferRequest request,
        string destinationRoot,
        byte[] buffer,
        IProgress<TransferProgress>? progress,
        List<TransferIssue> issues,
        CancellationToken cancellationToken)
    {
        var manifest = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in request.Verification.Entries)
            manifest.TryAdd(PathGuard.NormalizeRelative(entry.RelativePath), entry);

        if (request.Verification.Mode == VerificationMode.Sha256)
        {
            var present = new HashSet<string>(
                request.Inventory.Files.Select(file => PathGuard.NormalizeRelative(file.RelativePath)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in request.Verification.Entries)
            {
                if (!PathGuard.TryResolveChild(request.GameFilesDirectory, entry.RelativePath, out _, out _))
                {
                    issues.Add(new TransferIssue(TransferIssueKind.PathRejected, entry.RelativePath, "Chemin de manifeste refusé."));
                    continue;
                }

                if (!present.Contains(PathGuard.NormalizeRelative(entry.RelativePath)))
                {
                    issues.Add(new TransferIssue(
                        TransferIssueKind.MissingOnDisc,
                        entry.RelativePath,
                        "Présent dans le manifeste, absent du disque."));
                }
            }
        }

        var files = request.Inventory.Files;
        long verifiedBytes = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, TransferPhase.Verifying, file.RelativePath, index, files.Count, verifiedBytes, request.Inventory.TotalBytes);

            if (!PathGuard.TryResolveChild(destinationRoot, file.RelativePath, out var destination, out var error))
            {
                issues.Add(new TransferIssue(TransferIssueKind.PathRejected, file.RelativePath, error ?? "Chemin refusé."));
                continue;
            }

            if (!File.Exists(destination))
            {
                issues.Add(new TransferIssue(TransferIssueKind.MissingFile, file.RelativePath, "Le fichier copié est introuvable."));
                continue;
            }

            long destinationLength;
            try
            {
                destinationLength = new FileInfo(destination).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new TransferIssue(TransferIssueKind.ReadFailed, file.RelativePath, InventoryScanner.Describe(exception)));
                continue;
            }

            manifest.TryGetValue(PathGuard.NormalizeRelative(file.RelativePath), out var entry);
            if (destinationLength != file.Length)
            {
                issues.Add(new TransferIssue(
                    TransferIssueKind.SizeMismatch,
                    file.RelativePath,
                    $"Taille attendue : {file.Length} octets. Taille obtenue : {destinationLength} octets."));
            }
            else if (entry != null && destinationLength != entry.Size)
            {
                issues.Add(new TransferIssue(
                    TransferIssueKind.SizeMismatch,
                    file.RelativePath,
                    $"Le manifeste annonce {entry.Size} octets, le fichier copié en fait {destinationLength}."));
            }

            if (request.Verification.Mode == VerificationMode.Sha256)
            {
                if (entry?.Sha256 == null)
                {
                    issues.Add(new TransferIssue(
                        TransferIssueKind.NotInManifest,
                        file.RelativePath,
                        "Aucune empreinte SHA-256 exploitable pour ce fichier."));
                }
                else
                {
                    var actual = await HashAsync(destination, buffer, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new TransferIssue(
                            TransferIssueKind.HashMismatch,
                            file.RelativePath,
                            "L'empreinte SHA-256 ne correspond pas au manifeste."));
                    }
                }
            }

            verifiedBytes += destinationLength;
        }

        return request.Verification.Mode == VerificationMode.Sha256;
    }

    private static async Task<string> HashAsync(string path, byte[] buffer, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CollectExtraFiles(TransferRequest request, string destinationRoot, List<TransferIssue> issues)
    {
        var expected = new HashSet<string>(
            request.Inventory.Files.Select(file => PathGuard.NormalizeRelative(file.RelativePath)),
            StringComparer.OrdinalIgnoreCase);

        List<string> found;
        try
        {
            found = WalkFiles(destinationRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new TransferIssue(TransferIssueKind.ReadFailed, null, InventoryScanner.Describe(exception)));
            return;
        }

        foreach (var relative in found)
        {
            if (!expected.Contains(relative))
            {
                issues.Add(new TransferIssue(
                    TransferIssueKind.ExtraFile,
                    relative,
                    "Fichier déjà présent et conservé. Il ne provient pas du disque."));
            }
        }
    }

    private static List<string> WalkFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var full = Path.GetFullPath(entry);
                if (!PathGuard.IsStrictChild(root, full))
                    continue;

                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(full);
                    continue;
                }

                files.Add(PathGuard.NormalizeRelative(Path.GetRelativePath(root, full)));
            }
        }

        return files;
    }

    private static TransferOutcome Decide(TransferRequest request, List<TransferIssue> issues, bool hashesChecked)
    {
        var hard = issues.Any(issue => issue.Kind is TransferIssueKind.ReadFailed
            or TransferIssueKind.WriteFailed
            or TransferIssueKind.PathRejected
            or TransferIssueKind.MissingFile
            or TransferIssueKind.SizeMismatch
            or TransferIssueKind.HashMismatch);

        if (hard)
            return TransferOutcome.Failed;

        if (issues.Count > 0 || request.DiscNotices.Count > 0)
            return TransferOutcome.Warnings;

        return hashesChecked ? TransferOutcome.Verified : TransferOutcome.SizeChecked;
    }

    private static TransferResult Finish(
        TransferRequest request,
        TransferOutcome outcome,
        string destinationRoot,
        IReadOnlyList<TransferIssue> issues,
        bool hashesChecked)
        => new()
        {
            Outcome = outcome,
            DestinationDirectory = destinationRoot,
            Issues = issues,
            DiscNotices = request.DiscNotices,
            HashesChecked = hashesChecked,
        };

    private static void Report(
        IProgress<TransferProgress>? progress,
        TransferPhase phase,
        string? relativePath,
        int completedFiles,
        int totalFiles,
        long completedBytes,
        long totalBytes)
    {
        progress?.Report(new TransferProgress(phase, relativePath, completedFiles, totalFiles, completedBytes, totalBytes));
    }

    private static bool IsSourceFailure(string source)
        => !InventoryScanner.DriveReady(source) || !File.Exists(source);

    private static string DescribeCopyFailure(Exception exception, string source)
    {
        if (!InventoryScanner.DriveReady(source) || !File.Exists(source))
            return "Lecture impossible. Le disque a peut-être été retiré ou le fichier est inaccessible.";

        if (exception is PathTooLongException)
            return "Le chemin est trop long.";

        if (exception.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027))
            return "Espace disque insuffisant.";

        return InventoryScanner.Describe(exception);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(30);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}

public static class TransferWording
{
    public static string Summarize(TransferResult result)
    {
        return result.Outcome switch
        {
            TransferOutcome.Verified => "Copie terminée et vérifiée.",
            TransferOutcome.SizeChecked => "Copie terminée. Présence et tailles contrôlées.",
            TransferOutcome.Warnings => "Copie terminée avec avertissements.",
            TransferOutcome.Failed => "Échec de la copie ou de la vérification.",
            TransferOutcome.Cancelled => "Opération annulée.",
            _ => "Opération terminée.",
        };
    }
}
