namespace Magos.Modificus.Profiles;

/// <summary>
/// Thrown by <see cref="IProfileService.PrepareModRoot"/> when a staged symlink
/// cannot be created — typically Windows without symlink permissions or
/// Developer Mode. The staging layer <b>never silently copies</b> (that would
/// defeat the shared-mod purpose + surprise the user); it throws this instead.
/// </summary>
public sealed class SymlinkStagingException : InvalidOperationException
{
    public SymlinkStagingException(string message, Exception innerException)
        : base(message, innerException) { }
}
