using McSH;
using System.Diagnostics;
using McSH.Models;
using McSH.Services;
using McSH.State;
using Spectre.Console;

namespace McSH.Commands;

public class ModCommand
{
    private readonly AppState        _state;
    private readonly InstanceStore   _instances;
    private readonly ModStore        _mods;
    private readonly ModrinthService _modrinth;
    private string _lastModQuery = "";

    private static string L(string key) => McSH.Services.LanguageService.Get(key);

    public ModCommand(AppState state, InstanceStore instances, ModStore mods, ModrinthService modrinth)
    {
        _state     = state;
        _instances = instances;
        _mods      = mods;
        _modrinth  = modrinth;
    }

    public async Task ExecuteAsync(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return; }

        switch (args[0].ToLowerInvariant())
        {
            case "list" or "ls":
                ListMods();
                break;

            case "search":
                if (args.Length < 2)
                {
                    if (!string.IsNullOrEmpty(_lastModQuery)) await SearchAsync(_lastModQuery);
                    else AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod search [grey]<query>[/]");
                }
                else await SearchAsync(string.Join(' ', args[1..]));
                break;

            case "install":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod install [grey]<id|#>[/]");
                else await InstallAsync(args[1]);
                break;

            case "details" or "info":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod details [grey]<id|#>[/]");
                else await DetailsAsync(args[1]);
                break;

            case "import":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod import [grey]<path>[/]");
                else ImportMod(string.Join(' ', args[1..]));
                break;

            case "remove" or "rm" or "delete" or "uninstall":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod remove [grey]<name>[/]");
                else RemoveMod(string.Join(' ', args[1..]));
                break;

            case "toggle":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod toggle [grey]<name>[/]");
                else ToggleMod(string.Join(' ', args[1..]));
                break;

            case "open":
                OpenModsFolder();
                break;

            case "profile":
                await ProfileAsync(args[1..]);
                break;

            default:
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Unknown subcommand:[/] mod {Markup.Escape(args[0])}");
                PrintUsage();
                break;
        }
    }

    // ── list ──────────────────────────────────────────────────────────────────

    private void ListMods()
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var mods = _mods.GetAll(name);

        if (mods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("mod.none_installed")}[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn($"[bold]{L("mod.col_name")}[/]").NoWrap())
            .AddColumn($"[bold]{L("mod.col_version")}[/]")
            .AddColumn($"[bold]{L("mod.col_status")}[/]")
            .AddColumn($"[bold]{L("mod.col_source")}[/]");

        // Enabled mods first, then alphabetical within each group.
        var sorted = mods
            .OrderByDescending(m => m.Enabled)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var m in sorted)
        {
            var status  = m.Enabled ? $"[green]{L("mod.status_enabled")}[/]" : $"[dim]{L("mod.status_disabled")}[/]";
            var source  = m.Source == "manual" ? "[dim]manual[/]" : "modrinth";
            var version = string.IsNullOrWhiteSpace(m.VersionNumber) ? "[dim]—[/]" : Markup.Escape(m.VersionNumber);
            table.AddRow(
                Markup.Escape(m.Name),
                version,
                status,
                source);
        }

        AnsiConsole.Write(table);
        var enabledCount  = sorted.Count(m => m.Enabled);
        var disabledCount = sorted.Count - enabledCount;
        var summary = disabledCount > 0
            ? $"[dim]{enabledCount} enabled, {disabledCount} disabled[/]"
            : $"[dim]{enabledCount} mod(s) enabled[/]";
        AnsiConsole.MarkupLine(summary + $"  [dim]· {L("mod.toggle_hint")}[/]");
    }

    // ── details ───────────────────────────────────────────────────────────────

    private async Task DetailsAsync(string idOrNumber)
    {
        var slug = ResolveSlug(idOrNumber);
        if (slug is null) return;

        ModrinthProjectDetails? details = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("mod.fetching_details"), async _ =>
            {
                details = await _modrinth.GetProjectDetailsAsync(slug);
            });

        if (details is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not fetch project details.[/]");
            return;
        }

        PrintDetails(details);

        AnsiConsole.WriteLine();
        if (AnsiConsole.Confirm(L("mod.install_confirm")))
            await InstallAsync(details.Slug);
    }

    // ── search ────────────────────────────────────────────────────────────────

    private async Task SearchAsync(string query)
    {
        var (name, instance) = RequireModdableInstance();
        if (name is null || instance is null) return;

        var loader = LoaderString(instance.Loader);

        ModSearchHit[]? hits = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync($"Searching Modrinth for '{Markup.Escape(query)}'...", async _ =>
            {
                hits = await _modrinth.SearchAsync(query, instance.MinecraftVersion, loader, "mod");
            });

        if (hits is null || hits.Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]No results found.[/]");
            return;
        }

        _state.LastSearchHits = hits;
        _lastModQuery = query;

        // Build a set of installed slugs so we can flag already-installed mods.
        var installedSlugs = _mods.GetAll(name)
            .Where(m => !string.IsNullOrEmpty(m.Slug))
            .Select(m => m.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]#[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Name[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Author[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Downloads[/]").RightAligned().NoWrap())
            .AddColumn("[bold]Description[/]");

        for (var i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            var isInstalled = installedSlugs.Contains(h.Slug);
            var nameCell = isInstalled
                ? $"[{UiTheme.AccentMarkup}]{Markup.Escape(h.Title)}[/] [green](installed)[/]"
                : $"[{UiTheme.AccentMarkup}]{Markup.Escape(h.Title)}[/]";
            table.AddRow(
                $"[dim]{i + 1}[/]",
                nameCell,
                Markup.Escape(h.Author),
                FormatDownloads(h.Downloads),
                Markup.Escape(h.Description.Length > 60
                    ? h.Description[..57] + "..."
                    : h.Description));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]Use 'mod install <#>' to install.[/]");
    }

    // ── install ───────────────────────────────────────────────────────────────

    private async Task InstallAsync(string idOrNumber)
    {
        var (name, instance) = RequireModdableInstance();
        if (name is null || instance is null) return;

        // Resolve slug/id — accept a search result number or a direct slug
        string projectIdOrSlug;
        if (int.TryParse(idOrNumber, out var num))
        {
            if (_state.LastSearchHits.Length == 0)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No search results in memory. Run 'mod search' first.[/]");
                return;
            }
            if (num < 1 || num > _state.LastSearchHits.Length)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Invalid number.[/] Pick 1–{_state.LastSearchHits.Length}.");
                return;
            }
            projectIdOrSlug = _state.LastSearchHits[num - 1].Slug;
        }
        else
        {
            projectIdOrSlug = idOrNumber;
        }

        var loader    = LoaderString(instance.Loader);
        var installed = _mods.GetAll(name);
        var installedIds = installed.Select(m => m.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<(ModrinthProject Project, ModrinthVersion Version)>? toInstall = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("mod.resolving_deps"), async _ =>
            {
                toInstall = await _modrinth.ResolveAllAsync(
                    projectIdOrSlug, instance.MinecraftVersion, loader, installedIds);
            });

        if (toInstall is null || toInstall.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to install.[/] Mod may not be compatible with this instance, or is already installed.");
            return;
        }

        // Show plan
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"The following [{UiTheme.AccentMarkup}]{toInstall.Count}[/] mod(s) will be installed:");
        foreach (var (p, v) in toInstall)
            AnsiConsole.MarkupLine($"  [{UiTheme.AccentMarkup}]{Markup.Escape(p.Title)}[/] [dim]{Markup.Escape(v.VersionNumber)}[/]");
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm(L("mod.proceed_confirm"))) return;

        var modsDir = PathService.ModsDir(name);
        Directory.CreateDirectory(modsDir);

        var failed = new List<string>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .StartAsync(async ctx =>
            {
                foreach (var (p, v) in toInstall)
                {
                    var task = ctx.AddTask(Markup.Escape(p.Title));
                    task.MaxValue = 1;

                    var dest = Path.Combine(modsDir, v.FileName);
                    var ok   = await _modrinth.DownloadAsync(v.DownloadUrl, dest, v.Sha512);

                    if (ok)
                    {
                        _mods.Add(name, new ModEntry
                        {
                            ProjectId     = p.Id,
                            Slug          = p.Slug,
                            Name          = p.Title,
                            VersionId     = v.Id,
                            VersionNumber = v.VersionNumber,
                            FileName      = v.FileName,
                            Sha512        = v.Sha512,
                            Enabled       = true,
                            Source        = "modrinth"
                        });
                    }
                    else
                    {
                        failed.Add(p.Title);
                    }

                    task.Increment(1);
                }
            });

        if (failed.Count > 0)
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to download:[/] {string.Join(", ", failed.Select(Markup.Escape))}");
        else
            AnsiConsole.MarkupLine($"[dim]Installed {toInstall.Count} mod(s).[/]");
    }

    // ── import ────────────────────────────────────────────────────────────────

    private void ImportMod(string sourcePath)
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        if (!File.Exists(sourcePath))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]File not found:[/] {Markup.Escape(sourcePath)}");
            return;
        }

        if (!sourcePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Only .jar files can be imported.[/]");
            return;
        }

        var modsDir  = PathService.ModsDir(name);
        Directory.CreateDirectory(modsDir);

        var fileName = Path.GetFileName(sourcePath);
        var dest     = Path.Combine(modsDir, fileName);

        File.Copy(sourcePath, dest, overwrite: true);

        var modName = Path.GetFileNameWithoutExtension(fileName);
        _mods.Add(name, new ModEntry
        {
            Name     = modName,
            FileName = fileName,
            Enabled  = true,
            Source   = "manual"
        });

        AnsiConsole.MarkupLine($"Imported [{UiTheme.AccentMarkup}]{Markup.Escape(modName)}[/].");
    }

    // ── remove ────────────────────────────────────────────────────────────────

    private void RemoveMod(string search)
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var mods = _mods.GetAll(name);
        var mod  = mods.FirstOrDefault(m =>
            m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            m.FileName.Contains(search, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No mod matching[/] '{Markup.Escape(search)}' [{UiTheme.AccentMarkup}]found.[/]");
            return;
        }

        if (!AnsiConsole.Confirm($"Remove [{UiTheme.AccentMarkup}]{Markup.Escape(mod.Name)}[/]?"))
            return;

        // Delete the file from disk (handle both enabled and disabled variants)
        var modsDir = PathService.ModsDir(name);
        var filePath = Path.Combine(modsDir, mod.FileName);
        if (File.Exists(filePath)) File.Delete(filePath);

        // Remove from mod list
        mods.Remove(mod);
        _mods.Save(name, mods);

        AnsiConsole.MarkupLine($"[dim]Removed[/] [{UiTheme.AccentMarkup}]{Markup.Escape(mod.Name)}[/][dim].[/]");
    }

    // ── toggle ────────────────────────────────────────────────────────────────

    private void ToggleMod(string search)
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var mods = _mods.GetAll(name);
        var mod  = mods.FirstOrDefault(m =>
            m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            m.FileName.Contains(search, StringComparison.OrdinalIgnoreCase));

        if (mod is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No mod matching[/] '{Markup.Escape(search)}' [{UiTheme.AccentMarkup}]found.[/]");
            return;
        }

        var modsDir  = PathService.ModsDir(name);
        string oldPath, newPath;

        if (mod.Enabled)
        {
            // Disable: add .disabled suffix
            oldPath = Path.Combine(modsDir, mod.FileName);
            var disabledName = mod.FileName + ".disabled";
            newPath = Path.Combine(modsDir, disabledName);
            if (File.Exists(oldPath)) File.Move(oldPath, newPath, overwrite: true);
            mod.FileName = disabledName;
            mod.Enabled  = false;
            AnsiConsole.MarkupLine($"[dim]Disabled[/] [{UiTheme.AccentMarkup}]{Markup.Escape(mod.Name)}[/][dim].[/]");
        }
        else
        {
            // Enable: strip .disabled suffix
            oldPath = Path.Combine(modsDir, mod.FileName);
            var enabledName = mod.FileName.EndsWith(".disabled")
                ? mod.FileName[..^".disabled".Length]
                : mod.FileName;
            newPath = Path.Combine(modsDir, enabledName);
            if (File.Exists(oldPath)) File.Move(oldPath, newPath, overwrite: true);
            mod.FileName = enabledName;
            mod.Enabled  = true;
            AnsiConsole.MarkupLine($"[dim]Enabled[/] [{UiTheme.AccentMarkup}]{Markup.Escape(mod.Name)}[/][dim].[/]");
        }

        _mods.Save(name, mods);
    }

    // ── open ──────────────────────────────────────────────────────────────────

    private void OpenModsFolder()
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var modsDir = PathService.ModsDir(name);
        Directory.CreateDirectory(modsDir);

        PlatformHelper.OpenFolder(modsDir);
        AnsiConsole.MarkupLine($"[dim]Opened[/] {Markup.Escape(modsDir)}");
    }

    // ── profile ───────────────────────────────────────────────────────────────

    private async Task ProfileAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list" or "ls":
                ListProfiles();
                break;
            case "save":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod profile save [grey]<name>[/]");
                else SaveProfile(string.Join(' ', args[1..]));
                break;
            case "load":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod profile load [grey]<name>[/]");
                else LoadProfile(string.Join(' ', args[1..]));
                break;
            case "delete" or "rm" or "remove":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod profile delete [grey]<name>[/]");
                else DeleteProfile(string.Join(' ', args[1..]));
                break;
            default:
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] mod profile [grey]<list|save|load|delete>[/]");
                break;
        }

        await Task.CompletedTask;
    }

    private List<McSH.Models.ModProfile> LoadProfiles(string instanceName)
    {
        var path = PathService.ModProfilesPath(instanceName);
        if (!File.Exists(path)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<McSH.Models.ModProfile>>(
                File.ReadAllText(path)) ?? [];
        }
        catch { return []; }
    }

    private void SaveProfiles(string instanceName, List<McSH.Models.ModProfile> profiles)
    {
        File.WriteAllText(
            PathService.ModProfilesPath(instanceName),
            System.Text.Json.JsonSerializer.Serialize(profiles,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private void ListProfiles()
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var profiles = LoadProfiles(name);
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("mod.no_profiles")}[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Profile[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Mods[/]").RightAligned());

        foreach (var p in profiles)
            table.AddRow(Markup.Escape(p.Name), $"[dim]{p.EnabledMods.Count}[/]");

        AnsiConsole.Write(table);
    }

    private void SaveProfile(string profileName)
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var mods     = _mods.GetAll(name);
        var enabled  = mods.Where(m => m.Enabled).Select(m => m.Name).ToList();
        var profiles = LoadProfiles(name);

        var existing = profiles.FirstOrDefault(p =>
            p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.EnabledMods = enabled;
            AnsiConsole.MarkupLine($"[dim]Updated profile[/] [{UiTheme.AccentMarkup}]{Markup.Escape(profileName)}[/] [dim]({enabled.Count} mods).[/]");
        }
        else
        {
            profiles.Add(new McSH.Models.ModProfile { Name = profileName, EnabledMods = enabled });
            AnsiConsole.MarkupLine($"[dim]Saved profile[/] [{UiTheme.AccentMarkup}]{Markup.Escape(profileName)}[/] [dim]({enabled.Count} mods).[/]");
        }

        SaveProfiles(name, profiles);
    }

    private void LoadProfile(string profileName)
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var profiles = LoadProfiles(name);
        var profile  = profiles.FirstOrDefault(p =>
            p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Profile not found:[/] {Markup.Escape(profileName)}");
            return;
        }

        var mods    = _mods.GetAll(name);
        var modsDir = PathService.ModsDir(name);
        var changed = 0;

        foreach (var mod in mods)
        {
            var shouldBeEnabled = profile.EnabledMods.Contains(mod.Name, StringComparer.OrdinalIgnoreCase);
            if (mod.Enabled == shouldBeEnabled) continue;

            var oldPath = Path.Combine(modsDir, mod.FileName);

            if (shouldBeEnabled && !mod.Enabled)
            {
                var enabledName = mod.FileName.EndsWith(".disabled")
                    ? mod.FileName[..^".disabled".Length]
                    : mod.FileName;
                var newPath = Path.Combine(modsDir, enabledName);
                if (File.Exists(oldPath)) File.Move(oldPath, newPath, overwrite: true);
                mod.FileName = enabledName;
                mod.Enabled  = true;
            }
            else if (!shouldBeEnabled && mod.Enabled)
            {
                var disabledName = mod.FileName + ".disabled";
                var newPath = Path.Combine(modsDir, disabledName);
                if (File.Exists(oldPath)) File.Move(oldPath, newPath, overwrite: true);
                mod.FileName = disabledName;
                mod.Enabled  = false;
            }

            changed++;
        }

        _mods.Save(name, mods);
        AnsiConsole.MarkupLine($"[dim]Loaded profile[/] [{UiTheme.AccentMarkup}]{Markup.Escape(profileName)}[/][dim]. {changed} mod(s) toggled.[/]");
    }

    private void DeleteProfile(string profileName)
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var profiles = LoadProfiles(name);
        var removed  = profiles.RemoveAll(p =>
            p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Profile not found:[/] {Markup.Escape(profileName)}");
            return;
        }

        SaveProfiles(name, profiles);
        AnsiConsole.MarkupLine($"[dim]Deleted profile[/] [{UiTheme.AccentMarkup}]{Markup.Escape(profileName)}[/][dim].[/]");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns the active instance name, or prints an error and returns null.</summary>
    private (string? name, Instance? instance) RequireInstance()
    {
        if (_state.ActiveInstance is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No instance selected.[/] Use 'instance select <name>' first.");
            return (null, null);
        }
        var inst = _instances.Get(_state.ActiveInstance);
        if (inst is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Instance not found:[/] {Markup.Escape(_state.ActiveInstance)}");
            return (null, null);
        }
        return (_state.ActiveInstance, inst);
    }

    /// <summary>Same as RequireInstance but also rejects Vanilla (mods need a loader).</summary>
    private (string? name, Instance? instance) RequireModdableInstance()
    {
        var (name, inst) = RequireInstance();
        if (inst is null) return (null, null);

        if (inst.Loader == ModLoader.Vanilla)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]This instance uses Vanilla.[/] Mods require a mod loader.");
            return (null, null);
        }

        return (name, inst);
    }

    private static string LoaderString(ModLoader loader) => loader switch
    {
        ModLoader.Fabric   => "fabric",
        ModLoader.Quilt    => "quilt",
        ModLoader.Forge    => "forge",
        ModLoader.NeoForge => "neoforge",
        _                  => "fabric"
    };

    private string? ResolveSlug(string idOrNumber)
    {
        if (int.TryParse(idOrNumber, out var num))
        {
            if (_state.LastSearchHits.Length == 0)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No search results in memory. Run 'mod search' first.[/]");
                return null;
            }
            if (num < 1 || num > _state.LastSearchHits.Length)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Invalid number.[/] Pick 1–{_state.LastSearchHits.Length}.");
                return null;
            }
            return _state.LastSearchHits[num - 1].Slug;
        }
        return idOrNumber;
    }

    private static void PrintDetails(ModrinthProjectDetails d)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold {UiTheme.AccentMarkup}]{Markup.Escape(d.Title)}[/]");
        AnsiConsole.MarkupLine($"[dim]modrinth.com/mod/{Markup.Escape(d.Slug)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  Downloads : [{UiTheme.AccentMarkup}]{FormatDownloads(d.Downloads)}[/]   Followers: {FormatDownloads(d.Followers)}");
        AnsiConsole.MarkupLine($"  Client    : {d.ClientSide}   Server: {d.ServerSide}");
        if (d.Categories.Length > 0)
            AnsiConsole.MarkupLine($"  Tags      : {Markup.Escape(string.Join(", ", d.Categories))}");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[dim]Description[/]").RuleStyle("grey dim").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Markup.Escape(StripMarkdown(d.Body)));
        AnsiConsole.WriteLine();
        var links = new List<string>();
        if (d.SourceUrl  is not null) links.Add($"{L("mod.link_source")}: {d.SourceUrl}");
        if (d.IssuesUrl  is not null) links.Add($"{L("mod.link_issues")}: {d.IssuesUrl}");
        if (d.WikiUrl    is not null) links.Add($"{L("mod.link_wiki")}: {d.WikiUrl}");
        if (d.DiscordUrl is not null) links.Add($"{L("mod.link_discord")}: {d.DiscordUrl}");
        if (links.Count > 0)
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(string.Join("  |  ", links))}[/]");
    }

    private static string StripMarkdown(string md)
    {
        // Trim to a readable length
        if (md.Length > 2000) md = md[..2000] + "\n[...]";

        var lines = md.Split('\n');
        var result = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            // Headings → plain text (keep the text)
            if (line.StartsWith("### "))      line = line[4..];
            else if (line.StartsWith("## "))  line = line[3..];
            else if (line.StartsWith("# "))   line = line[2..];
            // Horizontal rules
            if (line is "---" or "***" or "___") line = "─────────────────────";
            // Strip inline: bold/italic, code, links [text](url) → text
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\*{1,3}(.+?)\*{1,3}", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"`(.+?)`", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[(.+?)\]\(.+?\)", "$1");
            result.AppendLine(line);
        }
        return result.ToString().TrimEnd();
    }

    private static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000     => $"{n / 1_000.0:0.#}K",
        _            => n.ToString()
    };

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine($"Usage: [{UiTheme.AccentMarkup}]mod[/] [grey]<list|search|install|remove|details|import|toggle|open|profile>[/] [[args]]");
        AnsiConsole.MarkupLine($"       Alias: [{UiTheme.AccentMarkup}]m[/]");
    }
}


