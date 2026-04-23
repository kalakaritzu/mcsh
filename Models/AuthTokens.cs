using System.Text.Json.Serialization;

namespace McSH.Models;

public class AuthTokens
{
    public string MicrosoftRefreshToken { get; set; } = string.Empty;
    public string MinecraftAccessToken  { get; set; } = string.Empty;
    public DateTime MinecraftTokenExpiry { get; set; }
    public string PlayerUuid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsMinecraftTokenValid =>
        !string.IsNullOrEmpty(MinecraftAccessToken) &&
        DateTime.UtcNow < MinecraftTokenExpiry;
}
