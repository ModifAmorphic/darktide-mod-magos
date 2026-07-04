using System.IO.Compression;
using System.Text;
using Magos.Modificus.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Mods.Tests;

/// <summary>
/// <see cref="IModImportService"/>: container find/create + version dedup +
/// <c>isLatest</c> flip + the <c>modName</c> path-traversal confinement retained
/// from Track B's review fix. Folder + <c>.zip</c> import into the repository.
/// Uses a temp <c>ModsFolder</c> + a DI-resolved service (black-box).
/// </summary>
public sealed class ModImportServiceTests
{
    // ---- container resolution (find/create) --------------------------------

    [Fact]
    public void Import_creates_a_new_untracked_container_when_absent()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var (containerId, version) = fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        var container = fx.Repo.Get(containerId);
        Assert.NotNull(container);
        Assert.Equal("DMF", container!.Name);
        Assert.IsType<UntrackedSource>(container.Source);
        Assert.Equal("1.0", version);
    }

    [Fact]
    public void Import_dedups_untracked_by_name_on_re_import()
    {
        using var fx = new ImportFixture();
        var firstDir = fx.MakeSourceFolder("SrcA", ("a.txt", "a"));
        var (firstId, _) = fx.Service.Import(firstDir, "DMF", new UntrackedSource(), "1.0");

        var secondDir = fx.MakeSourceFolder("SrcB", ("b.txt", "b"));
        var (secondId, _) = fx.Service.Import(secondDir, "DMF", new UntrackedSource(), "1.0");

        // Same container (dedup by name).
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void Import_dedups_Nexus_by_mod_id_on_re_import()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceFolder("Src", ("f.txt", "x"));
        var nexus = new NexusSource { ModId = 4242 };

        var (firstId, _) = fx.Service.Import(dir, "WT-A", nexus, "1.0");
        var (secondId, _) = fx.Service.Import(dir, "WT-B", nexus, "2.0"); // different name, same source

        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void Import_does_not_dedup_across_different_sources()
    {
        // Goal #4: different sources never collide. Same name, different source
        // types yield different containers.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var (untrackedId, _) = fx.Service.Import(dir, "WT", new UntrackedSource(), "1.0");
        var (nexusId, _) = fx.Service.Import(dir, "WT", new NexusSource { ModId = 99 }, "1.0");

        Assert.NotEqual(untrackedId, nexusId);
    }

    // ---- version resolution (dedup + isLatest flip) -----------------------

    [Fact]
    public void Import_same_versionString_dedups_reusing_the_version_folder()
    {
        using var fx = new ImportFixture();
        var firstDir = fx.MakeSourceFolder("SrcA", ("a.txt", "a"));
        var (containerId, _) = fx.Service.Import(firstDir, "DMF", new UntrackedSource(), "1.0");
        var firstFolder = fx.Repo.Get(containerId)!.Versions[0].Folder;

        var secondDir = fx.MakeSourceFolder("SrcB", ("b.txt", "b"));
        var (_, _) = fx.Service.Import(secondDir, "DMF", new UntrackedSource(), "1.0");

        var container = fx.Repo.Get(containerId);
        var version = Assert.Single(container!.Versions);
        Assert.Equal(firstFolder, version.Folder); // folder reused
        Assert.Equal("1.0", version.VersionString);

        // Files refreshed (no merge).
        var versionPath = fx.Repo.GetVersionFolderPath(containerId, firstFolder);
        Assert.False(File.Exists(Path.Combine(versionPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "b.txt")));
    }

    [Fact]
    public void Import_new_versionString_flips_isLatest_to_the_new_version()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceFolder("Src", ("f.txt", "x"));
        var (containerId, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "1.0");
        var (_, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "2.0");

        var container = fx.Repo.Get(containerId);
        Assert.Equal(2, container!.Versions.Count);
        var latest = Assert.Single(container.Versions, v => v.IsLatest);
        Assert.Equal("2.0", latest.VersionString);
    }

    // ---- folder + zip extraction ------------------------------------------

    [Fact]
    public void Import_folder_copies_files_into_the_resolved_version_folder()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var (containerId, _) = fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(versionPath, "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(versionPath, "sub", "nested.txt")));
    }

    [Fact]
    public void Import_zip_extracts_into_the_resolved_version_folder()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        MakeZip(zipPath, ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(versionPath, "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(versionPath, "sub", "nested.txt")));
    }

    [Theory]
    [InlineData("MOD.ZIP")]
    [InlineData("mod.Zip")]
    public void Import_detects_zip_by_extension_ordinal_ignore_case(string zipName)
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, zipName);
        MakeZip(zipPath, ("file.txt", "x"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(File.Exists(Path.Combine(versionPath, "file.txt")));
    }

    // ---- empty version + round-trip ---------------------------------------

    [Fact]
    public void Import_with_empty_version_records_an_empty_version_string()
    {
        // Untracked imports pass string.Empty for the version (no tag).
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var (containerId, versionString) = fx.Service.Import(sourceDir, "Local", new UntrackedSource(), "");

        Assert.Equal(string.Empty, versionString);
        var version = Assert.Single(fx.Repo.Get(containerId)!.Versions);
        Assert.Equal(string.Empty, version.VersionString);
    }

    [Fact]
    public void Import_persists_container_and_version_through_a_new_repository_instance()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var (containerId, _) = fx.Service.Import(
            sourceDir,
            "WeaponTweaks",
            new GitHubSource { Owner = "o", Repo = "r" },
            "v1.2.3");

        var reloaded = fx.ReloadRepo();
        var container = reloaded.Get(containerId);
        Assert.NotNull(container);
        Assert.Equal("WeaponTweaks", container!.Name);
        var gh = Assert.IsType<GitHubSource>(container.Source);
        Assert.Equal("o", gh.Owner);
        Assert.Equal("r", gh.Repo);
        var version = Assert.Single(container.Versions);
        Assert.Equal("v1.2.3", version.VersionString);
    }

    // ---- error paths + modName confinement (retained from Track B) --------

    [Fact]
    public void Import_throws_FileNotFoundException_when_source_does_not_exist()
    {
        using var fx = new ImportFixture();
        var ghost = Path.Combine(fx.TempRoot, "nope");

        Assert.Throws<FileNotFoundException>(() =>
            fx.Service.Import(ghost, "DMF", new UntrackedSource(), "1.0"));
    }

    [Fact]
    public void Import_throws_on_malformed_zip()
    {
        using var fx = new ImportFixture();
        var fakeZip = Path.Combine(fx.TempRoot, "broken.zip");
        File.WriteAllBytes(fakeZip, Encoding.UTF8.GetBytes("this is not a zip archive"));

        Assert.ThrowsAny<InvalidDataException>(() =>
            fx.Service.Import(fakeZip, "DMF", new UntrackedSource(), "1.0"));
    }

    [Fact]
    public void Import_throws_ArgumentException_for_null_or_whitespace_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "", new UntrackedSource(), "1.0"));
        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "   ", new UntrackedSource(), "1.0"));
    }

    // modName confinement: the import target is now an opaque UUID folder
    // (not modName-derived), but the confinement is retained as defense-in-depth
    // (a malformed name would later clash with symlink-name sanitization in
    // staging or confuse the untracked-by-name index).

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Import_rejects_modName_that_escapes_or_nests_under_the_shared_root(string modName)
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, modName, new UntrackedSource(), "1.0"));

        // Defense: a rejected name must not have created or deleted anything
        // outside the mod root (the "../evil" case in particular).
        Assert.False(Directory.Exists(Path.Combine(fx.TempRoot, "evil")));
    }

    [Fact]
    public void Import_rejects_an_absolute_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));
        var absolute = Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory())!, "abs-mod");

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, absolute, new UntrackedSource(), "1.0"));
    }

    [Fact]
    public void Import_accepts_a_normal_single_segment_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        var (containerId, _) = fx.Service.Import(sourceDir, "WeaponTweaks", new UntrackedSource(), "1.2");

        var container = fx.Repo.Get(containerId);
        Assert.Equal("WeaponTweaks", container!.Name);
    }

    [Fact]
    public void Import_rejects_null_arguments()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(null!, "DMF", new UntrackedSource(), "1.0"));
        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(sourceDir, "DMF", null!, "1.0"));
        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), null!));
    }

    [Fact]
    public void Import_creates_ModsFolder_on_first_use()
    {
        // First-import safety: a missing ModsFolder is created.
        using var fx = new ImportFixture();
        if (Directory.Exists(fx.ModsFolder))
        {
            Directory.Delete(fx.ModsFolder, recursive: true);
        }
        var sourceDir = fx.MakeSourceFolder("Src", ("f.txt", "x"));

        fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        Assert.True(Directory.Exists(fx.ModsFolder));
    }

    // ---- fixture + helpers -------------------------------------------------

    /// <summary>Per-test fixture: temp <c>ModsFolder</c> + a DI-resolved
    /// <see cref="IModImportService"/> + <see cref="IModRepository"/>.</summary>
    private sealed class ImportFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "magos-import-" + Guid.NewGuid());
        public string ModsFolder { get; }
        public IModImportService Service { get; }
        public IModRepository Repo { get; }

        public ImportFixture()
        {
            ModsFolder = Path.Combine(TempRoot, "mods");
            Directory.CreateDirectory(TempRoot);

            var config = MagosConfig.CreateDefault();
            config.ModsFolder = ModsFolder;
            _provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            Service = _provider.GetRequiredService<IModImportService>();
            Repo = _provider.GetRequiredService<IModRepository>();
        }

        public IModRepository ReloadRepo()
        {
            var config = MagosConfig.CreateDefault();
            config.ModsFolder = ModsFolder;
            var provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            return provider.GetRequiredService<IModRepository>();
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
    /// (entryPath, content) pairs.</summary>
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
