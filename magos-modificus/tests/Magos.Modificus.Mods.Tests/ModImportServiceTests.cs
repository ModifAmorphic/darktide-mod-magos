using System.IO.Compression;
using System.Text;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Mods.Tests;

/// <summary>
/// <see cref="IModImportService"/>: container find/create + version dedup +
/// <c>isLatest</c> flip + the <c>modName</c> path-traversal confinement retained
/// from Track B's review fix, AND the source-structure validation (both folder +
/// <c>.zip</c> require exactly one base directory with a matching
/// <c>&lt;base&gt;.mod</c> descriptor; the base folder is preserved under
/// <c>&lt;versionFolder&gt;/&lt;base&gt;/</c>), AND the two peeks the add flow
/// uses for the base-name collision hard-block:
/// <see cref="IModImportService.GetBaseName"/> (validates + returns the base name
/// without creating anything) + <see cref="IModImportService.FindExistingContainer"/>
/// (resolves the would-be dedup container, without creating anything). Uses a
/// temp <c>ModsFolder</c> + a DI-resolved service (black-box).
/// </summary>
public sealed class ModImportServiceTests
{
    // ---- container resolution (find/create) --------------------------------

    [Fact]
    public void Import_creates_a_new_untracked_container_when_absent()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

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
        var firstDir = fx.MakeSourceModFolder("SrcA", ("a.txt", "a"));
        var (firstId, _) = fx.Service.Import(firstDir, "DMF", new UntrackedSource(), "1.0");

        var secondDir = fx.MakeSourceModFolder("SrcB", ("b.txt", "b"));
        var (secondId, _) = fx.Service.Import(secondDir, "DMF", new UntrackedSource(), "1.0");

        // Same container (dedup by name).
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void Import_dedups_Nexus_by_mod_id_on_re_import()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
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
        var dir = fx.MakeSourceModFolder("Src");

        var (untrackedId, _) = fx.Service.Import(dir, "WT", new UntrackedSource(), "1.0");
        var (nexusId, _) = fx.Service.Import(dir, "WT", new NexusSource { ModId = 99 }, "1.0");

        Assert.NotEqual(untrackedId, nexusId);
    }

    // ---- version resolution (dedup + isLatest flip) -----------------------

    [Fact]
    public void Import_same_versionString_dedups_reusing_the_version_folder()
    {
        using var fx = new ImportFixture();
        var firstDir = fx.MakeSourceModFolder("SrcA", ("a.txt", "a"));
        var (containerId, _) = fx.Service.Import(firstDir, "DMF", new UntrackedSource(), "1.0");
        var firstFolder = fx.Repo.Get(containerId)!.Versions[0].Folder;

        var secondDir = fx.MakeSourceModFolder("SrcB", ("b.txt", "b"));
        var (_, _) = fx.Service.Import(secondDir, "DMF", new UntrackedSource(), "1.0");

        var container = fx.Repo.Get(containerId);
        var version = Assert.Single(container!.Versions);
        Assert.Equal(firstFolder, version.Folder); // folder reused
        Assert.Equal("1.0", version.VersionString);

        // Files refreshed (no merge). The reused version folder now holds the
        // second import's base folder (SrcB), with SrcA gone.
        var versionPath = fx.Repo.GetVersionFolderPath(containerId, firstFolder);
        Assert.False(Directory.Exists(Path.Combine(versionPath, "SrcA")));
        Assert.True(File.Exists(Path.Combine(versionPath, "SrcB", "b.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "SrcB", "SrcB.mod")));
    }

    [Fact]
    public void Import_new_versionString_flips_isLatest_to_the_new_version()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var (containerId, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "1.0");
        var (_, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "2.0");

        var container = fx.Repo.Get(containerId);
        Assert.Equal(2, container!.Versions.Count);
        var latest = Assert.Single(container.Versions, v => v.IsLatest);
        Assert.Equal("2.0", latest.VersionString);
    }

    // ---- folder + zip placement (base folder preserved) -------------------

    [Fact]
    public void Import_folder_copies_the_folder_itself_into_the_version_folder()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder(
            "Src", ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var (containerId, _) = fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        // The folder is copied ITSELF (name preserved), not its contents, so the
        // shape is <versionPath>/Src/<files> (consistent with the zip path).
        Assert.Equal("hi", File.ReadAllText(Path.Combine(versionPath, "Src", "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(versionPath, "Src", "sub", "nested.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "Src", "Src.mod")));
    }

    [Fact]
    public void Import_zip_extracts_preserving_the_single_top_level_base_folder()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        // The single top-level base folder is preserved at the version root:
        // <versionPath>/dmf/<files>.
        Assert.Equal("hi", File.ReadAllText(Path.Combine(versionPath, "dmf", "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(versionPath, "dmf", "sub", "nested.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "dmf.mod")));
    }

    [Theory]
    [InlineData("MOD.ZIP")]
    [InlineData("mod.Zip")]
    public void Import_detects_zip_by_extension_ordinal_ignore_case(string zipName)
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, zipName);
        fx.MakeModZip(zipPath, "dmf", ("file.txt", "x"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "file.txt")));
    }

    // ---- source-structure validation (zip + folder) -----------------------
    //
    // Both import kinds require exactly one base directory with a <base>.mod
    // descriptor (filename matches the base folder name). Validation runs BEFORE
    // any file is placed or container/version is created, so an invalid source
    // leaves nothing on disk + creates no container.

    [Fact]
    public void Import_zip_with_single_base_and_matching_descriptor_succeeds_and_preserves_structure()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("scripts/init.lua", "-- dmf"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(Directory.Exists(Path.Combine(versionPath, "dmf")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "dmf.mod")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "scripts", "init.lua")));
    }

    [Fact]
    public void Import_zip_with_multiple_top_level_folders_is_rejected_and_extracts_nothing()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "two-mods.zip");
        MakeZip(zipPath, ("modA/modA.mod", "a"), ("modB/modB.mod", "b"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "TwoMods", new UntrackedSource(), "1.0"));
        Assert.Contains("exactly one top-level mod folder", ex.Message);

        // No container created, nothing extracted (validation precedes both).
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_zip_with_loose_top_level_files_is_rejected()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "loose.zip");
        MakeZip(zipPath, ("readme.txt", "no folder here"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "Loose", new UntrackedSource(), "1.0"));
        Assert.Contains("loose top-level file", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_zip_missing_the_base_mod_descriptor_is_rejected()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "no-mod.zip");
        // Base folder present, but no dmf/dmf.mod.
        MakeZip(zipPath, ("dmf/scripts/init.lua", "-- no descriptor"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0"));
        Assert.Contains("dmf/dmf.mod", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_zip_with_a_mismatched_descriptor_name_is_rejected()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mismatch.zip");
        // Base folder 'dmf', but the descriptor is 'other.mod' (not 'dmf.mod').
        MakeZip(zipPath, ("dmf/other.mod", "wrong name"));

        Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0"));
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_folder_preserves_the_folder_name_and_validates_mod_descriptor()
    {
        using var fx = new ImportFixture();
        // Container display name differs from the base folder name; the base
        // folder name (not the display name) is what's preserved on disk.
        var sourceDir = fx.MakeSourceModFolder("Power_DI", ("scripts/init.lua", "-- pi"));

        var (containerId, _) = fx.Service.Import(sourceDir, "Power DI", new UntrackedSource(), "1.2");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(Directory.Exists(Path.Combine(versionPath, "Power_DI")));
        Assert.True(File.Exists(Path.Combine(versionPath, "Power_DI", "Power_DI.mod")));
        Assert.True(File.Exists(Path.Combine(versionPath, "Power_DI", "scripts", "init.lua")));
    }

    [Fact]
    public void Import_folder_missing_the_base_mod_descriptor_is_rejected()
    {
        using var fx = new ImportFixture();
        // A folder with files but no matching <folder>.mod.
        var sourceDir = fx.MakeSourceFolder("NoDescriptor", ("scripts/init.lua", "x"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(sourceDir, "NoDescriptor", new UntrackedSource(), "1.0"));
        Assert.Contains("NoDescriptor.mod", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_folder_that_is_a_parent_of_mods_is_rejected()
    {
        // The picked folder must BE the mod, not a parent containing several
        // mods. A parent has no matching <parent>.mod, so it's rejected (this is
        // the structural guard against a mis-pick).
        using var fx = new ImportFixture();
        var parentDir = Path.Combine(fx.TempRoot, "MyMods");
        Directory.CreateDirectory(Path.Combine(parentDir, "modA"));
        Directory.CreateDirectory(Path.Combine(parentDir, "modB"));
        File.WriteAllText(Path.Combine(parentDir, "modA", "modA.mod"), "a");
        File.WriteAllText(Path.Combine(parentDir, "modB", "modB.mod"), "b");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(parentDir, "MyMods", new UntrackedSource(), "1.0"));
        Assert.Contains("MyMods.mod", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_folder_that_is_empty_is_rejected()
    {
        using var fx = new ImportFixture();
        var sourceDir = Path.Combine(fx.TempRoot, "Empty");
        Directory.CreateDirectory(sourceDir);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(sourceDir, "Empty", new UntrackedSource(), "1.0"));
        Assert.Contains("empty", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    // ---- empty version + round-trip ---------------------------------------

    [Fact]
    public void Import_with_empty_version_records_an_empty_version_string()
    {
        // Untracked imports pass string.Empty for the version (no tag).
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        var (containerId, versionString) = fx.Service.Import(sourceDir, "Local", new UntrackedSource(), "");

        Assert.Equal(string.Empty, versionString);
        var version = Assert.Single(fx.Repo.Get(containerId)!.Versions);
        Assert.Equal(string.Empty, version.VersionString);
    }

    [Fact]
    public void Import_persists_container_and_version_through_a_new_repository_instance()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

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
        var sourceDir = fx.MakeSourceModFolder("Src");

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "", new UntrackedSource(), "1.0"));
        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "   ", new UntrackedSource(), "1.0"));
    }

    // modName confinement: the import target is now an opaque UUID folder
    // (not modName-derived), but the confinement is retained as defense-in-depth
    // (a malformed name would later clash with the untracked-by-name index).

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Import_rejects_modName_that_escapes_or_nests_under_the_shared_root(string modName)
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

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
        var sourceDir = fx.MakeSourceModFolder("Src");
        var absolute = Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory())!, "abs-mod");

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, absolute, new UntrackedSource(), "1.0"));
    }

    [Fact]
    public void Import_accepts_a_normal_single_segment_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        var (containerId, _) = fx.Service.Import(sourceDir, "WeaponTweaks", new UntrackedSource(), "1.2");

        var container = fx.Repo.Get(containerId);
        Assert.Equal("WeaponTweaks", container!.Name);
    }

    [Fact]
    public void Import_rejects_null_arguments()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

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
        var sourceDir = fx.MakeSourceModFolder("Src");

        fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        Assert.True(Directory.Exists(fx.ModsFolder));
    }

    // ---- GetBaseName (peek) ------------------------------------------------
    //
    // GetBaseName validates the source structure + returns the base folder name
    // WITHOUT creating a container or version. Same validation as Import (reuses
    // the private validators); an invalid source throws, leaving nothing on disk.

    [Fact]
    public void GetBaseName_returns_the_base_name_from_a_valid_zip_without_creating_anything()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("scripts/init.lua", "-- dmf"));

        var baseName = fx.Service.GetBaseName(zipPath);

        Assert.Equal("dmf", baseName);
        // A peek creates nothing: no container, no version folder.
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void GetBaseName_returns_the_base_name_from_a_valid_folder_without_creating_anything()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Power_DI", ("scripts/init.lua", "-- pi"));

        var baseName = fx.Service.GetBaseName(sourceDir);

        Assert.Equal("Power_DI", baseName);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void GetBaseName_throws_for_an_invalid_source_and_creates_nothing()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "two-mods.zip");
        MakeZip(zipPath, ("modA/modA.mod", "a"), ("modB/modB.mod", "b"));

        var ex = Assert.Throws<InvalidOperationException>(() => fx.Service.GetBaseName(zipPath));
        Assert.Contains("exactly one top-level mod folder", ex.Message);

        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void GetBaseName_throws_for_a_missing_source()
    {
        using var fx = new ImportFixture();
        Assert.Throws<FileNotFoundException>(() =>
            fx.Service.GetBaseName(Path.Combine(fx.TempRoot, "nope")));
    }

    [Fact]
    public void GetBaseName_then_Import_produce_the_same_base_folder()
    {
        // The peek + the import agree on the base folder name (both run the same
        // validation); Import then places files under <versionFolder>/<base>/.
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("scripts/init.lua", "-- dmf"));

        var peeked = fx.Service.GetBaseName(zipPath);
        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        Assert.Equal("dmf", peeked);
        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(Directory.Exists(Path.Combine(versionPath, "dmf")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "dmf.mod")));
    }

    // ---- FindExistingContainer (dedup peek) --------------------------------
    //
    // FindExistingContainer resolves the container an import WOULD dedup to,
    // without creating anything. Mirrors Import's container resolution minus the
    // create (the dedup rules live in one place: this service).

    [Fact]
    public void FindExistingContainer_returns_the_untracked_container_by_name()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var (createdId, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "1.0");

        var found = fx.Service.FindExistingContainer(new UntrackedSource(), "DMF");

        Assert.NotNull(found);
        Assert.Equal(createdId, found!.Id);
    }

    [Fact]
    public void FindExistingContainer_returns_null_for_an_unknown_untracked_name()
    {
        using var fx = new ImportFixture();

        var found = fx.Service.FindExistingContainer(new UntrackedSource(), "Nobody");

        Assert.Null(found);
    }

    [Fact]
    public void FindExistingContainer_creates_nothing()
    {
        using var fx = new ImportFixture();

        _ = fx.Service.FindExistingContainer(new UntrackedSource(), "Ghost");

        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void FindExistingContainer_resolves_nexus_by_mod_id()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var (createdId, _) = fx.Service.Import(dir, "WT", nexus, "1.0");

        var found = fx.Service.FindExistingContainer(new NexusSource { ModId = 4242 }, "other");

        Assert.NotNull(found);
        Assert.Equal(createdId, found!.Id);
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
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
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
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            return provider.GetRequiredService<IModRepository>();
        }

        /// <summary>Creates a valid mod source folder <c>&lt;TempRoot&gt;/&lt;baseName&gt;</c>
        /// containing <c>&lt;baseName&gt;.mod</c> plus the given (relativePath, content)
        /// files, returning its full path.</summary>
        public string MakeSourceModFolder(string baseName, params (string Path, string Content)[] files)
        {
            var dir = Path.Combine(TempRoot, baseName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, baseName + ".mod"), baseName);
            foreach (var (relPath, content) in files)
            {
                var full = Path.Combine(dir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            return dir;
        }

        /// <summary>Creates <c>&lt;TempRoot&gt;/&lt;dirName&gt;</c> with the given
        /// (relativePath, content) files (NO <c>.mod</c> descriptor), returning its
        /// full path. Used by tests that need a structurally-invalid source.</summary>
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

        /// <summary>Builds a valid mod <c>.zip</c> at <paramref name="path"/> whose
        /// single top-level folder is <paramref name="baseName"/>, containing
        /// <c>&lt;baseName&gt;/&lt;baseName&gt;.mod</c> plus the given files (each placed
        /// under <c>&lt;baseName&gt;/</c>). Returns <paramref name="path"/>.</summary>
        public string MakeModZip(string path, string baseName, params (string Path, string Content)[] files)
        {
            var entries = new List<(string Entry, string Content)>
            {
                ($"{baseName}/{baseName}.mod", baseName),
            };
            entries.AddRange(files.Select(f => ($"{baseName}/{f.Path}", f.Content)));
            MakeZip(path, entries.ToArray());
            return path;
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

    /// <summary>Builds a raw <c>.zip</c> at <paramref name="path"/> from the given
    /// (entryPath, content) pairs, with no validation. Used to construct
    /// structurally-invalid archives (multiple top-level folders, loose files,
    /// missing/mismatched descriptor) + by <c>MakeModZip</c> for valid ones.
    /// </summary>
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
