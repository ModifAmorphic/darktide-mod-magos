using System.IO.Compression;
using System.Text;
using Magos.Modificus.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.SharedMods.Tests;

/// <summary>
/// <see cref="IModImportService"/>: folder + <c>.zip</c> import into the shared
/// store, upsert semantics, and the recorded source/version/path. Uses a temp
/// <c>SharedModsFolder</c> + a DI-resolved service (black-box via the interface).
/// </summary>
public sealed class ModImportServiceTests
{
    // ---- folder import -----------------------------------------------------

    [Fact]
    public void Import_folder_recursively_copies_files_to_SharedModsFolder_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var entry = fx.Service.Import(sourceDir, "DMF", new NoneSource(), "1.0");

        Assert.Equal(Path.Combine(fx.SharedFolder, "DMF"), entry.Path);
        Assert.True(File.Exists(Path.Combine(entry.Path, "readme.txt")));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(entry.Path, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(entry.Path, "sub", "nested.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(entry.Path, "sub", "nested.txt")));
    }

    // ---- zip import --------------------------------------------------------

    [Fact]
    public void Import_zip_extracts_to_SharedModsFolder_modName()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        MakeZip(zipPath, ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var entry = fx.Service.Import(zipPath, "DMF", new NoneSource(), "1.0");

        Assert.Equal(Path.Combine(fx.SharedFolder, "DMF"), entry.Path);
        Assert.True(File.Exists(Path.Combine(entry.Path, "readme.txt")));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(entry.Path, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(entry.Path, "sub", "nested.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(entry.Path, "sub", "nested.txt")));
    }

    [Theory]
    [InlineData("MOD.ZIP")]   // upper-case extension
    [InlineData("mod.Zip")]   // mixed case
    public void Import_detects_zip_by_extension_ordinal_ignore_case(string zipName)
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, zipName);
        MakeZip(zipPath, ("file.txt", "x"));

        var entry = fx.Service.Import(zipPath, "DMF", new NoneSource(), "1.0");

        Assert.True(File.Exists(Path.Combine(entry.Path, "file.txt")));
    }

    // ---- upsert ------------------------------------------------------------

    [Fact]
    public void Re_import_upserts_files_and_manifest_entry()
    {
        using var fx = new ImportFixture();
        var firstSource = fx.MakeSourceFolder("SrcA", ("a.txt", "a-content"), ("only-in-a.txt", "a-only"));
        fx.Service.Import(firstSource, "DMF", new NoneSource(), "1.0");
        Assert.True(File.Exists(Path.Combine(fx.SharedFolder, "DMF", "only-in-a.txt")));

        var secondSource = fx.MakeSourceFolder("SrcB", ("b.txt", "b-content"));

        var entry = fx.Service.Import(secondSource, "DMF", new NexusSource { ModId = 42 }, "2.0");

        // Files: SrcB's content replaced SrcA's. The "only-in-a.txt" file is gone
        // (the target was cleaned first → upsert, not merge).
        Assert.True(File.Exists(Path.Combine(entry.Path, "b.txt")));
        Assert.Equal("b-content", File.ReadAllText(Path.Combine(entry.Path, "b.txt")));
        Assert.False(File.Exists(Path.Combine(entry.Path, "only-in-a.txt")),
            "re-import should replace the target wholesale, not merge");

        // Manifest: single entry, with the NEW metadata.
        var only = Assert.Single(fx.Store.List());
        Assert.Equal("DMF", only.Name);
        Assert.Equal("2.0", only.ActualVersion);
        Assert.IsType<NexusSource>(only.Source);
        Assert.Equal(42, Assert.IsType<NexusSource>(only.Source).ModId);
    }

    [Fact]
    public void Re_import_replaces_a_prior_file_at_the_target_path()
    {
        // Defends against weird prior state: a FILE (not a dir) at the target
        // path must be cleaned before extract/copy, not left to clash.
        using var fx = new ImportFixture();
        var targetPath = Path.Combine(fx.SharedFolder, "DMF");
        Directory.CreateDirectory(fx.SharedFolder);
        File.WriteAllText(targetPath, "i am a file, not a dir");

        var sourceDir = fx.MakeSourceFolder("Src", ("file.txt", "x"));

        var entry = fx.Service.Import(sourceDir, "DMF", new NoneSource(), "1.0");

        Assert.True(Directory.Exists(entry.Path));
        Assert.True(File.Exists(Path.Combine(entry.Path, "file.txt")));
    }

    // ---- recorded metadata -------------------------------------------------

    [Fact]
    public void Import_records_source_actual_version_and_path_on_entry()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var entry = fx.Service.Import(
            sourceDir,
            "WeaponTweaks",
            new GitHubSource { Owner = "o", Repo = "r" },
            "v1.2.3");

        Assert.Equal("WeaponTweaks", entry.Name);
        Assert.Equal("v1.2.3", entry.ActualVersion);
        var gh = Assert.IsType<GitHubSource>(entry.Source);
        Assert.Equal("o", gh.Owner);
        Assert.Equal("r", gh.Repo);
        Assert.Equal(Path.Combine(fx.SharedFolder, "WeaponTweaks"), entry.Path);

        // And it round-trips through the manifest (persisted, not just in-memory).
        var reloaded = fx.ReloadStore().Get("WeaponTweaks")!;
        Assert.Equal("v1.2.3", reloaded.ActualVersion);
        Assert.IsType<GitHubSource>(reloaded.Source);
    }

    [Fact]
    public void Import_with_empty_version_records_empty_string()
    {
        // Local imports pass string.Empty for the version (untracked). The entry
        // round-trips with "" as ActualVersion.
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var entry = fx.Service.Import(sourceDir, "Local", new NoneSource(), "");

        Assert.Equal(string.Empty, entry.ActualVersion);
    }

    // ---- error paths -------------------------------------------------------

    [Fact]
    public void Import_throws_FileNotFoundException_when_source_does_not_exist()
    {
        using var fx = new ImportFixture();
        var ghost = Path.Combine(fx.TempRoot, "nope");

        Assert.Throws<FileNotFoundException>(() =>
            fx.Service.Import(ghost, "DMF", new NoneSource(), "1.0"));
    }

    [Fact]
    public void Import_throws_on_malformed_zip()
    {
        using var fx = new ImportFixture();
        // A non-zip file with a .zip extension: ExtractToDirectory will reject.
        var fakeZip = Path.Combine(fx.TempRoot, "broken.zip");
        File.WriteAllBytes(fakeZip, Encoding.UTF8.GetBytes("this is not a zip archive"));

        Assert.ThrowsAny<InvalidDataException>(() =>
            fx.Service.Import(fakeZip, "DMF", new NoneSource(), "1.0"));
    }

    [Fact]
    public void Import_throws_ArgumentException_for_null_or_whitespace_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "", new NoneSource(), "1.0"));
        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "   ", new NoneSource(), "1.0"));
    }

    // ---- path-traversal / confinement of modName --------------------------
    // modName is user-editable (the import modal lets the user rename). It is
    // combined into <SharedModsFolder>/<modName> and CleanTarget then does a
    // recursive Directory.Delete on it, so the name must be a single direct
    // child of the root: no separators, no "..", no absolute path. Any of those
    // could escape the root and trigger deletion outside it.

    [Theory]
    [InlineData("../evil")]     // parent traversal: escapes the root
    [InlineData("..")]          // bare parent: resolves above the root
    [InlineData("a/b")]         // forward-separator nesting under the root
    [InlineData("a\\b")]        // backslash nesting (separator on Windows; literal but invalid on Unix)
    public void Import_rejects_modName_that_escapes_or_nests_under_the_shared_root(string modName)
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, modName, new NoneSource(), "1.0"));

        // Defense: a rejected name must not have created or deleted anything
        // outside the shared root (the "../evil" case in particular).
        Assert.False(Directory.Exists(Path.Combine(fx.TempRoot, "evil")));
    }

    [Fact]
    public void Import_rejects_an_absolute_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));
        // A fully-qualified path at the system root: "/" on Unix, "C:\" on
        // Windows. Cross-platform via GetPathRoot of the current directory.
        var absolute = Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory())!, "abs-mod");

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, absolute, new NoneSource(), "1.0"));
    }

    [Fact]
    public void Import_accepts_a_normal_single_segment_modName()
    {
        // Happy path for the confinement check: a plain leaf name still imports.
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var entry = fx.Service.Import(sourceDir, "WeaponTweaks", new NoneSource(), "1.2");

        Assert.Equal(Path.Combine(fx.SharedFolder, "WeaponTweaks"), entry.Path);
        Assert.True(File.Exists(Path.Combine(entry.Path, "f.txt")));
    }

    [Fact]
    public void Import_rejects_null_arguments()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(null!, "DMF", new NoneSource(), "1.0"));
        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(sourceDir, "DMF", null!, "1.0"));
        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(sourceDir, "DMF", new NoneSource(), null!));
    }

    [Fact]
    public void Import_creates_SharedModsFolder_on_first_use()
    {
        // First-import safety: a missing SharedModsFolder is created (mirrors the
        // SharedModStore first-run posture).
        using var fx = new ImportFixture();
        // Wipe the folder the fixture pre-created so we exercise the import path.
        if (Directory.Exists(fx.SharedFolder))
        {
            Directory.Delete(fx.SharedFolder, recursive: true);
        }
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var entry = fx.Service.Import(sourceDir, "DMF", new NoneSource(), "1.0");

        Assert.True(Directory.Exists(fx.SharedFolder));
        Assert.True(File.Exists(Path.Combine(entry.Path, "f.txt")));
    }

    // ---- fixture + helpers -------------------------------------------------

    /// <summary>Per-test fixture: temp <c>SharedModsFolder</c> + a DI-resolved
    /// <see cref="IModImportService"/> + <see cref="ISharedModStore"/>.</summary>
    private sealed class ImportFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "magos-import-" + Guid.NewGuid());
        public string SharedFolder { get; }
        public IModImportService Service { get; }
        public ISharedModStore Store { get; }

        public ImportFixture()
        {
            SharedFolder = Path.Combine(TempRoot, "shared-mods");
            Directory.CreateDirectory(TempRoot);

            var config = MagosConfig.CreateDefault();
            config.SharedModsFolder = SharedFolder;
            _provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddSharedMods()
                .BuildServiceProvider();
            Service = _provider.GetRequiredService<IModImportService>();
            Store = _provider.GetRequiredService<ISharedModStore>();
        }

        public ISharedModStore ReloadStore()
        {
            var config = MagosConfig.CreateDefault();
            config.SharedModsFolder = SharedFolder;
            var provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddSharedMods()
                .BuildServiceProvider();
            return provider.GetRequiredService<ISharedModStore>();
        }

        /// <summary>Creates <c>&lt;TempRoot&gt;/&lt;dirName&gt;</c> with the given
        /// (relativePath, content) files, returning its full path.</summary>
        public string MakeSourceFolder(string dirName, params (string Path, string Content)[] files)
        {
            var dir = Path.Combine(TempRoot, dirName);
            Directory.CreateDirectory(dir);
            foreach (var (relPath, content) in files)
            {
                var full = Path.Combine(dir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            return dir;
        }

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
    }

    /// <summary>Builds a real .zip at <paramref name="path"/> from the given
    /// (entryPath, content) pairs (uses the BCL ZipFile API, so a real archive
    /// round-trips through the import).</summary>
    private static void MakeZip(string path, params (string Entry, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var ze = archive.CreateEntry(entry);
            using var writer = new StreamWriter(ze.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
