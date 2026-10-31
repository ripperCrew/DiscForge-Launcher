namespace DiscForge.Core.Tests;

public class PathAndSpaceTests
{
    [Theory]
    [InlineData("The Legend of Zelda: Twilight Princess", "The Legend of Zelda Twilight Princess")]
    [InlineData("CON", "Jeu")]
    [InlineData("aux.txt", "Jeu")]
    [InlineData("   ", "Jeu")]
    [InlineData("foo/bar", "foo bar")]
    public void Sanitizes_folder_names(string title, string expected)
    {
        Assert.Equal(expected, FolderNames.Sanitize(title));
    }

    [Fact]
    public void Truncates_long_folder_names()
    {
        var name = FolderNames.Sanitize(new string('a', 200));
        Assert.Equal(FolderNames.MaxLength, name.Length);
    }

    [Theory]
    [InlineData(@"..\secret.txt")]
    [InlineData(@"C:\Windows\system.ini")]
    [InlineData(@"\\server\share\file.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("good/../../secret.txt")]
    public void Rejects_paths_that_escape(string relative)
    {
        using var folder = new TempFolder();
        var ok = PathGuard.TryResolveChild(folder.Path, relative, out _, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Accepts_a_nested_relative_path()
    {
        using var folder = new TempFolder();
        var ok = PathGuard.TryResolveChild(folder.Path, @"nested\file.bin", out var full, out _);
        Assert.True(ok);
        Assert.StartsWith(folder.Path, full, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(System.IO.Path.Combine("nested", "file.bin"), full, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Does_not_treat_a_sibling_prefix_as_a_child()
    {
        using var folder = new TempFolder();
        var sibling = folder.Path + "Evil";
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.False(PathGuard.IsStrictChild(folder.Path, sibling));
        }
        finally
        {
            Directory.Delete(sibling);
        }
    }

    [Fact]
    public void Byte_text_uses_french_units()
    {
        Assert.Equal("1,36 Go", ByteText.Format(1459978240));
        Assert.Equal("1 fichier", ByteText.FileCount(1));
        Assert.Equal("2 fichiers", ByteText.FileCount(2));
    }

    [Fact]
    public void Disk_space_counts_only_bytes_that_will_be_written()
    {
        using var source = new TempFolder();
        using var destination = new TempFolder();
        var existing = destination.Write("keep.bin", "12345");
        var inventory = new FileInventory(
            [
                new InventoryFile("keep.bin", 5),
                new InventoryFile("new.bin", 10),
            ],
            []);

        var keep = DiskSpace.BytesRequired(inventory, destination.Path, ExistingFilePolicy.Keep);
        var replaceSame = DiskSpace.BytesRequired(inventory, destination.Path, ExistingFilePolicy.Replace);

        Assert.Equal(10 + DiskSpace.MarginBytes, keep);
        Assert.Equal(10 + DiskSpace.MarginBytes, replaceSame);
        Assert.True(File.Exists(existing));
    }

    [Fact]
    public void Disk_space_reports_insufficient_room_from_the_injected_probe()
    {
        using var destination = new TempFolder();
        var verdict = DiskSpace.Evaluate(destination.Path, 50, _ => 10);
        Assert.Equal(DiskSpaceStatus.Insufficient, verdict.Status);

        var unknown = DiskSpace.Evaluate(destination.Path, 50, _ => null);
        Assert.Equal(DiskSpaceStatus.Unknown, unknown.Status);

        var free = DiskSpace.ProbeFreeSpace(destination.Path);
        Assert.NotNull(free);
        Assert.True(free > 0);
    }

    [Fact]
    public void Root_locator_prefers_the_argument_then_the_executable_directory()
    {
        using var folder = new TempFolder();
        var fromArgument = DiscRootLocator.Locate(@"D:\ignored\PLAY.exe", ["--root", folder.Path]);
        Assert.Equal(System.IO.Path.GetFullPath(folder.Path), fromArgument);

        var executable = System.IO.Path.Combine(folder.Path, "PLAY.exe");
        var fromExecutable = DiscRootLocator.Locate(executable, []);
        Assert.Equal(System.IO.Path.GetFullPath(folder.Path), fromExecutable);
    }
}
