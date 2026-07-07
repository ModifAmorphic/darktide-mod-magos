namespace Modificus.Curator.Profiles;

/// <summary>
/// Creates a directory symlink at <paramref name="linkPath"/> pointing to
/// <paramref name="targetPath"/>. The default implementation (registered by
/// <c>AddProfiles</c>) wraps <see cref="System.IO.Directory.CreateSymbolicLink"/>;
/// tests substitute it to exercise the symlink-failure path without depending on
/// platform permissions.
/// </summary>
/// <remarks>
/// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
/// (or <see cref="SymlinkStagingException"/> at the staging call site) when the
/// link can't be created -- e.g. Windows without symlink permissions / Developer
/// Mode. The staging layer never silently copies.
/// </remarks>
public delegate void SymlinkCreator(string linkPath, string targetPath);
