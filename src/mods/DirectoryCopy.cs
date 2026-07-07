namespace Modificus.Curator.Mods;

/// <summary>
/// Recursively copies a directory tree, creating the target as it goes. The
/// shared implementation behind two callers that both need a faithful,
/// recursive source-to-target copy:
/// <list type="bullet">
/// <item><description><see cref="ModRepository.Relocate"/>'s cross-volume path,
/// where <see cref="Directory.Move"/> throws <see cref="IOException"/> (it does
/// not fall back to a copy) so a real cross-volume move is a copy + source
/// delete.</description></item>
/// <item><description><see cref="ModImportService"/>'s folder-import path, which
/// copies the picked folder itself (not its contents) into the version
/// directory. The archive-import path uses SharpCompress's per-entry
/// <c>WriteEntryToDirectory</c> instead, but the folder path (a picked or
/// pre-extracted directory) still needs this faithful recursive copy.</description></item>
/// </list>
/// </summary>
internal static class DirectoryCopy
{
    /// <summary>
    /// Recursively copies <paramref name="sourceDir"/> to
    /// <paramref name="targetDir"/>. The target tree is created as the copy
    /// proceeds; existing files at the target are overwritten in place (so a
    /// re-copy onto a prior tree does not merge stale files at the leaves that
    /// the source no longer has, though it will not prune extra target entries
    /// either, callers that need a clean target wipe it first).
    /// </summary>
    /// <param name="sourceDir">An existing directory to copy from.</param>
    /// <param name="targetDir">The destination directory (created if
    /// absent).</param>
    public static void Copy(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // Files first (cheap), then recurse into subdirectories.
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            Copy(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }
}
