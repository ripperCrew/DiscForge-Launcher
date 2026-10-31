using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Tests;

public class DiscLoaderTests
{
    private const string ZeldaMetadata = """
        {
          "formatVersion": 1,
          "gameTitle": "The Legend of Zelda: Twilight Princess",
          "subtitle": "",
          "edition": "",
          "instructions": "",
          "filesystem": "UDF 2.01",
          "artwork": {
            "file": "artwork.png",
            "mediaType": "image/png"
          },
          "gameFiles": {
            "directory": "GameFiles",
            "fileCount": 1,
            "totalBytes": 1459978240
          },
          "manifest": {
            "file": "manifest.json",
            "algorithm": "SHA-256"
          },
          "launcher": {
            "file": "PLAY.exe"
          }
        }
        """;

    private const string ZeldaManifest = """
        {
          "formatVersion": 1,
          "algorithm": "SHA-256",
          "root": "GameFiles",
          "fileCount": 1,
          "totalBytes": 1459978240,
          "files": [
            {
              "path": "Legend of Zelda, The - Twilight Princess.iso",
              "size": 1459978240,
              "sha256": "ab8a811eb484ab5db065d69dde4a343bbebd897c5d74ad447632aff4722cc652"
            }
          ]
        }
        """;

    [Fact]
    public void Loads_the_real_metadata_shape_and_falls_back_when_text_is_empty()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", ZeldaMetadata);
        disc.Write("manifest.json", ZeldaManifest);
        Directory.CreateDirectory(System.IO.Path.Combine(disc.Path, "GameFiles"));

        var loaded = DiscLoader.Load(disc.Path);

        Assert.Null(loaded.BlockingError);
        Assert.NotNull(loaded.Presentation);
        Assert.Equal("The Legend of Zelda: Twilight Princess", loaded.Presentation.Title);
        Assert.Equal("The Legend of Zelda Twilight Princess", loaded.Presentation.FolderName);
        Assert.Null(loaded.Presentation.Subtitle);
        Assert.Null(loaded.Presentation.Edition);
        Assert.Equal(GameText.DefaultInstructions, loaded.Presentation.Instructions);
        Assert.Equal("UDF 2.01", loaded.Presentation.Filesystem);
        Assert.Null(loaded.Presentation.ArtworkPath);
        Assert.Equal(VerificationMode.Sha256, loaded.Verification.Mode);
        var entry = Assert.Single(loaded.Verification.Entries);
        Assert.Equal("Legend of Zelda, The - Twilight Princess.iso", entry.RelativePath);
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("1459978240", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_missing_or_invalid_metadata_without_throwing()
    {
        using var missing = new TempFolder();
        var missingDisc = DiscLoader.Load(missing.Path);
        Assert.Contains("metadata.json", missingDisc.BlockingError, StringComparison.OrdinalIgnoreCase);

        using var invalid = new TempFolder();
        invalid.Write("metadata.json", "{");
        var invalidDisc = DiscLoader.Load(invalid.Path);
        Assert.Contains("illisible", invalidDisc.BlockingError, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidDisc.CanCopy);
    }

    [Fact]
    public void Unknown_format_version_is_a_warning()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", """
            {
              "formatVersion": 9,
              "gameTitle": "Démo"
            }
            """);
        Directory.CreateDirectory(System.IO.Path.Combine(disc.Path, "GameFiles"));

        var loaded = DiscLoader.Load(disc.Path);

        Assert.Null(loaded.BlockingError);
        Assert.Equal("Démo", loaded.Presentation!.Title);
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("non reconnue", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_game_directory_that_leaves_the_disc()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", """
            {
              "gameTitle": "Démo",
              "gameFiles": { "directory": "../dehors" }
            }
            """);

        var loaded = DiscLoader.Load(disc.Path);

        Assert.NotNull(loaded.BlockingError);
        Assert.Contains("refusé", loaded.BlockingError, StringComparison.OrdinalIgnoreCase);
        Assert.False(loaded.CanCopy);
    }

    [Fact]
    public void Missing_game_directory_is_a_blocking_error()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", ZeldaMetadata);

        var loaded = DiscLoader.Load(disc.Path);

        Assert.Contains("introuvable", loaded.BlockingError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The Legend of Zelda: Twilight Princess", loaded.Presentation!.Title);
    }

    [Fact]
    public void Malformed_manifest_falls_back_to_size_checks()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", """
            {
              "gameTitle": "Démo",
              "manifest": { "file": "manifest.json", "algorithm": "SHA-256" }
            }
            """);
        disc.Write("manifest.json", "{");
        Directory.CreateDirectory(System.IO.Path.Combine(disc.Path, "GameFiles"));

        var loaded = DiscLoader.Load(disc.Path);

        Assert.Equal(VerificationMode.SizeOnly, loaded.Verification.Mode);
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("malformé", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_root_outside_the_disc_disables_hashes()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", """{ "gameTitle": "Démo" }""");
        disc.Write("manifest.json", """
            {
              "algorithm": "SHA-256",
              "root": "../ailleurs",
              "files": [ { "path": "a.bin", "size": 1, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ]
            }
            """);
        Directory.CreateDirectory(System.IO.Path.Combine(disc.Path, "GameFiles"));

        var loaded = DiscLoader.Load(disc.Path);

        Assert.Equal(VerificationMode.SizeOnly, loaded.Verification.Mode);
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("racine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Manifest_entry_that_escapes_is_not_kept()
    {
        using var disc = new TempFolder();
        var secret = disc.Write("secret.txt", "secret");
        disc.Write("metadata.json", """{ "gameTitle": "Démo" }""");
        disc.Write("GameFiles/inside.txt", "ok");
        var hash = Hash(System.IO.Path.Combine(disc.Path, "GameFiles", "inside.txt"));
        disc.Write("manifest.json", $$"""
            {
              "algorithm": "SHA-256",
              "root": "GameFiles",
              "fileCount": 2,
              "totalBytes": 8,
              "files": [
                { "path": "inside.txt", "size": 2, "sha256": "{{hash}}" },
                { "path": "../secret.txt", "size": 6, "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
              ]
            }
            """);

        var loaded = DiscLoader.Load(disc.Path);

        Assert.Equal(VerificationMode.Sha256, loaded.Verification.Mode);
        Assert.Single(loaded.Verification.Entries);
        Assert.Equal("inside.txt", loaded.Verification.Entries[0].RelativePath);
        Assert.Equal("secret", File.ReadAllText(secret));
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("refusée", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inventory_preserves_nested_files_and_empty_directories()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", """
            {
              "gameTitle": "Démo",
              "gameFiles": { "directory": "GameFiles", "fileCount": 9, "totalBytes": 1 }
            }
            """);
        disc.Write("GameFiles/nested/data.bin", "abcdef");
        Directory.CreateDirectory(System.IO.Path.Combine(disc.Path, "GameFiles", "empty"));

        var loaded = DiscLoader.Load(disc.Path);

        Assert.True(loaded.CanCopy);
        var file = Assert.Single(loaded.Inventory!.Files);
        Assert.Equal(System.IO.Path.Combine("nested", "data.bin"), file.RelativePath);
        Assert.Equal(6, file.Length);
        Assert.Contains(loaded.Inventory.Directories, directory => directory.Equals("nested", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded.Inventory.Directories, directory => directory.Equals("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("9", StringComparison.Ordinal));
    }

    [Fact]
    public void Skips_a_link_whose_target_leaves_the_game_directory()
    {
        using var disc = new TempFolder();
        var outside = disc.Write("outside.txt", "nope");
        var game = System.IO.Path.Combine(disc.Path, "GameFiles");
        Directory.CreateDirectory(game);
        var link = System.IO.Path.Combine(game, "escape.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        disc.Write("metadata.json", """{ "gameTitle": "Démo" }""");
        var loaded = DiscLoader.Load(disc.Path);

        Assert.True(loaded.CanCopy);
        Assert.DoesNotContain(loaded.Inventory!.Files, file => file.RelativePath.Contains("outside", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("sort du dossier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_an_artwork_path_that_leaves_the_disc()
    {
        using var disc = new TempFolder();
        disc.Write("metadata.json", """
            {
              "gameTitle": "Démo",
              "artwork": { "file": "../artwork.png" }
            }
            """);
        Directory.CreateDirectory(System.IO.Path.Combine(disc.Path, "GameFiles"));

        var loaded = DiscLoader.Load(disc.Path);

        Assert.Null(loaded.Presentation!.ArtworkPath);
        Assert.Contains(loaded.Notices, notice => notice.Message.Contains("illustration", StringComparison.OrdinalIgnoreCase));
    }

    private static string Hash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
