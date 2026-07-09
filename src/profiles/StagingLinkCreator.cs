namespace Modificus.Curator.Profiles;

/// <summary>
/// Creates a directory staging link at <paramref name="linkPath"/> pointing to
/// <paramref name="targetPath"/>. The default implementation (registered by
/// <c>AddProfiles</c>) is platform-selective: an NTFS <b>junction</b> on Windows
/// (privilege-free; no Developer Mode / admin required) and a <b>symlink</b> via
/// <see cref="System.IO.Directory.CreateSymbolicLink"/> on Linux. Tests
/// substitute it to exercise the staging-link-failure path without depending on
/// platform permissions.
/// </summary>
/// <remarks>
/// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
/// when the link can't be created (e.g. Windows on a non-NTFS volume, or no
/// write access to the profile's <c>staged/</c> directory). The staging layer
/// never silently copies.
/// </remarks>
public delegate void StagingLinkCreator(string linkPath, string targetPath);
