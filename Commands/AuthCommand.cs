using McSH;
using McSH.Services;
using Spectre.Console;

namespace McSH.Commands;

public class AuthCommand
{
    private readonly AuthService _auth;

    public AuthCommand(AuthService auth) => _auth = auth;

    public async Task ExecuteAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

        switch (sub)
        {
            // ── Login ──────────────────────────────────────────────────────────
            case "login" or "in":
            {
                // Optional alias: "auth login <alias>" saves the account under that name
                var alias = args.Length > 1 ? string.Join(' ', args[1..]) : null;
                await _auth.LoginAsync(alias);
                break;
            }

            // ── Logout (active account) ────────────────────────────────────────
            case "logout" or "out":
                _auth.Logout();
                break;

            // ── Status / whoami ────────────────────────────────────────────────
            case "status" or "s" or "whoami":
                _auth.PrintStatus();
                break;

            // ── List all saved accounts ────────────────────────────────────────
            case "accounts" or "list" or "ls":
                _auth.ListAccounts();
                break;

            // ── Switch active account ──────────────────────────────────────────
            case "switch" or "use":
                if (args.Length < 2)
                    AnsiConsole.MarkupLine(
                        $"[{UiTheme.AccentMarkup}]Usage:[/] auth switch [grey]<alias>[/]");
                else
                    _auth.SwitchAccount(string.Join(' ', args[1..]));
                break;

            // ── Remove a saved account ─────────────────────────────────────────
            case "remove" or "rm" or "delete":
                if (args.Length < 2)
                    AnsiConsole.MarkupLine(
                        $"[{UiTheme.AccentMarkup}]Usage:[/] auth remove [grey]<alias>[/]");
                else
                    _auth.RemoveAccount(string.Join(' ', args[1..]));
                break;

            default:
                AnsiConsole.MarkupLine(
                    $"[{UiTheme.AccentMarkup}]Unknown auth subcommand:[/] {Markup.Escape(sub)}");
                AnsiConsole.MarkupLine(
                    "[dim]Usage: auth login [alias] | auth logout | auth status | " +
                    "auth accounts | auth switch <alias> | auth remove <alias>[/]");
                break;
        }
    }
}
