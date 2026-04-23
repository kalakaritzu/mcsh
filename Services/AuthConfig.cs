namespace McSH.Services;

/// <summary>
/// Microsoft Azure AD application settings required for authentication.
///
/// To obtain a Client ID:
///   1. Go to https://portal.azure.com  →  Azure Active Directory  →  App registrations
///   2. New registration — choose "Accounts in any organizational directory and personal Microsoft accounts"
///   3. Under Authentication, add a Mobile/Desktop platform with redirect URI:
///      https://login.microsoftonline.com/common/oauth2/nativeclient
///   4. Enable "Allow public client flows" (required for Device Code Flow)
///   5. Copy the Application (client) ID and paste it below.
/// </summary>
public static class AuthConfig
{
    /// <summary>
    /// OAuth Client ID used for the Microsoft device-code flow.
    ///
    /// Note: Minecraft Services may reject newly-created app registrations with:
    /// "Invalid app registration" (HTTP 403). In that case you must request approval:
    /// https://aka.ms/mce-reviewappid
    ///
    /// You can override this value by setting the environment variable:
    ///   MCSH_CLIENT_ID
    /// </summary>
    public static string ClientId { get; set; } =
        Environment.GetEnvironmentVariable("MCSH_CLIENT_ID")
        ?? "faaa29dc-b46f-4e89-bf35-31d04df56c5c";
}

