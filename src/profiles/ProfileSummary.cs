namespace Modificus.Curator.Profiles;

/// <summary>
/// Lightweight projection of a <see cref="Profile"/> for listing -- just enough
/// to render a profile picker without loading every profile's full mod list.
/// </summary>
public sealed record ProfileSummary(Guid Id, string Name);
