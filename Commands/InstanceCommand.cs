using McSH.Models;
using McSH;
using McSH.Services;
using McSH.State;
using System.Diagnostics;
using Spectre.Console;

namespace McSH.Commands;

public class InstanceCommand
{
    private readonly AppState _state;
    private readonly InstanceStore _store;
    private readonly MojangService _mojang;
    private readonly ProcessTracker _tracker;
    private readonly AuthService _auth;
    private readonly GameLaunchService _launcher;
    private readonly MrpackService _mrpack;
    private readonly ModStore _mods;
    private readonly ModrinthService _modrinth;

    private static string L(string key) => McSH.Services.LanguageService.Get(key);

    public InstanceCommand(
        AppState state,
        InstanceStore store,
        MojangService mojang,
        ProcessTracker tracker,
        AuthService auth,
        GameLaunchService launcher,
        MrpackService mrpack,
        ModrinthService modrinth)
    {
        _state    = state;
        _store    = store;
        _mojang   = mojang;
        _tracker  = tracker;
        _auth     = auth;
        _launcher = launcher;
        _mrpack   = mrpack;
        _mods     = new ModStore();
        _modrinth = modrinth;
    }

    public async Task ExecuteAsync(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return; }

        switch (args[0].ToLowerInvariant())
        {
            case "list" or "ls":
                ListInstances();
                break;

            case "create" or "new":
                await CreateWizardAsync();
                break;

            case "select" or "use":
                if (args.Length < 2)
                {
                    var all = _store.GetAll();
                    if (all.Count == 0) { AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instances found.[/]"); break; }
                    var pick = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title($"[{UiTheme.AccentMarkup}]Select instance:[/]")
                            .AddChoices(all.Select(i => i.Name).Append("(cancel)")));
                    if (pick != "(cancel)") SelectInstance(pick);
                }
                else SelectInstance(string.Join(' ', args[1..]));
                break;

            case "deselect":
                DeselectInstance();
                break;

            case "run":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null)
                {
                    var all = _store.GetAll();
                    if (all.Count == 0)
                    {
                        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instances found.[/] Use 'instance create' to add one.");
                        break;
                    }
                    target = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title($"[{UiTheme.AccentMarkup}]Which instance?[/]")
                            .AddChoices(all.Select(i => i.Name).Append("(cancel)")));
                    if (target == "(cancel)") break;
                }
                await RunInstanceAsync(target);
                break;
            }

            case "stop" or "kill":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance select <name>' first, or pass a name.");
                else KillInstance(target);
                break;
            }

            case "delete" or "rm" or "remove":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance delete <name>'.");
                else await DeleteInstanceAsync(target);
                break;
            }

            case "rename" or "mv":
            {
                var oldName = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (oldName is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance rename <name>'.");
                else
                {
                    var newName = AnsiConsole.Ask<string>($"[{UiTheme.AccentMarkup}]New name:[/]");
                    await RenameInstanceAsync(oldName, newName);
                }
                break;
            }

            case "clone" or "copy" or "duplicate":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance clone <name>'.");
                else await CloneInstanceAsync(target);
                break;
            }

            case "import":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance import [grey]<path.mrpack>[/]");
                else await ImportMrpackAsync(string.Join(' ', args[1..]));
                break;

            case "open":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance open <name>'.");
                else OpenInstanceFolder(target);
                break;
            }

            case "info":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance info <name>'.");
                else ShowInstanceInfo(target);
                break;
            }

            case "export":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance export [grey]<name>[/]");
                else await ExportInstanceAsync(target);
                break;
            }

            case "backup":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance backup [grey]<name>[/]");
                else await BackupInstanceAsync(target);
                break;
            }

            case "prism" or "multimc":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance prism [grey]<path>[/]");
                else await ImportPrismAsync(string.Join(' ', args[1..]));
                break;

            case "update":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null)
                    AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance select <name>' first, or pass a name.");
                else
                {
                    var inst = _store.Get(target);
                    if (inst is null)
                        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(target)}");
                    else
                        await UpdateModsAsync(target, inst);
                }
                break;
            }

            case "mrpack":
            {
                // Optional version ID argument: "instance mrpack [name] [versionId]"
                // With no args, use the active instance.
                var target    = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                var versionId = "1.0.0";

                // If the last arg looks like a semantic version, strip it from the name
                if (args.Length >= 3 &&
                    System.Text.RegularExpressions.Regex.IsMatch(args[^1], @"^\d+\.\d+"))
                {
                    target    = string.Join(' ', args[1..^1]);
                    versionId = args[^1];
                }

                if (target is null)
                    AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance mrpack [grey][[name]] [[version]][/]");
                else
                    await ExportMrpackAsync(target, versionId);
                break;
            }

            case "config" or "cfg":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance config [grey]<name>[/]");
                else InstanceConfigMenu(target);
                break;
            }

            case "crash":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance crash [grey][[name]][/]");
                else await CrashAsync(target);
                break;
            }

            case "worlds":
            {
                var target = args.Length >= 2 ? string.Join(' ', args[1..]) : _state.ActiveInstance;
                if (target is null) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] instance worlds [grey][[name]][/]");
                else await WorldsAsync(target);
                break;
            }

            default:
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Unknown subcommand:[/] instance {Markup.Escape(args[0])}");
                PrintUsage();
                break;
        }
    }

    // ── list ──────────────────────────────────────────────────────────────────

    private void ListInstances()
    {
        _tracker.Purge();

        var instances = _store.GetAll();

        if (instances.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("instance.no_instances")} {L("instance.use_create_hint")}[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn($"[bold]{L("instance.col_name")}[/]").NoWrap())
            .AddColumn($"[bold]{L("instance.col_version")}[/]")
            .AddColumn($"[bold]{L("instance.col_loader")}[/]")
            .AddColumn($"[bold]{L("instance.col_ram")}[/]")
            .AddColumn(new TableColumn($"[bold]{L("instance.col_mods")}[/]").RightAligned())
            .AddColumn($"[bold]{L("instance.col_status")}[/]")
            .AddColumn($"[bold]{L("instance.col_created")}[/]");

        // Running instances shown first, then alphabetical
        instances = [.. instances
            .OrderByDescending(i => _tracker.IsRunning(i.Name))
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

        foreach (var inst in instances)
        {
            var isActive = inst.Name == _state.ActiveInstance;
            var nameCell = isActive
                ? $"[{UiTheme.AccentMarkup}]{Markup.Escape(inst.Name)}[/] [dim](active)[/]"
                : Markup.Escape(inst.Name);

            var status   = _tracker.IsRunning(inst.Name) ? $"[green]{L("instance.status_running")}[/]" : $"[dim]{L("instance.status_stopped")}[/]";
            var allMods  = _mods.GetAll(inst.Name);
            var modCount = allMods.Count == 0
                ? "[dim]—[/]"
                : allMods.Count(m => m.Enabled) == allMods.Count
                    ? $"[dim]{allMods.Count}[/]"
                    : $"[dim]{allMods.Count(m => m.Enabled)}/{allMods.Count}[/]";

            table.AddRow(
                nameCell,
                inst.MinecraftVersion,
                inst.Loader.ToString(),
                FormatRam(inst.RamMb),
                modCount,
                status,
                inst.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"));
        }

        AnsiConsole.Write(table);
    }

    // ── create wizard ─────────────────────────────────────────────────────────

    private async Task CreateWizardAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{L("instance.wizard_title")}[/]");
        AnsiConsole.Write(new Rule().RuleStyle("grey dim"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{L("instance.wizard_cancel_hint")}[/]");
        AnsiConsole.WriteLine();

        // 1. Name
        var name = AnsiConsole.Ask<string>($"[{UiTheme.AccentMarkup}]{L("instance.wizard_name_prompt")}[/]");
        if (name.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsValidName(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("instance.invalid_name")}[/] Avoid the characters: \\ / : * ? \" < > |");
            return;
        }

        if (_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]An instance named[/] '{Markup.Escape(name)}' [{UiTheme.AccentMarkup}]already exists.[/]");
            return;
        }

        // 2. Minecraft version
        string mcVersion;
        string[] versions = [];

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("instance.fetching_versions"), async _ =>
            {
                versions = await _mojang.GetReleaseVersionsAsync();
            });

        if (versions.Length == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{L("instance.mojang_offline")}[/]");
            mcVersion = AnsiConsole.Ask<string>($"[{UiTheme.AccentMarkup}]{L("instance.wizard_version_prompt")}[/]");
            if (mcVersion.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                return;
        }
        else
        {
            var latestLabel = $"latest  [dim]({versions[0]})[/]";
            var chosen = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[{UiTheme.AccentMarkup}]{L("instance.wizard_version_prompt")}[/]")
                    .PageSize(15)
                    .AddChoices(new[] { latestLabel }.Concat(versions).Append("(cancel)")));
            if (chosen.Equals("(cancel)", StringComparison.OrdinalIgnoreCase))
                return;
            mcVersion = chosen == latestLabel ? versions[0] : chosen;
        }

        // 3. Mod loader
        var loaderLabel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[{UiTheme.AccentMarkup}]{L("instance.wizard_loader_prompt")}[/]")
                .AddChoices(Enum.GetNames<ModLoader>().Concat(["(cancel)"])));
        if (loaderLabel.Equals("(cancel)", StringComparison.OrdinalIgnoreCase))
            return;
        var loader = Enum.Parse<ModLoader>(loaderLabel);

        // 4. RAM
        var ramLabel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[{UiTheme.AccentMarkup}]{L("instance.wizard_ram_prompt")}[/]")
                .AddChoices("1 GB", "2 GB", "4 GB", "6 GB", "8 GB", "12 GB", "16 GB", "(cancel)"));
        if (ramLabel.Equals("(cancel)", StringComparison.OrdinalIgnoreCase))
            return;

        var ramMb = int.Parse(ramLabel.Split(' ')[0]) * 1024;

        _store.Save(new Instance
        {
            Name             = name,
            MinecraftVersion = mcVersion,
            Loader           = loader,
            RamMb            = ramMb,
            CreatedAt        = DateTime.UtcNow
        });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Instance [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/] {L("instance.created")}");
        AnsiConsole.MarkupLine($"  {L("instance.detail_version")} : {mcVersion}");
        AnsiConsole.MarkupLine($"  {L("instance.detail_loader")} : {loader}");
        AnsiConsole.MarkupLine($"  {L("instance.detail_ram")} : {ramLabel}");
        AnsiConsole.MarkupLine($"  {L("instance.detail_path")} : [dim]{Markup.Escape(PathService.InstanceDir(name))}[/]");
        AnsiConsole.WriteLine();

        if (AnsiConsole.Confirm($"[{UiTheme.AccentMarkup}]Select[/] [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/] [dim]as the active instance?[/]"))
        {
            _state.ActiveInstance = name;
            UiService.Success("Active instance", $"Set to [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/].");
            var created = _store.Get(name);
            if (created is not null) _ = _launcher.PreWarmAsync(created);
        }
    }

    // ── import mrpack ─────────────────────────────────────────────────────────

    private async Task ImportMrpackAsync(string path)
    {
        if (!path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("instance.mrpack_only")}[/]");
            return;
        }

        var ramLabel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[{UiTheme.AccentMarkup}]RAM allocation[/]:")
                .AddChoices("1 GB", "2 GB", "4 GB", "6 GB", "8 GB", "12 GB", "16 GB", "(cancel)"));
        if (ramLabel.Equals("(cancel)", StringComparison.OrdinalIgnoreCase)) return;

        var ramMb = int.Parse(ramLabel.Split(' ')[0]) * 1024;
        var created = await _mrpack.ImportAsync(path, ramMb);
        if (created is not null)
        {
            _state.ActiveInstance = created;
            AnsiConsole.MarkupLine($"[dim]{L("instance.selected")}[/] [{UiTheme.AccentMarkup}]{Markup.Escape(created)}[/][dim]. {L("instance.run_hint")}[/]");
            var inst = _store.Get(created);
            if (inst is not null) _ = _launcher.PreWarmAsync(inst);
        }
    }

    // ── run ───────────────────────────────────────────────────────────────────

    private async Task RunInstanceAsync(string name)
    {
        var instance = _store.Get(name);

        if (instance is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        if (_tracker.IsRunning(name))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(name)} {L("instance.already_running")}[/]");
            _state.ActiveInstance = name;
            return;
        }

        if (!_auth.IsAuthenticated)
            AnsiConsole.MarkupLine($"[dim]{L("instance.not_signed_in")}[/]");

        // Warn if RAM is very low for a modded instance
        if (instance.Loader != ModLoader.Vanilla && instance.RamMb < 2048)
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] [dim]Only {FormatRam(instance.RamMb)} {L("instance.low_ram_warning")}[/]");

        _state.ActiveInstance = name;
        await _launcher.PrepareAndLaunchAsync(instance);
    }

    // ── select ────────────────────────────────────────────────────────────────

    private void SelectInstance(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        _state.ActiveInstance = name;
        UiService.Success("Active instance", $"Set to [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/].");

        // Kick off background pre-warm: extract natives, build classpath, cache Java version.
        // By the time the user types 'instance run', all the slow work is already done.
        var instance = _store.Get(name);
        if (instance is not null)
            _ = _launcher.PreWarmAsync(instance);
    }

    // ── deselect ──────────────────────────────────────────────────────────────

    private void DeselectInstance()
    {
        if (_state.ActiveInstance is null)
        {
            AnsiConsole.MarkupLine($"[dim]{L("instance.none_selected")}[/]");
            return;
        }

        var prev = _state.ActiveInstance;
        _state.ActiveInstance = null;
        AnsiConsole.MarkupLine($"[dim]Deselected[/] [{UiTheme.AccentMarkup}]{Markup.Escape(prev)}[/][dim].[/]");
    }

    // ── kill ──────────────────────────────────────────────────────────────────

    private void KillInstance(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        if (!_tracker.IsRunning(name))
        {
            AnsiConsole.MarkupLine($"[yellow]Instance '{Markup.Escape(name)}' is not running.[/]");
            return;
        }

        if (SettingsService.Current.ConfirmBeforeKill &&
            !AnsiConsole.Confirm($"[yellow]{L("instance.kill_confirm")}[/] [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/][yellow]?[/]"))
            return;

        _tracker.Remove(name);

        if (_state.ActiveInstance == name)
            _state.ActiveInstance = null;

        AnsiConsole.MarkupLine($"Instance [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/] terminated.");
    }

    // ── open ──────────────────────────────────────────────────────────────────

    private void OpenInstanceFolder(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var dir = PathService.InstanceDir(name);
        Directory.CreateDirectory(dir);

        try
        {
            PlatformHelper.OpenFolder(dir);
            AnsiConsole.MarkupLine($"[dim]Opened[/] {Markup.Escape(dir)}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not open folder:[/] {Markup.Escape(ex.Message)}");
        }
    }

    // ── info ──────────────────────────────────────────────────────────────────

    private void ShowInstanceInfo(string name)
    {
        var instance = _store.Get(name);
        if (instance is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var isActive  = name.Equals(_state.ActiveInstance, StringComparison.OrdinalIgnoreCase);
        var isRunning = _tracker.IsRunning(name);
        var allMods   = _mods.GetAll(name);
        var modLine   = allMods.Count == 0
            ? "[dim]none[/]"
            : $"{allMods.Count(m => m.Enabled)} enabled, {allMods.Count(m => !m.Enabled)} disabled [dim]({allMods.Count} total)[/]";
        var dir       = PathService.InstanceDir(name);
        var diskSize  = FormatBytes(GetDirectorySize(dir));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(name)}[/]" + (isActive ? $"  [{UiTheme.AccentMarkup}](active)[/]" : ""));
        AnsiConsole.MarkupLine($"  Version   : [{UiTheme.AccentMarkup}]{instance.MinecraftVersion}[/]");
        AnsiConsole.MarkupLine($"  Loader    : {instance.Loader}");
        if (!string.IsNullOrEmpty(instance.ModrinthVersionNumber))
            AnsiConsole.MarkupLine($"  Modpack   : [dim]{Markup.Escape(instance.ModrinthVersionNumber)}[/]");
        AnsiConsole.MarkupLine($"  RAM       : {FormatRam(instance.RamMb)}");
        AnsiConsole.MarkupLine($"  Mods      : {modLine}");
        AnsiConsole.MarkupLine($"  Disk      : [dim]{diskSize}[/]");
        AnsiConsole.MarkupLine($"  Status    : " + (isRunning ? "[green]running[/]" : "[dim]stopped[/]"));
        AnsiConsole.MarkupLine($"  Created   : [dim]{instance.CreatedAt.ToLocalTime():yyyy-MM-dd}[/]");
        AnsiConsole.MarkupLine($"  Path      : [dim]{Markup.Escape(dir)}[/]");
        AnsiConsole.WriteLine();
    }

    // ── delete ────────────────────────────────────────────────────────────────

    private async Task DeleteInstanceAsync(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var isRunning = _tracker.IsRunning(name);

        if (isRunning)
        {
            if (!AnsiConsole.Confirm($"[yellow]Instance[/] [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/] [yellow]is running. Stop it now?[/]"))
                return;

            var process = _tracker.Get(name);
            if (process is not null && !process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("/stop"); } catch { /* ignore */ }

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    if (process.HasExited) break;
                    await Task.Delay(250);
                }
            }

            if (_tracker.IsRunning(name))
            {
                if (!AnsiConsole.Confirm($"[yellow]Instance did not stop. Force-kill[/] [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/][yellow]?[/]"))
                    return;
                _tracker.Remove(name);
            }
            else
            {
                _tracker.Purge();
            }
        }

        if (!AnsiConsole.Confirm($"[yellow]Delete instance[/] [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/][yellow]? This removes its folder from disk.[/]"))
            return;

        try
        {
            _store.Delete(name);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to delete instance:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        if (_state.ActiveInstance?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            _state.ActiveInstance = null;

        UiService.Success("Instance deleted", $"[{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/] removed from disk.");
    }

    // ── rename ────────────────────────────────────────────────────────────────

    private async Task RenameInstanceAsync(string oldName, string newName)
    {
        if (!_store.Exists(oldName))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(oldName)}");
            return;
        }

        if (!IsValidName(newName))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Invalid new name.[/] Avoid the characters: \\ / : * ? \" < > |");
            return;
        }

        if (_store.Exists(newName))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]An instance named[/] '{Markup.Escape(newName)}' [{UiTheme.AccentMarkup}]already exists.[/]");
            return;
        }

        if (_tracker.IsRunning(oldName))
        {
            if (!AnsiConsole.Confirm($"[yellow]Instance[/] [{UiTheme.AccentMarkup}]{Markup.Escape(oldName)}[/] [yellow]is running. Stop it before renaming?[/]"))
                return;

            KillInstance(oldName);

            var process = _tracker.Get(oldName);
            if (process is not null)
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    if (process.HasExited) break;
                    await Task.Delay(250);
                }
            }

            if (_tracker.IsRunning(oldName))
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance is still running. Rename cancelled.[/]");
                return;
            }

            _tracker.Purge();
        }

        try
        {
            _store.Rename(oldName, newName);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to rename instance:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        if (_state.ActiveInstance?.Equals(oldName, StringComparison.OrdinalIgnoreCase) == true)
            _state.ActiveInstance = newName;

        UiService.Success("Instance renamed", $"[{UiTheme.AccentMarkup}]{Markup.Escape(oldName)}[/] → [{UiTheme.AccentMarkup}]{Markup.Escape(newName)}[/].");
    }

    // ── clone ─────────────────────────────────────────────────────────────────

    private async Task CloneInstanceAsync(string sourceName)
    {
        if (!_store.Exists(sourceName))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(sourceName)}");
            return;
        }

        var newName = AnsiConsole.Ask<string>($"[{UiTheme.AccentMarkup}]New instance name:[/]");

        if (!IsValidName(newName))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Invalid name.[/] Avoid the characters: \\ / : * ? \" < > |");
            return;
        }

        if (_store.Exists(newName))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]An instance named[/] '{Markup.Escape(newName)}' [{UiTheme.AccentMarkup}]already exists.[/]");
            return;
        }

        var source = _store.Get(sourceName)!;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync($"Cloning {Markup.Escape(sourceName)}...", async _ =>
            {
                await Task.Run(() =>
                {
                    // Copy the full instance directory tree
                    var srcDir  = PathService.InstanceDir(sourceName);
                    var destDir = PathService.InstanceDir(newName);
                    CopyDirectory(srcDir, destDir);
                });

                // Save a new manifest with the new name (and reset creation time)
                _store.Save(new Instance
                {
                    Name                  = newName,
                    MinecraftVersion      = source.MinecraftVersion,
                    Loader                = source.Loader,
                    RamMb                 = source.RamMb,
                    CreatedAt             = DateTime.UtcNow,
                    ModrinthProjectId     = source.ModrinthProjectId,
                    ModrinthVersionId     = source.ModrinthVersionId,
                    ModrinthVersionNumber = source.ModrinthVersionNumber,
                });
            });

        AnsiConsole.MarkupLine($"Cloned [{UiTheme.AccentMarkup}]{Markup.Escape(sourceName)}[/] → [{UiTheme.AccentMarkup}]{Markup.Escape(newName)}[/].");

        if (AnsiConsole.Confirm($"[{UiTheme.AccentMarkup}]Select[/] [{UiTheme.AccentMarkup}]{Markup.Escape(newName)}[/] [dim]as the active instance?[/]"))
        {
            _state.ActiveInstance = newName;
            UiService.Success("Active instance", $"Set to [{UiTheme.AccentMarkup}]{Markup.Escape(newName)}[/].");
            var cloned = _store.Get(newName);
            if (cloned is not null) _ = _launcher.PreWarmAsync(cloned);
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    // ── export ────────────────────────────────────────────────────────────────

    private async Task ExportInstanceAsync(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            $"{name}.zip");

        var outPath = AnsiConsole.Ask($"[{UiTheme.AccentMarkup}]Save to[/] ([grey]Enter for default[/]):", defaultPath);

        if (!outPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            outPath += ".zip";

        var srcDir = PathService.InstanceDir(name);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync($"Exporting {Markup.Escape(name)}...", async _ =>
            {
                if (File.Exists(outPath)) File.Delete(outPath);
                await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(srcDir, outPath));
            });

        AnsiConsole.MarkupLine($"Exported to [{UiTheme.AccentMarkup}]{Markup.Escape(outPath)}[/].");
    }

    // ── export as .mrpack ─────────────────────────────────────────────────────

    private async Task ExportMrpackAsync(string name, string versionId)
    {
        var instance = _store.Get(name);
        if (instance is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var mods = _mods.GetAll(name);
        var modrinthMods = mods.Count(m => m.Enabled && m.Source == "modrinth");
        var manualMods   = mods.Count(m => m.Enabled && m.Source != "modrinth");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Exporting .mrpack[/]  [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/]");
        AnsiConsole.MarkupLine($"  Minecraft  : {instance.MinecraftVersion}");
        AnsiConsole.MarkupLine($"  Loader     : {instance.Loader}");
        AnsiConsole.MarkupLine($"  Pack ver   : {versionId}");
        AnsiConsole.MarkupLine($"  Index mods : {modrinthMods} (Modrinth-sourced, linked by URL)");
        if (manualMods > 0)
            AnsiConsole.MarkupLine($"  Override   : {manualMods} (manual, bundled in overrides/mods/)");
        AnsiConsole.WriteLine();

        var outPath = await _mrpack.ExportAsync(instance, versionId);
        if (outPath is null) return;

        var size = new FileInfo(outPath).Length;
        AnsiConsole.MarkupLine(
            $"Exported [{UiTheme.AccentMarkup}]{Markup.Escape(Path.GetFileName(outPath))}[/] " +
            $"[dim]({FormatBytes(size)})[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Saved to: {Markup.Escape(outPath)}[/]");
        AnsiConsole.MarkupLine(
            "[dim]Import into McSH with: instance import <path>[/]");
    }

    // ── backup ────────────────────────────────────────────────────────────────

    private async Task BackupInstanceAsync(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var savesDir = Path.Combine(PathService.InstanceDir(name), "saves");
        if (!Directory.Exists(savesDir) || !Directory.EnumerateDirectories(savesDir).Any())
        {
            AnsiConsole.MarkupLine("[dim]No world saves found in this instance.[/]");
            return;
        }

        var backupDir  = PathService.InstanceBackupsDir(name);
        Directory.CreateDirectory(backupDir);

        var stamp      = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupPath = Path.Combine(backupDir, $"{name}-{stamp}.zip");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync($"Backing up saves for {Markup.Escape(name)}...", async _ =>
            {
                await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(savesDir, backupPath));
            });

        var info = new FileInfo(backupPath);
        AnsiConsole.MarkupLine($"Backup saved: [{UiTheme.AccentMarkup}]{Markup.Escape(backupPath)}[/] [dim]({FormatBytes(info.Length)})[/]");
    }

    // ── prism import ──────────────────────────────────────────────────────────

    private async Task ImportPrismAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Directory not found:[/] {Markup.Escape(path)}");
            return;
        }

        // Read instance.cfg for the display name
        var cfgPath = Path.Combine(path, "instance.cfg");
        if (!File.Exists(cfgPath))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance.cfg found.[/] Make sure this is a valid Prism/MultiMC instance folder.");
            return;
        }

        var cfg = File.ReadAllLines(cfgPath)
            .Where(l => l.Contains('='))
            .ToDictionary(
                l => l[..l.IndexOf('=')].Trim(),
                l => l[(l.IndexOf('=') + 1)..].Trim(),
                StringComparer.OrdinalIgnoreCase);

        cfg.TryGetValue("name", out var instanceName);
        instanceName = string.IsNullOrWhiteSpace(instanceName) ? Path.GetFileName(path) : instanceName;

        // Sanitise name
        instanceName = new string(instanceName.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        if (_store.Exists(instanceName))
            instanceName += "_imported";

        // Read mmc-pack.json for MC version and loader
        string mcVersion = "1.21.1";
        var loader = ModLoader.Vanilla;

        var mmcPackPath = Path.Combine(path, "mmc-pack.json");
        if (File.Exists(mmcPackPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mmcPackPath));
                if (doc.RootElement.TryGetProperty("components", out var components))
                {
                    foreach (var comp in components.EnumerateArray())
                    {
                        comp.TryGetProperty("uid", out var uid);
                        comp.TryGetProperty("version", out var ver);
                        var uidStr = uid.GetString() ?? "";
                        var verStr = ver.GetString() ?? "";

                        if (uidStr == "net.minecraft" && !string.IsNullOrEmpty(verStr))
                            mcVersion = verStr;
                        else if (uidStr == "net.fabricmc.fabric-loader") loader = ModLoader.Fabric;
                        else if (uidStr == "org.quiltmc.quilt-loader")   loader = ModLoader.Quilt;
                        else if (uidStr == "net.minecraftforge")          loader = ModLoader.Forge;
                        else if (uidStr == "net.neoforged")               loader = ModLoader.NeoForge;
                    }
                }
            }
            catch { /* use defaults */ }
        }

        var ramLabel = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[{UiTheme.AccentMarkup}]RAM allocation[/]:")
                .AddChoices("1 GB", "2 GB", "4 GB", "6 GB", "8 GB", "12 GB", "16 GB", "(cancel)"));
        if (ramLabel.Equals("(cancel)", StringComparison.OrdinalIgnoreCase)) return;
        var ramMb = int.Parse(ramLabel.Split(' ')[0]) * 1024;

        // Copy .minecraft contents to instance dir
        var minecraftSrc = Path.Combine(path, ".minecraft");
        if (!Directory.Exists(minecraftSrc))
            minecraftSrc = path; // some instances have files directly in the root

        var destDir = PathService.InstanceDir(instanceName);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync($"Importing {Markup.Escape(instanceName)}...", async _ =>
            {
                await Task.Run(() => CopyDirectory(minecraftSrc, destDir));
            });

        _store.Save(new Instance
        {
            Name             = instanceName,
            MinecraftVersion = mcVersion,
            Loader           = loader,
            RamMb            = ramMb,
            CreatedAt        = DateTime.UtcNow,
        });

        AnsiConsole.MarkupLine($"Imported [{UiTheme.AccentMarkup}]{Markup.Escape(instanceName)}[/].");
        AnsiConsole.MarkupLine($"  Version : {mcVersion}");
        AnsiConsole.MarkupLine($"  Loader  : {loader}");
        AnsiConsole.WriteLine();

        if (AnsiConsole.Confirm($"[{UiTheme.AccentMarkup}]Select[/] [{UiTheme.AccentMarkup}]{Markup.Escape(instanceName)}[/] [dim]as the active instance?[/]"))
        {
            _state.ActiveInstance = instanceName;
            UiService.Success("Active instance", $"Set to [{UiTheme.AccentMarkup}]{Markup.Escape(instanceName)}[/].");
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return !name.Any(c => Path.GetInvalidFileNameChars().Contains(c));
    }

    /// <summary>Formats a RAM value in MB to a human-readable string (e.g. "4 GB", "512 MB").</summary>
    private static string FormatRam(int ramMb) =>
        ramMb >= 1024 && ramMb % 1024 == 0
            ? $"{ramMb / 1024} GB"
            : ramMb >= 1024
                ? $"{ramMb / 1024.0:0.#} GB"
                : $"{ramMb} MB";

    /// <summary>Returns the total size of all files in a directory tree.</summary>
    // ── crash ─────────────────────────────────────────────────────────────────

    private async Task CrashAsync(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var crashDir = Path.Combine(PathService.InstanceDir(name), "crash-reports");
        if (!Directory.Exists(crashDir))
        {
            AnsiConsole.MarkupLine("[dim]No crash reports found for this instance.[/]");
            return;
        }

        var reports = Directory.GetFiles(crashDir, "*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .ToArray();

        if (reports.Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]No crash reports found for this instance.[/]");
            return;
        }

        var latest  = reports[0];
        var created = File.GetLastWriteTime(latest);
        var content = await File.ReadAllTextAsync(latest);
        var lines   = content.Split('\n');

        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/] [dim]— {Path.GetFileName(latest)} ({created:MMM d, yyyy HH:mm})[/]");
        if (reports.Length > 1)
            AnsiConsole.MarkupLine($"[dim]{reports.Length - 1} older report{(reports.Length > 2 ? "s" : "")} also found in crash-reports/[/]");
        AnsiConsole.WriteLine();

        // Show up to the start of "detailed walkthrough" — the stack trace is what matters
        var cutoff = Array.FindIndex(lines, l => l.Contains("A detailed walkthrough", StringComparison.OrdinalIgnoreCase));
        var display = cutoff > 0 ? lines[..cutoff] : lines[..Math.Min(50, lines.Length)];

        foreach (var line in display)
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");

        if (cutoff > 0)
            AnsiConsole.MarkupLine($"[dim]... full report at: {Markup.Escape(latest)}[/]");
    }

    // ── worlds ────────────────────────────────────────────────────────────────

    private void InstanceConfigMenu(string name)
    {
        var inst = _store.Get(name);
        if (inst is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("instance.not_found")}[/] {Markup.Escape(name)}");
            return;
        }

        string Val(string? s) => string.IsNullOrWhiteSpace(s) ? $"[dim]{L("instance.config_none")}[/]" : $"[{UiTheme.AccentMarkup}]{Markup.Escape(s)}[/]";
        string Bool(bool b) => b ? $"[green]{L("instance.config_on")}[/]" : $"[grey]{L("instance.config_off")}[/]";

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]{L("instance.config_title")}[/] [{UiTheme.AccentMarkup}]{Markup.Escape(inst.Name)}[/]")
                    .PageSize(20)
                    .AddChoices(
                        $"[bold]{L("instance.config_window")}[/]",
                        $"  {L("instance.config_fullscreen")}: {Bool(inst.Fullscreen)}",
                        $"  {L("instance.config_width")}: [{UiTheme.AccentMarkup}]{inst.WindowWidth}[/]",
                        $"  {L("instance.config_height")}: [{UiTheme.AccentMarkup}]{inst.WindowHeight}[/]",
                        "",
                        $"[bold]{L("instance.config_perf")}[/]",
                        $"  {L("instance.config_memory")}: [{UiTheme.AccentMarkup}]{inst.RamMb} {L("instance.config_mb")}[/]",
                        $"  {L("instance.config_jvm_args")}: {Val(inst.ExtraJvmArgs)}",
                        "",
                        $"[bold]{L("instance.config_env")}[/]",
                        $"  {L("instance.config_env_vars")}: {Val(inst.EnvVars)}",
                        "",
                        $"[bold]{L("instance.config_hooks")}[/]",
                        $"  {L("instance.config_pre_launch")}: {Val(inst.PreLaunchCommand)}",
                        $"  {L("instance.config_wrapper")}: {Val(inst.WrapperCommand)}",
                        $"  {L("instance.config_post_exit")}: {Val(inst.PostExitCommand)}",
                        "",
                        L("instance.config_back")
                    ));

            var back = L("instance.config_back");
            if (choice == back) break;
            if (choice.StartsWith("[bold]", StringComparison.Ordinal) || choice.Length == 0) continue;

            // ── Window ────────────────────────────────────────────────────────
            if (choice.Contains(L("instance.config_fullscreen"), StringComparison.Ordinal))
            {
                inst.Fullscreen = !inst.Fullscreen;
            }
            else if (choice.Contains(L("instance.config_width"), StringComparison.Ordinal))
            {
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_int")} (px)");
                if (int.TryParse(input, out var v) && v > 0) inst.WindowWidth = v;
            }
            else if (choice.Contains(L("instance.config_height"), StringComparison.Ordinal))
            {
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_int")} (px)");
                if (int.TryParse(input, out var v) && v > 0) inst.WindowHeight = v;
            }

            // ── Performance ───────────────────────────────────────────────────
            else if (choice.Contains(L("instance.config_memory"), StringComparison.Ordinal))
            {
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_int")} (MB)");
                if (int.TryParse(input, out var v) && v >= 256) inst.RamMb = v;
            }
            else if (choice.Contains(L("instance.config_jvm_args"), StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[dim]Example: -Xss1m -XX:+UseZGC[/]");
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_value")}");
                inst.ExtraJvmArgs = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
            }

            // ── Environment ───────────────────────────────────────────────────
            else if (choice.Contains(L("instance.config_env_vars"), StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[dim]One KEY=VALUE per line. Leave blank to clear.[/]");
                if (!string.IsNullOrWhiteSpace(inst.EnvVars))
                    AnsiConsole.MarkupLine($"[dim]Current:[/] {Markup.Escape(inst.EnvVars.Replace("\n", " | "))}");
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_value")}");
                inst.EnvVars = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
            }

            // ── Hooks ─────────────────────────────────────────────────────────
            else if (choice.Contains(L("instance.config_pre_launch"), StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[dim]Runs before Minecraft starts. Example: ./notify.sh[/]");
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_value")}");
                inst.PreLaunchCommand = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
            }
            else if (choice.Contains(L("instance.config_wrapper"), StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[dim]Wraps the Java process. Example: mangohud[/]");
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_value")}");
                inst.WrapperCommand = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
            }
            else if (choice.Contains(L("instance.config_post_exit"), StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[dim]Runs after Minecraft exits. Example: ./cleanup.sh[/]");
                var input = AnsiConsole.Ask<string>($"  {L("instance.config_enter_value")}");
                inst.PostExitCommand = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
            }

            _store.Save(inst);
            AnsiConsole.MarkupLine($"[dim]{L("instance.config_saved")}[/]");
        }
    }

    private async Task WorldsAsync(string name)
    {
        if (!_store.Exists(name))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(name)}");
            return;
        }

        var savesDir = Path.Combine(PathService.InstanceDir(name), "saves");
        if (!Directory.Exists(savesDir) || !Directory.EnumerateDirectories(savesDir).Any())
        {
            AnsiConsole.MarkupLine($"[dim]{L("instance.worlds_none")}[/]");
            return;
        }

        // Read world metadata
        var worlds = new List<(string folder, string displayName, DateTime lastPlayed, long size)>();
        foreach (var worldDir in Directory.GetDirectories(savesDir))
        {
            var folder      = Path.GetFileName(worldDir);
            var displayName = folder;
            var lastPlayed  = Directory.GetLastWriteTime(worldDir);

            var levelDat = Path.Combine(worldDir, "level.dat");
            if (File.Exists(levelDat))
            {
                try
                {
                    var nbt  = new fNbt.NbtFile(levelDat);
                    var data = (fNbt.NbtCompound)nbt.RootTag["Data"]!;
                    if (data.TryGet("LevelName", out fNbt.NbtTag? lvlTag))
                        displayName = lvlTag!.StringValue ?? folder;
                    if (data.TryGet("LastPlayed", out fNbt.NbtTag? lpTag))
                        lastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(((fNbt.NbtLong)lpTag!).Value).LocalDateTime;
                }
                catch { }
            }

            worlds.Add((folder, displayName, lastPlayed, GetDirectorySize(worldDir)));
        }

        worlds = [.. worlds.OrderByDescending(w => w.lastPlayed)];

        // Table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
            .AddColumn("[bold]World[/]")
            .AddColumn("[bold]Last Played[/]")
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned());

        for (int i = 0; i < worlds.Count; i++)
        {
            var w = worlds[i];
            table.AddRow(
                $"[dim]{i + 1}[/]",
                Markup.Escape(w.displayName),
                $"[dim]{McSH.Services.RecentService.RelativeTime(w.lastPlayed.ToUniversalTime())}[/]",
                $"[dim]{FormatBytes(w.size)}[/]"
            );
        }
        AnsiConsole.Write(table);

        if (Console.IsInputRedirected) return;

        // Pick a world
        var worldChoices = worlds.Select((w, i) => $"{i + 1}.  {w.displayName}").ToList();
        var cancelLabel = L("instance.worlds_cancel");
        worldChoices.Add(cancelLabel);

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[dim]{L("instance.worlds_select")}[/]")
                .AddChoices(worldChoices));

        if (pick == cancelLabel) return;
        var idx = int.Parse(pick.Split('.')[0]) - 1;
        var selected = worlds[idx];

        var lblBackup = L("instance.worlds_backup");
        var lblDelete = L("instance.worlds_delete");
        var lblOpen   = L("instance.worlds_open");

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[{UiTheme.AccentMarkup}]{Markup.Escape(selected.displayName)}[/]")
                .AddChoices(lblBackup, lblDelete, lblOpen, cancelLabel));

        if (action == lblBackup)
        {
            var backupDir  = PathService.InstanceBackupsDir(name);
            Directory.CreateDirectory(backupDir);
            var stamp      = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupPath = Path.Combine(backupDir, $"{selected.folder}-{stamp}.zip");
            await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(UiTheme.SpinnerStyle)
                .StartAsync($"Backing up {Markup.Escape(selected.displayName)}...", async _ =>
                    await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(
                        Path.Combine(savesDir, selected.folder), backupPath)));
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("instance.worlds_backup")}:[/] [dim]{Markup.Escape(backupPath)}[/]");
        }
        else if (action == lblDelete)
        {
            if (_tracker.IsRunning(name))
            {
                AnsiConsole.MarkupLine($"[red]{L("instance.worlds_running")}[/] [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/] [red]{L("instance.worlds_running_suffix")}[/]");
            }
            else if (AnsiConsole.Confirm($"[red]Delete[/] [bold]{Markup.Escape(selected.displayName)}[/][red]?[/] This cannot be undone."))
            {
                Directory.Delete(Path.Combine(savesDir, selected.folder), recursive: true);
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("instance.worlds_deleted")}[/]");
            }
        }
        else if (action == lblOpen)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = Path.Combine(savesDir, selected.folder),
                UseShellExecute = true,
            });
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return new FileInfo(f).Length; }
                    catch { return 0L; }
                });
        }
        catch { return 0; }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.#} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:0.#} MB",
        >= 1_024         => $"{bytes / 1_024.0:0.#} KB",
        _                => $"{bytes} B"
    };

    // ── update mods ───────────────────────────────────────────────────────────

    private async Task UpdateModsAsync(string name, Instance instance)
    {
        if (instance.Loader == ModLoader.Vanilla)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]This instance uses Vanilla.[/] Mods require a mod loader.");
            return;
        }

        var mods         = _mods.GetAll(name);
        var modrinthMods = mods.Where(m => m.Source == "modrinth").ToList();

        if (modrinthMods.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No Modrinth mods installed on this instance.[/]");
            return;
        }

        var loader  = LoaderString(instance.Loader);
        var outdated = new List<(ModEntry Entry, ModrinthVersion Latest)>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("instance.checking_mod_updates").Replace("{0}", modrinthMods.Count.ToString()), async _ =>
            {
                var tasks = modrinthMods.Select(async mod =>
                {
                    var latest = await _modrinth.GetCompatibleVersionAsync(
                        mod.ProjectId, instance.MinecraftVersion, loader);
                    return (mod, latest);
                });

                foreach (var (mod, latest) in await Task.WhenAll(tasks))
                {
                    if (latest is not null &&
                        !latest.Id.Equals(mod.VersionId, StringComparison.OrdinalIgnoreCase))
                        outdated.Add((mod, latest));
                }
            });

        if (outdated.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]All mods are up to date.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{outdated.Count}[/] update(s) available:");
        foreach (var (m, v) in outdated)
            AnsiConsole.MarkupLine(
                $"  [{UiTheme.AccentMarkup}]{Markup.Escape(m.Name)}[/]  [dim]{Markup.Escape(m.VersionNumber)}[/] → [dim]{Markup.Escape(v.VersionNumber)}[/]");
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm(L("instance.update_all_confirm"))) return;

        var modsDir = PathService.ModsDir(name);
        var failed  = new List<string>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .StartAsync(async ctx =>
            {
                foreach (var (mod, latest) in outdated)
                {
                    var task = ctx.AddTask(Markup.Escape(mod.Name));
                    task.MaxValue = 1;

                    var newDest = Path.Combine(modsDir, latest.FileName);
                    var ok      = await _modrinth.DownloadAsync(latest.DownloadUrl, newDest, latest.Sha512);

                    if (ok)
                    {
                        var oldFile = Path.Combine(modsDir, mod.FileName);
                        if (File.Exists(oldFile) &&
                            !oldFile.Equals(newDest, StringComparison.OrdinalIgnoreCase))
                            File.Delete(oldFile);

                        mod.VersionId     = latest.Id;
                        mod.VersionNumber = latest.VersionNumber;
                        mod.FileName      = latest.FileName;
                        mod.Sha512        = latest.Sha512;
                        _mods.Add(name, mod);
                    }
                    else
                    {
                        failed.Add(mod.Name);
                    }

                    task.Increment(1);
                }
            });

        if (failed.Count > 0)
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to update:[/] {string.Join(", ", failed.Select(Markup.Escape))}");
        else
            AnsiConsole.MarkupLine($"[dim]Updated {outdated.Count} mod(s).[/]");
    }

    private static string LoaderString(ModLoader loader) => loader switch
    {
        ModLoader.Fabric   => "fabric",
        ModLoader.Quilt    => "quilt",
        ModLoader.Forge    => "forge",
        ModLoader.NeoForge => "neoforge",
        _                  => "fabric"
    };

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine($"Usage: [{UiTheme.AccentMarkup}]instance[/] [grey]<subcommand> [[args]][/]");
        AnsiConsole.MarkupLine($"[dim]Subcommands:[/] list, create, select, run, stop, kill, delete, rename, clone, import, export, backup, update, worlds, crash, config, mrpack, prism, open, info");
        AnsiConsole.MarkupLine($"[dim]Aliases:[/] [{UiTheme.AccentMarkup}]i[/] [{UiTheme.AccentMarkup}]ins[/] [{UiTheme.AccentMarkup}]inst[/]  [dim]· type 'ref' for full details[/]");
    }
}

