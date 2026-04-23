namespace McSH.Models;

/// <summary>
/// A single result from a Modrinth search query, stored in AppState for install-by-number.
/// </summary>
public record ModSearchHit(
    string ProjectId,
    string Slug,
    string Title,
    string Author,
    long   Downloads,
    string Description);
