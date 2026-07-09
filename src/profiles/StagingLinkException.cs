namespace Modificus.Curator.Profiles;

/// <summary>
/// Thrown by <see cref="IProfileService.PrepareModRoot"/> when a staging link
/// cannot be created. The staging layer <b>never silently copies</b> (that would
/// duplicate repository files and surprise the user); it throws this instead.
/// </summary>
/// <remarks>
/// On Windows this typically means the profile's <c>staged/</c> directory is not
/// writable or the volume is not NTFS (junctions are NTFS-only); on Linux it
/// means no write access to <c>staged/</c>.
/// </remarks>
public sealed class StagingLinkException : InvalidOperationException
{
    public StagingLinkException(string message, Exception innerException)
        : base(message, innerException) { }
}
