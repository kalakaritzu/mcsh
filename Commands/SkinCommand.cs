using McSH.Services;
using Spectre.Console;

namespace McSH.Commands;

public class SkinCommand
{
    private readonly SkinService _skins = new();
    private readonly AuthService _auth;

    private static string L(string key) => LanguageService.Get(key);

    public SkinCommand(AuthService auth) => _auth = auth;

    public async Task ExecuteAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        switch (sub)
        {
            case "import":
                Import(args[1..]);
                break;
            case "delete":
            case "remove":
            case "rm":
                Delete(args[1..]);
                break;
            case "cape":
                await CapeAsync();
                break;
            default:
                await SkinMenuAsync();
                break;
        }
    }

    // ── Skin menu ─────────────────────────────────────────────────────────────

    private async Task SkinMenuAsync()
    {
        var all = _skins.GetAll();

        if (all.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("skin.none")}[/]");
        }
        else
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("[bold]#[/]").RightAligned().NoWrap())
                .AddColumn(new TableColumn($"[bold]{L("skin.col_name")}[/]").NoWrap())
                .AddColumn($"[bold]{L("skin.col_model")}[/]")
                .AddColumn($"[bold]{L("skin.col_imported")}[/]");

            for (int i = 0; i < all.Count; i++)
            {
                var s = all[i];
                table.AddRow(
                    $"[dim]{i + 1}[/]",
                    $"[{UiTheme.AccentMarkup}]{Markup.Escape(s.Name)}[/]",
                    Markup.Escape(s.Model),
                    s.ImportedAt.ToLocalTime().ToString("yyyy-MM-dd"));
            }

            AnsiConsole.Write(table);
        }

        if (Console.IsInputRedirected) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{L("skin.hint")}[/]");

        var input = AnsiConsole.Prompt(
            new TextPrompt<string>($"[{UiTheme.AccentMarkup}]{L("skin.prompt")}[/]")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(input)) return;

        if (int.TryParse(input, out var idx) && idx >= 1 && idx <= all.Count)
        {
            await ApplyAsync(all[idx - 1]);
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]{L("skin.invalid_selection")}[/]");
        }
    }

    // ── Apply skin ────────────────────────────────────────────────────────────

    private async Task ApplyAsync(Models.SkinEntry entry)
    {
        var token = _auth.MinecraftToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            AnsiConsole.MarkupLine($"[yellow]{L("skin.not_signed_in")}[/]");
            return;
        }

        var pngPath = Path.Combine(PathService.SkinsDir, entry.FileName);
        if (!File.Exists(pngPath))
        {
            AnsiConsole.MarkupLine($"[yellow]{L("skin.file_missing")}[/]");
            return;
        }

        bool ok = false;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("skin.applying"), async _ =>
            {
                ok = await _skins.UploadSkinAsync(token, pngPath, entry.Model);
            });

        if (ok)
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("skin.applied")}[/] {Markup.Escape(entry.Name)}");
        else
            AnsiConsole.MarkupLine($"[yellow]{L("skin.apply_failed")}[/]");
    }

    // ── Import ────────────────────────────────────────────────────────────────

    private void Import(string[] args)
    {
        string? path = args.Length > 0 ? args[0] : null;
        if (path is null || !File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[yellow]{L("skin.import_no_file")}[/]");
            return;
        }

        if (Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine($"[yellow]{L("skin.import_interactive")}[/]");
            return;
        }

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>($"[{UiTheme.AccentMarkup}]{L("skin.import_name_prompt")}[/]")
                .AllowEmpty());
        if (string.IsNullOrWhiteSpace(name)) { AnsiConsole.MarkupLine($"[dim]{L("skin.cancelled")}[/]"); return; }

        var modelLabel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[{UiTheme.AccentMarkup}]{L("skin.import_model_prompt")}[/]")
                .AddChoices("classic", "slim"));

        var (entry, error) = _skins.Import(path, name, modelLabel);
        if (error is not null)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(error)}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("skin.imported")}[/] {Markup.Escape(entry!.Name)}  [dim]({Markup.Escape(entry.Model)})[/]");
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    private void Delete(string[] args)
    {
        var all = _skins.GetAll();
        if (all.Count == 0) { AnsiConsole.MarkupLine($"[dim]{L("skin.none")}[/]"); return; }

        Models.SkinEntry? target = null;

        if (args.Length > 0)
        {
            var nameArg = string.Join(" ", args);
            target = all.FirstOrDefault(s => s.Name.Equals(nameArg, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                AnsiConsole.MarkupLine($"[yellow]{L("skin.not_found")}[/] {Markup.Escape(nameArg)}");
                return;
            }
        }
        else
        {
            if (Console.IsInputRedirected) return;
            target = AnsiConsole.Prompt(
                new SelectionPrompt<Models.SkinEntry>()
                    .Title($"[{UiTheme.AccentMarkup}]{L("skin.delete_select")}[/]")
                    .UseConverter(s => s.Name)
                    .AddChoices(all));
        }

        _skins.Delete(target);
        AnsiConsole.MarkupLine($"[dim]{L("skin.deleted")}[/] {Markup.Escape(target.Name)}");
    }

    // ── Cape ──────────────────────────────────────────────────────────────────

    private async Task CapeAsync()
    {
        var token = _auth.MinecraftToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            AnsiConsole.MarkupLine($"[yellow]{L("skin.not_signed_in")}[/]");
            return;
        }

        MojangProfile? profile = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("skin.cape_loading"), async _ =>
            {
                profile = await _skins.GetProfileAsync(token);
            });

        if (profile is null)
        {
            AnsiConsole.MarkupLine($"[yellow]{L("skin.cape_failed")}[/]");
            return;
        }

        if (profile.Capes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("skin.cape_none")}[/]");
            return;
        }

        if (Console.IsInputRedirected) return;

        var activeCape = profile.Capes.FirstOrDefault(c => c.State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase));

        var noneLabel = L("skin.cape_disable");
        var choices   = profile.Capes
            .Select(c =>
            {
                var label = string.IsNullOrWhiteSpace(c.Alias) ? c.Id : c.Alias;
                if (c.State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
                    label += $"  [{UiTheme.AccentMarkup}]← active[/]";
                return (Label: label, Cape: c);
            })
            .ToList();

        choices.Add((Label: noneLabel, Cape: null!));

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<(string Label, MojangCape Cape)>()
                .Title($"[{UiTheme.AccentMarkup}]{L("skin.cape_select")}[/]")
                .UseConverter(x => x.Label)
                .AddChoices(choices));

        bool isNone = selection.Label == noneLabel;
        string? capeId = isNone ? null : selection.Cape.Id;

        // No change if same cape already active
        if (!isNone && activeCape?.Id == capeId)
        {
            AnsiConsole.MarkupLine($"[dim]{L("skin.cape_no_change")}[/]");
            return;
        }

        bool ok = false;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("skin.cape_applying"), async _ =>
            {
                ok = await _skins.SetActiveCapeAsync(token, capeId);
            });

        if (ok)
        {
            if (isNone)
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("skin.cape_disabled")}[/]");
            else
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("skin.cape_applied")}[/] {Markup.Escape(selection.Cape.Alias.Length > 0 ? selection.Cape.Alias : selection.Cape.Id)}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]{L("skin.cape_apply_failed")}[/]");
        }
    }
}
