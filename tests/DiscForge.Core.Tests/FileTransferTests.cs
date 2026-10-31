using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Tests;

public class FileTransferTests
{
    [Fact]
    public async Task Copies_the_tree_and_verifies_sha256()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        var game = System.IO.Path.Combine(source.Path, "GameFiles");
        Directory.CreateDirectory(System.IO.Path.Combine(game, "nested"));
        Directory.CreateDirectory(System.IO.Path.Combine(game, "empty"));
        var payload = new string('x', 2 * 1024 * 1024);
        await File.WriteAllTextAsync(System.IO.Path.Combine(game, "nested", "data.bin"), payload);
        await File.WriteAllTextAsync(System.IO.Path.Combine(game, "note.txt"), "discforge");
        File.SetAttributes(System.IO.Path.Combine(game, "note.txt"), FileAttributes.ReadOnly);

        var loaded = Load(source.Path, "Démo", withManifest: true);
        var destination = System.IO.Path.Combine(destinationParent.Path, loaded.Presentation!.FolderName);

        var result = await FileTransfer.RunAsync(Request(loaded, destination, ExistingFilePolicy.Replace), null, CancellationToken.None);

        Assert.Equal(TransferOutcome.Verified, result.Outcome);
        Assert.True(result.HashesChecked);
        Assert.Equal("Copie terminée et vérifiée.", TransferWording.Summarize(result));
        Assert.Equal(payload, await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "nested", "data.bin")));
        Assert.Equal("discforge", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "note.txt")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(destination, "empty")));
        Assert.True(File.GetAttributes(System.IO.Path.Combine(game, "note.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("discforge", await File.ReadAllTextAsync(System.IO.Path.Combine(game, "note.txt")));
    }

    [Fact]
    public async Task Size_check_does_not_claim_the_files_were_verified()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(source.Path, "GameFiles"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source.Path, "GameFiles", "a.txt"), "abc");
        var loaded = Load(source.Path, "Démo", withManifest: false);
        var destination = System.IO.Path.Combine(destinationParent.Path, "out");

        var result = await FileTransfer.RunAsync(Request(loaded, destination, ExistingFilePolicy.Replace), null, CancellationToken.None);

        Assert.Equal(TransferOutcome.SizeChecked, result.Outcome);
        Assert.False(result.HashesChecked);
        var summary = TransferWording.Summarize(result);
        Assert.Equal("Copie terminée. Présence et tailles contrôlées.", summary);
        Assert.DoesNotContain("vérifiée", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keep_preserves_existing_files_and_replace_does_not_delete_strangers()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        var game = System.IO.Path.Combine(source.Path, "GameFiles");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(System.IO.Path.Combine(game, "keep.txt"), "from-disc");
        await File.WriteAllTextAsync(System.IO.Path.Combine(game, "new.txt"), "new");
        var loaded = Load(source.Path, "Démo", withManifest: true);

        var destination = System.IO.Path.Combine(destinationParent.Path, "out");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(System.IO.Path.Combine(destination, "keep.txt"), "local");
        await File.WriteAllTextAsync(System.IO.Path.Combine(destination, "extra.txt"), "extra");

        var kept = await FileTransfer.RunAsync(Request(loaded, destination, ExistingFilePolicy.Keep), null, CancellationToken.None);

        Assert.Equal("local", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "keep.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "new.txt")));
        Assert.Equal("extra", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "extra.txt")));
        Assert.Contains(kept.Issues, issue => issue.Kind == TransferIssueKind.HashMismatch);
        Assert.Equal(TransferOutcome.Failed, kept.Outcome);

        var replaced = await FileTransfer.RunAsync(Request(loaded, destination, ExistingFilePolicy.Replace), null, CancellationToken.None);

        Assert.Equal("from-disc", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "keep.txt")));
        Assert.Equal("extra", await File.ReadAllTextAsync(System.IO.Path.Combine(destination, "extra.txt")));
        Assert.Contains(replaced.Issues, issue => issue.Kind == TransferIssueKind.ExtraFile && issue.RelativePath == "extra.txt");
        Assert.Equal(TransferOutcome.Warnings, replaced.Outcome);
        Assert.True(replaced.HashesChecked);
    }

    [Fact]
    public async Task Wrong_hash_fails_verification()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(source.Path, "GameFiles"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source.Path, "GameFiles", "a.txt"), "abc");
        source.Write("metadata.json", """{ "gameTitle": "Démo" }""");
        source.Write("manifest.json", """
            {
              "algorithm": "SHA-256",
              "root": "GameFiles",
              "files": [ { "path": "a.txt", "size": 3, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ]
            }
            """);
        var loaded = DiscLoader.Load(source.Path);
        var destination = System.IO.Path.Combine(destinationParent.Path, "out");

        var result = await FileTransfer.RunAsync(Request(loaded, destination, ExistingFilePolicy.Replace), null, CancellationToken.None);

        Assert.Equal(TransferOutcome.Failed, result.Outcome);
        Assert.Contains(result.Issues, issue => issue.Kind == TransferIssueKind.HashMismatch && issue.RelativePath == "a.txt");
        Assert.Equal("Échec de la copie ou de la vérification.", TransferWording.Summarize(result));
    }

    [Fact]
    public async Task Cancel_removes_the_incomplete_file_and_keeps_finished_ones()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        var game = System.IO.Path.Combine(source.Path, "GameFiles");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(System.IO.Path.Combine(game, "a.txt"), "done");
        await File.WriteAllBytesAsync(System.IO.Path.Combine(game, "b.bin"), new byte[256 * 1024]);
        var loaded = Load(source.Path, "Démo", withManifest: false);
        var destination = System.IO.Path.Combine(destinationParent.Path, "out");
        var cts = new CancellationTokenSource();
        var progress = new InlineProgress(update =>
        {
            if (update.Phase == TransferPhase.Copying
                && string.Equals(update.CurrentRelativePath, "b.bin", StringComparison.OrdinalIgnoreCase)
                && update.CompletedBytes > 4)
            {
                cts.Cancel();
            }
        });

        var result = await FileTransfer.RunAsync(Request(loaded, destination, ExistingFilePolicy.Replace), progress, cts.Token);

        Assert.Equal(TransferOutcome.Cancelled, result.Outcome);
        Assert.Equal("Opération annulée.", TransferWording.Summarize(result));
        Assert.False(result.HashesChecked);
        Assert.True(File.Exists(System.IO.Path.Combine(destination, "a.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(destination, "b.bin")));
    }

    [Fact]
    public async Task Cancel_during_verification_keeps_the_copy()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(source.Path, "GameFiles"));
        await File.WriteAllBytesAsync(System.IO.Path.Combine(source.Path, "GameFiles", "big.bin"), new byte[256 * 1024]);
        var loaded = Load(source.Path, "Démo", withManifest: true);
        var destination = System.IO.Path.Combine(destinationParent.Path, "out");
        var cts = new CancellationTokenSource();
        var progress = new InlineProgress(update =>
        {
            if (update.Phase == TransferPhase.Verifying)
                cts.Cancel();
        });

        var result = await FileTransfer.RunAsync(Request(loaded, destination, ExistingFilePolicy.Replace), progress, cts.Token);

        Assert.Equal(TransferOutcome.Cancelled, result.Outcome);
        Assert.False(result.HashesChecked);
        Assert.True(File.Exists(System.IO.Path.Combine(destination, "big.bin")));
    }

    [Fact]
    public async Task Reports_a_missing_source_file()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        var game = System.IO.Path.Combine(source.Path, "GameFiles");
        Directory.CreateDirectory(game);
        var file = System.IO.Path.Combine(game, "gone.txt");
        await File.WriteAllTextAsync(file, "temp");
        var inventory = new FileInventory([new InventoryFile("gone.txt", 4)], []);
        File.Delete(file);

        var request = new TransferRequest
        {
            GameFilesDirectory = game,
            DestinationDirectory = System.IO.Path.Combine(destinationParent.Path, "out"),
            Inventory = inventory,
            Policy = ExistingFilePolicy.Replace,
            Verification = VerificationPlan.SizeOnly(),
            FreeSpace = _ => long.MaxValue,
        };

        var result = await FileTransfer.RunAsync(request, null, CancellationToken.None);

        Assert.Equal(TransferOutcome.Failed, result.Outcome);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(TransferIssueKind.ReadFailed, issue.Kind);
        Assert.Equal("gone.txt", issue.RelativePath);
    }

    [Fact]
    public async Task Does_not_write_outside_the_destination()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        var game = System.IO.Path.Combine(source.Path, "GameFiles");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(System.IO.Path.Combine(game, "ok.txt"), "ok");
        var outside = destinationParent.Write("secret.txt", "stay");
        var inventory = new FileInventory(
            [
                new InventoryFile("ok.txt", 2),
                new InventoryFile(@"..\secret.txt", 4),
            ],
            [@"..\escaped"]);

        var request = new TransferRequest
        {
            GameFilesDirectory = game,
            DestinationDirectory = System.IO.Path.Combine(destinationParent.Path, "out"),
            Inventory = inventory,
            Policy = ExistingFilePolicy.Replace,
            Verification = VerificationPlan.SizeOnly(),
            FreeSpace = _ => long.MaxValue,
        };

        var result = await FileTransfer.RunAsync(request, null, CancellationToken.None);

        Assert.Equal(TransferOutcome.Failed, result.Outcome);
        Assert.Equal("stay", await File.ReadAllTextAsync(outside));
        Assert.False(Directory.Exists(System.IO.Path.Combine(destinationParent.Path, "escaped")));
        Assert.Contains(result.Issues, issue => issue.Kind == TransferIssueKind.PathRejected);
    }

    [Fact]
    public async Task Refuses_a_destination_on_the_source_disc()
    {
        using var source = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(source.Path, "GameFiles"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source.Path, "GameFiles", "a.txt"), "a");
        var loaded = Load(source.Path, "Démo", withManifest: false);

        var result = await FileTransfer.RunAsync(
            Request(loaded, System.IO.Path.Combine(source.Path, "inside"), ExistingFilePolicy.Replace),
            null,
            CancellationToken.None);

        Assert.Equal(TransferOutcome.Failed, result.Outcome);
        Assert.Contains(result.Issues, issue => issue.Detail.Contains("disque source", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(System.IO.Path.Combine(source.Path, "inside")));
    }

    [Fact]
    public async Task Stops_before_copying_when_space_is_insufficient()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(source.Path, "GameFiles"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source.Path, "GameFiles", "a.txt"), "abcdef");
        var loaded = Load(source.Path, "Démo", withManifest: false);
        var destination = System.IO.Path.Combine(destinationParent.Path, "out");
        var request = Request(loaded, destination, ExistingFilePolicy.Replace);
        request = new TransferRequest
        {
            GameFilesDirectory = request.GameFilesDirectory,
            DestinationDirectory = request.DestinationDirectory,
            DiscRoot = request.DiscRoot,
            Inventory = request.Inventory,
            Policy = request.Policy,
            Verification = request.Verification,
            DiscNotices = request.DiscNotices,
            FreeSpace = _ => 1,
        };

        var result = await FileTransfer.RunAsync(request, null, CancellationToken.None);

        Assert.Equal(TransferOutcome.Failed, result.Outcome);
        Assert.Contains(result.Issues, issue => issue.Detail.Contains("Espace disque insuffisant", StringComparison.Ordinal));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Count_mismatch_is_a_warning_even_when_hashes_match()
    {
        using var source = new TempFolder();
        using var destinationParent = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(source.Path, "GameFiles"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(source.Path, "GameFiles", "a.txt"), "abc");
        source.Write("metadata.json", """
            {
              "gameTitle": "Démo",
              "gameFiles": { "directory": "GameFiles", "fileCount": 4, "totalBytes": 3 }
            }
            """);
        var hash = Convert.ToHexString(SHA256.HashData("abc"u8.ToArray())).ToLowerInvariant();
        source.Write("manifest.json", $$"""
            {
              "algorithm": "SHA-256",
              "root": "GameFiles",
              "fileCount": 1,
              "totalBytes": 3,
              "files": [ { "path": "a.txt", "size": 3, "sha256": "{{hash}}" } ]
            }
            """);
        var loaded = DiscLoader.Load(source.Path);
        Assert.NotEmpty(loaded.Notices);

        var result = await FileTransfer.RunAsync(
            Request(loaded, System.IO.Path.Combine(destinationParent.Path, "out"), ExistingFilePolicy.Replace),
            null,
            CancellationToken.None);

        Assert.Equal(TransferOutcome.Warnings, result.Outcome);
        Assert.True(result.HashesChecked);
        Assert.Equal("Copie terminée avec avertissements.", TransferWording.Summarize(result));
    }

    private static LoadedDisc Load(string root, string title, bool withManifest)
    {
        File.WriteAllText(System.IO.Path.Combine(root, "metadata.json"), $$"""
            { "gameTitle": "{{title}}" }
            """);

        if (withManifest)
        {
            var game = System.IO.Path.Combine(root, "GameFiles");
            var files = Directory.Exists(game)
                ? Directory.GetFiles(game, "*", SearchOption.AllDirectories)
                : [];
            var entries = files.Select(file =>
            {
                var relative = Path.GetRelativePath(game, file).Replace('\\', '/');
                var bytes = File.ReadAllBytes(file);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return $$"""{ "path": "{{relative}}", "size": {{bytes.Length}}, "sha256": "{{hash}}" }""";
            });
            var total = files.Sum(file => new FileInfo(file).Length);
            File.WriteAllText(System.IO.Path.Combine(root, "manifest.json"), $$"""
                {
                  "formatVersion": 1,
                  "algorithm": "SHA-256",
                  "root": "GameFiles",
                  "fileCount": {{files.Length}},
                  "totalBytes": {{total}},
                  "files": [ {{string.Join(", ", entries)}} ]
                }
                """);
        }

        var loaded = DiscLoader.Load(root);
        Assert.True(loaded.CanCopy, loaded.BlockingError);
        return loaded;
    }

    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private static TransferRequest Request(LoadedDisc loaded, string destination, ExistingFilePolicy policy)
        => new()
        {
            GameFilesDirectory = loaded.GameFilesDirectory!,
            DestinationDirectory = destination,
            DiscRoot = loaded.DiscRoot,
            Inventory = loaded.Inventory!,
            Policy = policy,
            Verification = loaded.Verification,
            DiscNotices = loaded.Notices,
            FreeSpace = _ => long.MaxValue,
        };
}
