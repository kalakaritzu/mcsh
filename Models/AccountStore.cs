namespace McSH.Models;

/// <summary>
/// Persists all authenticated accounts to accounts.json.
/// Replaces the single-account auth.json (legacy files are migrated automatically).
/// </summary>
public class AccountStore
{
    /// <summary>Alias of the currently active account (null = none).</summary>
    public string? Active { get; set; }

    /// <summary>All saved accounts, keyed by user-chosen alias (usually the player name).</summary>
    public Dictionary<string, AuthTokens> Accounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
