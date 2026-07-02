using System.Text;
using System.Text.Json;
using Magos.Modificus.Config;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.SharedMods;

/// <summary>
/// Filesystem-backed <see cref="ISharedModStore"/>. The manifest lives at
/// <c>&lt;SharedModsFolder&gt;/shared-manifest.json</c> as a JSON array of
/// <see cref="SharedModEntry"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="MagosConfig.SharedModsFolder"/> is created on first run
/// (mirrors how <c>ProfileService</c> creates <c>ProfilesBaseFolder</c>). A
/// missing manifest file is first-run safe (empty list); the file is created on
/// the first mutation.</para>
/// <para>
/// Read-through, like <c>ProfileService</c>: each operation reads the manifest
/// fresh from disk (small file; avoids stale-state across instances), and
/// mutations write it back in full. Registered as a singleton — no per-request
/// state; all state lives on disk.</para>
/// <para>
/// Concurrency is not coordinated in Phase 2 (single-UI-thread assumption,
/// matching <c>ProfileService</c>); a future phase revisits it if needed.</para>
/// </remarks>
internal sealed class SharedModStore : ISharedModStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    // Consistent with profile.json / mods.lst: UTF-8 without BOM (hand-edits +
    // diffs stay clean; no consumer expects a BOM here).
    private static readonly Encoding ManifestEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _baseFolder;
    private readonly string _manifestPath;
    private readonly ILogger<SharedModStore> _logger;

    public SharedModStore(MagosConfig config, ILogger<SharedModStore> logger)
    {
        // SharedModsFolder is non-null by MagosConfig contract (defaults to
        // <app-data>/shared-mods). CreateDirectory is idempotent — first-run safe.
        _baseFolder = config.SharedModsFolder;
        _manifestPath = Path.Combine(_baseFolder, "shared-manifest.json");
        _logger = logger;
        Directory.CreateDirectory(_baseFolder);
    }

    /// <inheritdoc />
    public IReadOnlyList<SharedModEntry> List() => ReadManifest();

    /// <inheritdoc />
    public SharedModEntry? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ReadManifest().FirstOrDefault(
            e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Add(SharedModEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new ArgumentException("Shared mod entry Name must not be null or whitespace.", nameof(entry));
        }

        var entries = ReadManifest();
        // Upsert: replace an existing same-named entry, else append. A re-add is
        // an update (e.g. policy/version change), not a duplicate.
        var updated = entries
            .Where(e => !string.Equals(e.Name, entry.Name, StringComparison.Ordinal))
            .Append(entry)
            .ToList();

        WriteManifest(updated);
        _logger.LogInformation("Shared store: upserted {Mod} (policy={Policy})", entry.Name, entry.Policy);
    }

    /// <inheritdoc />
    public void Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var entries = ReadManifest();
        var updated = entries
            .Where(e => !string.Equals(e.Name, name, StringComparison.Ordinal))
            .ToList();

        if (updated.Count == entries.Count)
        {
            // Idempotent: nothing matched — no-op (don't even write).
            return;
        }

        WriteManifest(updated);
        _logger.LogInformation("Shared store: removed {Mod} from manifest (files untouched)", name);
    }

    // ---- manifest persistence ------------------------------------------------

    private List<SharedModEntry> ReadManifest()
    {
        if (!File.Exists(_manifestPath))
        {
            return new List<SharedModEntry>();
        }

        try
        {
            using var stream = File.OpenRead(_manifestPath);
            var entries = JsonSerializer.Deserialize<List<SharedModEntry>>(stream);
            return entries ?? new List<SharedModEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt manifest must not crash staging. Treat as empty (the
            // worst case: no mods share) and log loudly. This mirrors
            // ProfileService's "skip unreadable, keep going" posture.
            _logger.LogError(ex, "Shared manifest at {Path} is unreadable; treating as empty.", _manifestPath);
            return new List<SharedModEntry>();
        }
    }

    private void WriteManifest(List<SharedModEntry> entries)
    {
        Directory.CreateDirectory(_baseFolder);
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(_manifestPath, json, ManifestEncoding);
    }
}
