using McSH;
using McSH.Services;
using Spectre.Console;

namespace McSH.Commands;

public class SettingsCommand
{
    private static string L(string key) => LanguageService.Get(key);

    public void Execute(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            if (AnsiConsole.Confirm($"[yellow]{L("settings.reset_confirm")}[/]"))
            {
                SettingsService.ResetToDefaults();
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("settings.reset_done")}[/]");
            }
            return;
        }

        if (args.Length > 0 && args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            Print();
            return;
        }

        if (args.Length > 0 && args[0].Equals("theme", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1) SetTheme(args[1]);
            else PickTheme();
            return;
        }

        if (args.Length > 0 && args[0].Equals("language", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1) SetLanguage(args[1]);
            else PickLanguage();
            return;
        }

        if (args.Length > 0 && args[0].Equals("recent", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1 && int.TryParse(args[1], out var count) && count >= 0)
            {
                SettingsService.Current.ShowRecentOnStartup = count > 0;
                SettingsService.Current.RecentOnStartupCount = count;
                SettingsService.Save();
                var msg = count == 0
                    ? $"{L("settings.recent_on_startup")}: {L("settings.off")}"
                    : $"{L("settings.recent_on_startup")}: {count} {L("settings.entries")}";
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{msg}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Usage:[/] settings recent [grey]<count>[/]  [dim](0 = off)[/]");
            }
            return;
        }

        InteractiveMenu();
    }

    private static void InteractiveMenu()
    {
        while (true)
        {
            var s = SettingsService.Current;

            var on  = "[green]On[/]";
            var off = "[grey]Off[/]";

            var recentLabel = s.ShowRecentOnStartup
                ? $"[green]{s.RecentOnStartupCount} {L("settings.entries")}[/]"
                : $"[grey]{L("settings.off")}[/]";

            var langLabel = s.Language.ToUpperInvariant();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]{L("settings.title")}[/]")
                    .PageSize(20)
                    .AddChoices(
                        $"[bold]{L("settings.appearance")}[/]",
                        $"  {L("settings.theme")}: [{UiTheme.AccentMarkup}]{UiTheme.Themes[UiTheme.ActiveTheme].Label}[/]",
                        $"  {L("settings.language")}: [{UiTheme.AccentMarkup}]{langLabel}[/]",
                        "",
                        $"[bold]{L("settings.input")}[/]",
                        $"  {L("settings.auto_complete")}: {(s.AutoCompleteEnabled ? on : off)}",
                        $"  {L("settings.clear_screen")}: {(s.ClearScreenOnCommand ? on : off)}",
                        "",
                        $"[bold]{L("settings.ui")}[/]",
                        $"  {L("settings.enhanced_prompt")}: {(s.UiEnhancedPrompt ? on : off)}",
                        $"  {L("settings.command_framing")}: {(s.UiCommandFraming ? on : off)}",
                        $"  {L("settings.panels")}: {(s.UiPanels ? on : off)}",
                        $"  {L("settings.download_progress")}: {(s.UiDownloadProgress ? on : off)}",
                        "",
                        $"[bold]{L("settings.prompt_section")}[/]",
                        $"  {L("settings.show_active_instance")}: {(s.PromptShowActiveInstance ? on : off)}",
                        $"  {L("settings.show_player_name")}: {(s.PromptShowPlayerName ? on : off)}",
                        "",
                        $"[bold]{L("settings.safety")}[/]",
                        $"  {L("settings.confirm_before_kill")}: {(s.ConfirmBeforeKill ? on : off)}",
                        "",
                        $"[bold]{L("settings.console_section")}[/]",
                        $"  {L("settings.show_timestamps")}: {(s.ConsoleShowTimestamps ? on : off)}",
                        $"  {L("settings.highlight_warnings")}: {(s.ConsoleHighlightWarnings ? on : off)}",
                        "",
                        $"[bold]{L("settings.general")}[/]",
                        $"  {L("settings.show_banner")}: {(s.ShowBannerOnStartup ? on : off)}",
                        $"  {L("settings.auto_backup")}: {(s.AutoBackupBeforeLaunch ? on : off)}",
                        $"  {L("settings.recent_on_startup")}: {recentLabel}",
                        $"  {L("settings.verbose_errors")}: {(s.VerboseErrors ? on : off)}",
                        "",
                        $"[bold]{L("settings.app_section")}[/]",
                        $"  {L("settings.max_downloads")}: [{UiTheme.AccentMarkup}]{s.MaxConcurrentDownloads}[/]",
                        $"  {L("settings.max_writes")}: [{UiTheme.AccentMarkup}]{s.MaxConcurrentWrites}[/]",
                        $"  {L("settings.purge_cache")}",
                        "",
                        L("settings.show_raw"),
                        L("settings.reset_to_defaults"),
                        L("settings.back")
                    ));

            var back = L("settings.back");
            if (choice == back) return;
            if (choice.StartsWith("[bold]", StringComparison.Ordinal) || choice.Length == 0) continue;

            // ── Appearance ────────────────────────────────────────────────────
            if (choice.Contains(L("settings.theme") + ":", StringComparison.Ordinal))
            {
                PickTheme();
                continue;
            }
            if (choice.Contains(L("settings.language") + ":", StringComparison.Ordinal))
            {
                PickLanguage();
                continue;
            }

            // ── Input ─────────────────────────────────────────────────────────
            if (choice.Contains(L("settings.auto_complete"), StringComparison.Ordinal))
                s.AutoCompleteEnabled = !s.AutoCompleteEnabled;
            else if (choice.Contains(L("settings.clear_screen"), StringComparison.Ordinal))
                s.ClearScreenOnCommand = !s.ClearScreenOnCommand;

            // ── UI ────────────────────────────────────────────────────────────
            else if (choice.Contains(L("settings.enhanced_prompt"), StringComparison.Ordinal))
                s.UiEnhancedPrompt = !s.UiEnhancedPrompt;
            else if (choice.Contains(L("settings.command_framing"), StringComparison.Ordinal))
                s.UiCommandFraming = !s.UiCommandFraming;
            else if (choice.Contains(L("settings.panels"), StringComparison.Ordinal))
                s.UiPanels = !s.UiPanels;
            else if (choice.Contains(L("settings.download_progress"), StringComparison.Ordinal))
                s.UiDownloadProgress = !s.UiDownloadProgress;

            // ── Prompt ────────────────────────────────────────────────────────
            else if (choice.Contains(L("settings.show_active_instance"), StringComparison.Ordinal))
                s.PromptShowActiveInstance = !s.PromptShowActiveInstance;
            else if (choice.Contains(L("settings.show_player_name"), StringComparison.Ordinal))
                s.PromptShowPlayerName = !s.PromptShowPlayerName;

            // ── Safety ────────────────────────────────────────────────────────
            else if (choice.Contains(L("settings.confirm_before_kill"), StringComparison.Ordinal))
                s.ConfirmBeforeKill = !s.ConfirmBeforeKill;

            // ── Console ───────────────────────────────────────────────────────
            else if (choice.Contains(L("settings.show_timestamps"), StringComparison.Ordinal))
                s.ConsoleShowTimestamps = !s.ConsoleShowTimestamps;
            else if (choice.Contains(L("settings.highlight_warnings"), StringComparison.Ordinal))
                s.ConsoleHighlightWarnings = !s.ConsoleHighlightWarnings;

            // ── General ───────────────────────────────────────────────────────
            else if (choice.Contains(L("settings.show_banner"), StringComparison.Ordinal))
                s.ShowBannerOnStartup = !s.ShowBannerOnStartup;
            else if (choice.Contains(L("settings.auto_backup"), StringComparison.Ordinal))
                s.AutoBackupBeforeLaunch = !s.AutoBackupBeforeLaunch;
            else if (choice.Contains(L("settings.recent_on_startup"), StringComparison.Ordinal))
            {
                var input = AnsiConsole.Ask<string>($"  {L("settings.recent_count_prompt")}");
                if (int.TryParse(input, out var n) && n >= 0)
                {
                    s.RecentOnStartupCount = n;
                    s.ShowRecentOnStartup  = n > 0;
                }
                SettingsService.Save();
                continue;
            }
            else if (choice.Contains(L("settings.verbose_errors"), StringComparison.Ordinal))
                s.VerboseErrors = !s.VerboseErrors;

            // ── App ───────────────────────────────────────────────────────────
            else if (choice.Contains(L("settings.max_downloads"), StringComparison.Ordinal))
            {
                var input = AnsiConsole.Ask<string>($"  {L("settings.max_downloads_prompt")}");
                if (int.TryParse(input, out var n) && n >= 1 && n <= 32)
                    s.MaxConcurrentDownloads = n;
                SettingsService.Save();
                continue;
            }
            else if (choice.Contains(L("settings.max_writes"), StringComparison.Ordinal))
            {
                var input = AnsiConsole.Ask<string>($"  {L("settings.max_writes_prompt")}");
                if (int.TryParse(input, out var n) && n >= 1 && n <= 32)
                    s.MaxConcurrentWrites = n;
                SettingsService.Save();
                continue;
            }
            else if (choice.Contains(L("settings.purge_cache"), StringComparison.Ordinal))
            {
                PurgeCache();
                continue;
            }

            // ── Bottom actions ────────────────────────────────────────────────
            else if (choice == L("settings.show_raw"))
            {
                Print();
                continue;
            }
            else if (choice == L("settings.reset_to_defaults"))
            {
                if (AnsiConsole.Confirm($"[yellow]{L("settings.reset_confirm")}[/]"))
                    SettingsService.ResetToDefaults();
                continue;
            }

            SettingsService.Save();
        }
    }

    // ── Language ──────────────────────────────────────────────────────────────

    private static void PickLanguage()
    {
        var languages = new Dictionary<string, string>
        {
            ["en"] = "English",
            ["es"] = "Español",
        };

        var current = SettingsService.Current.Language;
        var choices = languages
            .Select(kv => $"[{UiTheme.AccentMarkup}]{kv.Value}[/]{(kv.Key == current ? " [dim](active)[/]" : "")}")
            .Append("Cancel")
            .ToList();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]{L("settings.language")}[/]")
                .AddChoices(choices));

        if (choice == "Cancel") return;

        var selected = languages.FirstOrDefault(kv => choice.Contains(kv.Value));
        if (selected.Key is not null)
            SetLanguage(selected.Key);
    }

    private static void SetLanguage(string code)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "es" };
        if (!valid.Contains(code))
        {
            AnsiConsole.MarkupLine($"[red]{L("settings.language_invalid")}[/]");
            return;
        }

        SettingsService.Current.Language = code.ToLowerInvariant();
        SettingsService.Save();
        LanguageService.Load(code);
        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("settings.language_set")} {code.ToUpperInvariant()}.[/]");
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private static void PickTheme()
    {
        var choices = UiTheme.Themes.Values
            .Select(t => $"[{t.AccentMarkup}]{t.Label}[/]{(t.Name == UiTheme.ActiveTheme ? " [dim](active)[/]" : "")}")
            .Append("Cancel")
            .ToList();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]{L("settings.theme")}[/]")
                .AddChoices(choices));

        if (choice == "Cancel") return;

        var name = UiTheme.Themes.Values.FirstOrDefault(t => choice.Contains(t.Label))?.Name;
        if (name is not null) SetTheme(name);
    }

    private static void SetTheme(string name)
    {
        if (!UiTheme.Themes.ContainsKey(name))
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown theme.[/] Available: {string.Join(", ", UiTheme.Themes.Keys)}");
            return;
        }

        SettingsService.Current.Theme = name;
        SettingsService.Save();
        UiTheme.Apply(name);
        AnsiConsole.MarkupLine($"Theme set to [{UiTheme.AccentMarkup}]{UiTheme.Themes[name].Label}[/].");
    }

    // ── Cache purge ───────────────────────────────────────────────────────────

    private static void PurgeCache()
    {
        long freed = 0;
        var dirs = new[]
        {
            PathService.AssetObjectDir,
            PathService.AssetIndexDir,
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        freed += new FileInfo(f).Length;
                        File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Also purge the Java version cache file
        if (File.Exists(PathService.JavaCachePath))
        {
            try { File.Delete(PathService.JavaCachePath); } catch { }
        }

        var mb = freed / 1_048_576.0;
        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("settings.cache_purged")}[/] [dim]({mb:0.#} MB freed)[/]");
    }

    // ── Print raw ─────────────────────────────────────────────────────────────

    private static void Print()
    {
        var s = SettingsService.Current;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Key[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Language",                s.Language.ToUpperInvariant());
        table.AddRow("Theme",                   $"[{UiTheme.AccentMarkup}]{s.Theme}[/]");
        table.AddRow("AutoCompleteEnabled",      s.AutoCompleteEnabled      ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("ClearScreenOnCommand",     s.ClearScreenOnCommand     ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("ShowBannerOnStartup",      s.ShowBannerOnStartup      ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("VerboseErrors",            s.VerboseErrors            ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("PromptShowActiveInstance", s.PromptShowActiveInstance  ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("PromptShowPlayerName",     s.PromptShowPlayerName     ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("ConfirmBeforeKill",        s.ConfirmBeforeKill        ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("ConsoleShowTimestamps",    s.ConsoleShowTimestamps    ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("ConsoleHighlightWarnings", s.ConsoleHighlightWarnings ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("UiEnhancedPrompt",         s.UiEnhancedPrompt         ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("UiCommandFraming",         s.UiCommandFraming         ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("UiPanels",                 s.UiPanels                 ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("UiDownloadProgress",       s.UiDownloadProgress       ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("AutoBackupBeforeLaunch",   s.AutoBackupBeforeLaunch   ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("ShowRecentOnStartup",      s.ShowRecentOnStartup      ? "[green]true[/]" : "[grey]false[/]");
        table.AddRow("RecentOnStartupCount",     s.RecentOnStartupCount.ToString());
        table.AddRow("MaxConcurrentDownloads",   s.MaxConcurrentDownloads.ToString());
        table.AddRow("MaxConcurrentWrites",      s.MaxConcurrentWrites.ToString());

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]Tip:[/] `settings reset` to restore defaults.");
    }
}
