using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McSH;
using McSH.Models;
using Spectre.Console;

namespace McSH.Services;

/// <summary>
/// Handles the full Microsoft → Xbox Live → XSTS → Minecraft authentication chain.
/// Supports multiple saved accounts; tokens are persisted to accounts.json.
/// Legacy auth.json files are migrated automatically on first load.
/// </summary>
public class AuthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    static AuthService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.1.0");

    private AccountStore _store = new();

    // ── Active-account shortcuts ───────────────────────────────────────────────

    private AuthTokens? ActiveTokens =>
        _store.Active is not null && _store.Accounts.TryGetValue(_store.Active, out var t) ? t : null;

    public bool IsAuthenticated  => ActiveTokens?.IsMinecraftTokenValid ?? false;
    public string? PlayerName    => ActiveTokens?.PlayerName;
    public string? PlayerUuid    => ActiveTokens?.PlayerUuid;
    public string? MinecraftToken => ActiveTokens?.MinecraftAccessToken;

    // ── Disk preload (for banner) ─────────────────────────────────────────────

    /// <summary>Reads cached tokens synchronously so the banner can show the player name.</summary>
    public void TryLoadFromDisk() => _store = LoadStore();

    // ── Login (Device Code Flow) ───────────────────────────────────────────────

    /// <summary>
    /// Runs the full MS Device Code auth flow.
    /// <paramref name="preferredAlias"/> is the key the account is stored under;
    /// if null, the player's Minecraft name is used.
    /// </summary>
    public async Task LoginAsync(string? preferredAlias = null)
    {
        if (AuthConfig.ClientId == "YOUR_AZURE_APP_CLIENT_ID")
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Microsoft Client ID not configured.[/]");
            AnsiConsole.MarkupLine("[dim]See Services/AuthConfig.cs for registration instructions.[/]");
            return;
        }

        // Step 1 — request device code
        DeviceCodeResponse dcr;
        try
        {
            dcr = await RequestDeviceCodeAsync();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to start authentication:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Open [{UiTheme.AccentMarkup}]{Markup.Escape(dcr.VerificationUri)}[/] and enter this code:");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [bold {UiTheme.AccentStrongMarkup}]{dcr.UserCode}[/]");
        AnsiConsole.WriteLine();

        // Step 2 — poll until user completes sign-in
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = null;
        handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;

        TokenResponse? tr = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync("Waiting for sign-in...", async _ =>
            {
                tr = await PollForTokenAsync(dcr.DeviceCode, dcr.Interval, dcr.ExpiresIn, cts.Token);
            });

        Console.CancelKeyPress -= handler;

        if (tr is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Authentication timed out or was cancelled.[/]");
            return;
        }

        // Steps 3–6 — Xbox → XSTS → Minecraft → profile
        bool success = false;
        AuthTokens? newTokens = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync("Completing authentication...", async ctx =>
            {
                try
                {
                    ctx.Status("Authenticating with Xbox Live...");
                    var (xblToken, uhs) = await AuthXboxLiveAsync(tr.AccessToken);

                    ctx.Status("Obtaining XSTS token...");
                    var xstsToken = await AuthXstsAsync(xblToken);

                    ctx.Status("Authenticating with Minecraft...");
                    var (mcToken, mcExpiry) = await AuthMinecraftAsync(uhs, xstsToken);

                    ctx.Status("Fetching profile...");
                    var (uuid, name) = await FetchProfileAsync(mcToken);

                    newTokens = new AuthTokens
                    {
                        MicrosoftRefreshToken = tr.RefreshToken,
                        MinecraftAccessToken  = mcToken,
                        MinecraftTokenExpiry  = DateTime.UtcNow.AddSeconds(mcExpiry),
                        PlayerUuid            = uuid,
                        PlayerName            = name
                    };

                    success = true;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Authentication failed:[/] {Markup.Escape(ex.Message)}");
                }
            });

        if (!success || newTokens is null) return;

        // Alias: explicit arg > player name
        var alias = !string.IsNullOrWhiteSpace(preferredAlias)
            ? preferredAlias.Trim()
            : newTokens.PlayerName;

        _store.Accounts[alias] = newTokens;
        _store.Active = alias;
        SaveStore(_store);

        UiService.Success("Signed in",
            $"As [{UiTheme.AccentMarkup}]{Markup.Escape(newTokens.PlayerName)}[/]" +
            (!alias.Equals(newTokens.PlayerName, StringComparison.OrdinalIgnoreCase)
                ? $"  [dim](alias: {Markup.Escape(alias)})[/]"
                : ""));

        if (_store.Accounts.Count > 1)
            AnsiConsole.MarkupLine(
                $"[dim]{_store.Accounts.Count} accounts saved. " +
                "Use 'auth accounts' to list all, 'auth switch <alias>' to change.[/]");
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    /// <summary>Signs out of the currently active account and removes it from the store.</summary>
    public void Logout()
    {
        var alias = _store.Active;
        if (alias is not null) _store.Accounts.Remove(alias);

        _store.Active = _store.Accounts.Keys.FirstOrDefault();

        if (_store.Accounts.Count == 0)
        {
            // Wipe file entirely; also clean up any legacy auth.json
            var path = PathService.AccountsPath;
            if (File.Exists(path)) File.Delete(path);
            var legacy = PathService.AuthPath;
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        else
        {
            SaveStore(_store);
        }

        AnsiConsole.MarkupLine(alias is not null
            ? $"[dim]Signed out of {Markup.Escape(alias)}.[/]"
            : "[dim]Signed out.[/]");

        if (_store.Active is not null)
            AnsiConsole.MarkupLine(
                $"[dim]Active account is now [{UiTheme.AccentMarkup}]{Markup.Escape(_store.Active)}[/][dim].[/]");
        else
            AnsiConsole.MarkupLine("[dim]Run 'auth login' to sign in again.[/]");
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public void PrintStatus()
    {
        if (_store.Accounts.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]Not signed in.[/]  Run [{UiTheme.AccentMarkup}]auth login[/] to authenticate.");
            return;
        }

        var active = ActiveTokens;
        if (active is null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]No active account.[/]  " +
                $"Run [{UiTheme.AccentMarkup}]auth switch <alias>[/] to select one.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"Signed in as [{UiTheme.AccentMarkup}]{Markup.Escape(active.PlayerName)}[/]" +
            (!(_store.Active ?? "").Equals(active.PlayerName, StringComparison.OrdinalIgnoreCase)
                ? $"  [dim](alias: {Markup.Escape(_store.Active ?? "")})[/]"
                : ""));

        AnsiConsole.MarkupLine($"[dim]UUID: {active.PlayerUuid}[/]");

        if (active.MinecraftTokenExpiry != default)
            AnsiConsole.MarkupLine(
                $"[dim]Token expires: {active.MinecraftTokenExpiry.ToLocalTime():yyyy-MM-dd HH:mm}[/]");

        if (_store.Accounts.Count > 1)
            AnsiConsole.MarkupLine(
                $"[dim]{_store.Accounts.Count} accounts saved — run 'auth accounts' to list all.[/]");
    }

    // ── Accounts list ─────────────────────────────────────────────────────────

    public void ListAccounts()
    {
        if (_store.Accounts.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No accounts saved. Run 'auth login' to add one.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Alias[/]").NoWrap())
            .AddColumn("[bold]Player[/]")
            .AddColumn("[bold]Token[/]");

        foreach (var (alias, tokens) in _store.Accounts)
        {
            var isActive = string.Equals(alias, _store.Active, StringComparison.OrdinalIgnoreCase);
            var marker   = isActive ? $"[{UiTheme.AccentMarkup}]>[/] " : "  ";
            var validity = tokens.IsMinecraftTokenValid ? "[dim]valid[/]" : "[dim]expired[/]";

            table.AddRow(
                $"{marker}[{UiTheme.AccentMarkup}]{Markup.Escape(alias)}[/]",
                Markup.Escape(tokens.PlayerName),
                validity);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[dim]Switch with [{UiTheme.AccentMarkup}]auth switch <alias>[/][dim]. " +
            "Add another with 'auth login <alias>'.[/]");
    }

    // ── Switch account ────────────────────────────────────────────────────────

    public bool SwitchAccount(string alias)
    {
        // Case-insensitive search
        var key = _store.Accounts.Keys
            .FirstOrDefault(k => k.Equals(alias, StringComparison.OrdinalIgnoreCase));

        if (key is null)
        {
            AnsiConsole.MarkupLine(
                $"[{UiTheme.AccentMarkup}]No account named '{Markup.Escape(alias)}'.[/]  " +
                "Run 'auth accounts' to see all saved accounts.");
            return false;
        }

        _store.Active = key;
        SaveStore(_store);

        var playerName = _store.Accounts[key].PlayerName;
        UiService.Success("Switched",
            $"Now signed in as [{UiTheme.AccentMarkup}]{Markup.Escape(playerName)}[/].");
        return true;
    }

    // ── Remove account ────────────────────────────────────────────────────────

    public bool RemoveAccount(string alias)
    {
        var key = _store.Accounts.Keys
            .FirstOrDefault(k => k.Equals(alias, StringComparison.OrdinalIgnoreCase));

        if (key is null)
        {
            AnsiConsole.MarkupLine(
                $"[{UiTheme.AccentMarkup}]No account named '{Markup.Escape(alias)}'.[/]");
            return false;
        }

        _store.Accounts.Remove(key);

        if (string.Equals(_store.Active, key, StringComparison.OrdinalIgnoreCase))
            _store.Active = _store.Accounts.Keys.FirstOrDefault();

        if (_store.Accounts.Count == 0)
        {
            var path = PathService.AccountsPath;
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            SaveStore(_store);
        }

        AnsiConsole.MarkupLine($"[dim]Removed account '{Markup.Escape(key)}'.[/]");

        if (_store.Active is not null)
            AnsiConsole.MarkupLine(
                $"[dim]Active account is now '{Markup.Escape(_store.Active)}'.[/]");
        else if (_store.Accounts.Count == 0)
            AnsiConsole.MarkupLine("[dim]No accounts remain. Run 'auth login' to add one.[/]");

        return true;
    }

    // ── Background refresh ────────────────────────────────────────────────────

    /// <summary>
    /// Called once at startup. Silently refreshes the active account's tokens if expired.
    /// Never throws — failures are swallowed so startup is never blocked.
    /// </summary>
    public async Task TryRefreshInBackgroundAsync()
    {
        _store = LoadStore();

        var active = ActiveTokens;
        if (active is null) return;
        if (active.IsMinecraftTokenValid) return;
        if (string.IsNullOrEmpty(active.MicrosoftRefreshToken)) return;

        try
        {
            var tr = await RefreshMicrosoftTokenAsync(active.MicrosoftRefreshToken);
            if (tr is null) return;

            var (xblToken, uhs)     = await AuthXboxLiveAsync(tr.AccessToken);
            var xstsToken           = await AuthXstsAsync(xblToken);
            var (mcToken, mcExpiry) = await AuthMinecraftAsync(uhs, xstsToken);
            var (uuid, name)        = await FetchProfileAsync(mcToken);

            var refreshed = new AuthTokens
            {
                MicrosoftRefreshToken = tr.RefreshToken,
                MinecraftAccessToken  = mcToken,
                MinecraftTokenExpiry  = DateTime.UtcNow.AddSeconds(mcExpiry),
                PlayerUuid            = uuid,
                PlayerName            = name
            };

            _store.Accounts[_store.Active!] = refreshed;
            SaveStore(_store);
        }
        catch
        {
            // user will need to run 'auth login' again
        }
    }

    // ── Token persistence ─────────────────────────────────────────────────────

    private static AccountStore LoadStore()
    {
        // Try new accounts.json first
        var accountsPath = PathService.AccountsPath;
        if (File.Exists(accountsPath))
        {
            try
            {
                var store = JsonSerializer.Deserialize<AccountStore>(
                    File.ReadAllText(accountsPath), Opts);
                if (store is not null) return store;
            }
            catch { }
        }

        // Migrate from legacy auth.json (single-account)
        var legacyPath = PathService.AuthPath;
        if (File.Exists(legacyPath))
        {
            try
            {
                var tokens = JsonSerializer.Deserialize<AuthTokens>(
                    File.ReadAllText(legacyPath), Opts);

                if (tokens is not null)
                {
                    var alias = !string.IsNullOrWhiteSpace(tokens.PlayerName)
                        ? tokens.PlayerName
                        : "default";

                    var migrated = new AccountStore
                    {
                        Active   = alias,
                        Accounts = new Dictionary<string, AuthTokens>(StringComparer.OrdinalIgnoreCase)
                            { [alias] = tokens }
                    };

                    // Persist to new format and remove old file
                    SaveStore(migrated);
                    try { File.Delete(legacyPath); } catch { }
                    return migrated;
                }
            }
            catch { }
        }

        return new AccountStore();
    }

    private static void SaveStore(AccountStore store)
    {
        Directory.CreateDirectory(PathService.RootDir);
        File.WriteAllText(PathService.AccountsPath, JsonSerializer.Serialize(store, Opts));
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private static async Task<DeviceCodeResponse> RequestDeviceCodeAsync()
    {
        var resp = await Http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = AuthConfig.ClientId,
                ["scope"]     = "XboxLive.SignIn offline_access"
            }));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<DeviceCodeResponse>())!;
    }

    private static async Task<TokenResponse?> PollForTokenAsync(
        string deviceCode, int interval, int expiresIn, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
        var body = new Dictionary<string, string>
        {
            ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"]   = AuthConfig.ClientId,
            ["device_code"] = deviceCode
        };

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            var resp = await Http.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(body),
                ct);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("access_token", out var at))
                return new TokenResponse(
                    at.GetString()!,
                    doc.RootElement.GetProperty("refresh_token").GetString()!,
                    doc.RootElement.GetProperty("expires_in").GetInt32());

            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.GetString() != "authorization_pending")
                return null; // denied or expired
        }

        return null; // timed out
    }

    private static async Task<TokenResponse?> RefreshMicrosoftTokenAsync(string refreshToken)
    {
        var resp = await Http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = AuthConfig.ClientId,
                ["refresh_token"] = refreshToken,
                ["scope"]         = "XboxLive.SignIn offline_access"
            }));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<TokenResponse>();
    }

    private static async Task<(string token, string uhs)> AuthXboxLiveAsync(string msAccessToken)
    {
        var resp = await Http.PostAsync(
            "https://user.auth.xboxlive.com/user/authenticate",
            MakeJson(new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName   = "user.auth.xboxlive.com",
                    RpsTicket  = $"d={msAccessToken}"
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType    = "JWT"
            }));

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Exception($"Xbox Live auth failed {(int)resp.StatusCode}: {body}");
        }

        var doc   = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("Token").GetString()!;
        var xuiArr = doc.RootElement.GetProperty("DisplayClaims").GetProperty("xui");
        if (xuiArr.GetArrayLength() == 0)
            throw new Exception("Xbox Live response missing user hash (xui array was empty).");
        var uhs = xuiArr[0].GetProperty("uhs").GetString()!;
        return (token, uhs);
    }

    private static async Task<string> AuthXstsAsync(string xblToken)
    {
        var resp = await Http.PostAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            MakeJson(new
            {
                Properties = new
                {
                    SandboxId  = "RETAIL",
                    UserTokens = new[] { xblToken }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType    = "JWT"
            }));
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Exception($"XSTS auth failed {(int)resp.StatusCode}: {body}");
        }

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("Token").GetString()!;
    }

    private static async Task<(string token, int expiresIn)> AuthMinecraftAsync(
        string uhs, string xstsToken)
    {
        var resp = await Http.PostAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            MakeJson(new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" }));

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 403 &&
                body.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(
                    "Minecraft auth failed 403: Invalid app registration.\n" +
                    "This usually means your Azure app registration is not approved for Minecraft Services.\n" +
                    "Request access here: https://aka.ms/mce-reviewappid\n" +
                    "Then set your Client ID in Services/AuthConfig.cs or via env var MCSH_CLIENT_ID.\n\n" +
                    body);
            }

            throw new Exception($"Minecraft auth failed {(int)resp.StatusCode}: {body}");
        }

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("access_token").GetString()!,
            doc.RootElement.GetProperty("expires_in").GetInt32()
        );
    }

    private static async Task<(string uuid, string name)> FetchProfileAsync(string mcToken)
    {
        var req = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new("Bearer", mcToken);

        var resp = await Http.SendAsync(req);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Exception($"Profile fetch failed {(int)resp.StatusCode}: {body}");
        }

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("id").GetString()!,
            doc.RootElement.GetProperty("name").GetString()!
        );
    }

    private static StringContent MakeJson<T>(T obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    // ── Deserialization records ────────────────────────────────────────────────

    private record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")]      string DeviceCode,
        [property: JsonPropertyName("user_code")]        string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")]       int    ExpiresIn,
        [property: JsonPropertyName("interval")]         int    Interval);

    private record TokenResponse(
        [property: JsonPropertyName("access_token")]  string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")]    int    ExpiresIn);
}
