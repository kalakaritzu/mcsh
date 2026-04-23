using McSH;
using McSH.Services;
using Spectre.Console;

namespace McSH.Commands;

public class JavaCommand
{
    private static string L(string key) => McSH.Services.LanguageService.Get(key);

    public async Task ExecuteAsync()
    {
        List<JavaInstall>? installs = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("java.scanning"), async _ =>
            {
                installs = await JavaService.FindAllAsync();
            });

        installs ??= [];

        var customPath = SettingsService.Current.CustomJavaPath;

        // ── Table ─────────────────────────────────────────────────────────────
        if (installs.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("java.none_found")}[/]");
        }
        else
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("[bold]#[/]").RightAligned().NoWrap())
                .AddColumn(new TableColumn($"[bold]{L("java.col_version")}[/]").NoWrap())
                .AddColumn($"[bold]{L("java.col_path")}[/]");

            for (int i = 0; i < installs.Count; i++)
            {
                var j = installs[i];

                var isActive = !string.IsNullOrWhiteSpace(customPath)
                    ? j.Path.Equals(customPath, StringComparison.OrdinalIgnoreCase)
                    : j.Tag == "PATH";

                var verCell  = $"[{UiTheme.AccentMarkup}]Java {j.MajorVersion}[/]";
                var pathCell = Markup.Escape(j.Path);
                if (j.Tag is not null)
                    pathCell += $"  [dim]({Markup.Escape(j.Tag)})[/]";
                if (isActive)
                    pathCell += $"  [{UiTheme.AccentMarkup}]← active[/]";

                table.AddRow($"[dim]{i + 1}[/]", verCell, pathCell);
            }

            AnsiConsole.Write(table);
        }

        // ── Custom path status ────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        if (!string.IsNullOrWhiteSpace(customPath))
            AnsiConsole.MarkupLine($"  {L("java.custom_path_label")} : [{UiTheme.AccentMarkup}]{Markup.Escape(customPath)}[/]");
        else
            AnsiConsole.MarkupLine($"  {L("java.custom_path_label")} : [dim]{L("java.custom_path_none")}[/]");

        if (Console.IsInputRedirected) return;

        // ── Input ─────────────────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{L("java.input_hint")}[/]");
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>($"[{UiTheme.AccentMarkup}]{L("java.set_prompt")}[/]")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(input)) return; // cancel

        if (input.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("none",  StringComparison.OrdinalIgnoreCase))
        {
            SettingsService.Current.CustomJavaPath = null;
            SettingsService.Save();
            AnsiConsole.MarkupLine($"[dim]{L("java.path_cleared")}[/]");
            return;
        }

        // Resolve: number → path from table, or treat as a direct path
        string? resolved = null;
        if (int.TryParse(input, out var idx) && idx >= 1 && idx <= installs.Count)
            resolved = installs[idx - 1].Path;
        else if (File.Exists(input))
            resolved = input;

        if (resolved is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("java.file_not_found")}[/] {Markup.Escape(input)}");
            return;
        }

        // Verify it actually runs java
        var version = await JavaService.GetMajorVersionAsync(resolved);
        if (version <= 0)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("java.verify_failed")}[/] {Markup.Escape(resolved)}");
            return;
        }

        SettingsService.Current.CustomJavaPath = resolved;
        SettingsService.Save();
        AnsiConsole.MarkupLine(
            $"[dim]{L("java.custom_set")}[/] [{UiTheme.AccentMarkup}]{Markup.Escape(resolved)}[/] [dim](Java {version})[/]");
    }
}
