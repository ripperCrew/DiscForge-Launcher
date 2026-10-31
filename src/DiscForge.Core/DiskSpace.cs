namespace DiscForge.Core;

public enum DiskSpaceStatus
{
    Enough,
    Insufficient,
    Unknown,
}

public sealed record DiskSpaceVerdict(DiskSpaceStatus Status, long? FreeBytes, long RequiredBytes)
{
    public static DiskSpaceVerdict Enough(long freeBytes, long requiredBytes) => new(DiskSpaceStatus.Enough, freeBytes, requiredBytes);

    public static DiskSpaceVerdict Insufficient(long freeBytes, long requiredBytes) => new(DiskSpaceStatus.Insufficient, freeBytes, requiredBytes);

    public static DiskSpaceVerdict Unknown(long requiredBytes) => new(DiskSpaceStatus.Unknown, null, requiredBytes);
}

public static class DiskSpace
{
    public const long MarginBytes = 1024 * 1024;

    public static long BytesRequired(FileInventory inventory, string destinationDirectory, ExistingFilePolicy policy)
    {
        long required = 0;
        foreach (var file in inventory.Files)
        {
            if (!PathGuard.TryResolveChild(destinationDirectory, file.RelativePath, out var destination, out _))
                continue;

            if (!File.Exists(destination))
            {
                required += file.Length;
                continue;
            }

            if (policy == ExistingFilePolicy.Keep)
                continue;

            long existing;
            try
            {
                existing = new FileInfo(destination).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                existing = 0;
            }

            required += Math.Max(0, file.Length - existing);
        }

        return required == 0 ? 0 : required + MarginBytes;
    }

    public static DiskSpaceVerdict Evaluate(string destinationDirectory, long bytesRequired, Func<string, long?>? freeSpace = null)
    {
        freeSpace ??= ProbeFreeSpace;
        long? free;
        try
        {
            free = freeSpace(destinationDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            free = null;
        }

        if (free == null)
            return DiskSpaceVerdict.Unknown(bytesRequired);

        if (free.Value < bytesRequired)
            return DiskSpaceVerdict.Insufficient(free.Value, bytesRequired);

        return DiskSpaceVerdict.Enough(free.Value, bytesRequired);
    }

    public static long? ProbeFreeSpace(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                    break;

                current = parent;
            }

            var root = Path.GetPathRoot(current);
            if (string.IsNullOrEmpty(root))
                return null;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return null;

            return drive.AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
