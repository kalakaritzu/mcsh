using McSH;
using System.Diagnostics;
using McSH.Models;
using McSH.Services;
using McSH.State;
using Spectre.Console;

namespace McSH.Commands;

/// <summary>
/// Handles search/install/open for non-mod Modrinth content: resource packs, shaders, and plugins.
/// One instance per content type is created in CommandRouter.
/// </summary>
public class ContentCommand
{
    public enum ContentType { ResourcePack, Shader, Plugin, Datapack }

    private static readonly HashSet<string> ShaderLoaderSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "iris", "irisshaders", "iris-shaders",
        "optifine", "optifabric",
        "oculus",           // Forge port of Iris
        "rubidium-extra",   // sometimes bundled shader support
    };

    private readonly ContentType      _type;
    private readonly AppState         _state;
    private readonly InstanceStore    _instances;
    private readonly ModStore         _mods;
    private readonly ModrinthService  _modrinth;

    // Modrinth project_type string for each content type
    private static string ProjectType(ContentType t) => t switch
    {
        ContentType.ResourcePack => "resourcepack",
        ContentType.Shader       => "shader",
        ContentType.Plugin       => "plugin",
        ContentType.Datapack     => "datapack",
        _                        => "resourcepack"
    };

    private static string L(string key) => McSH.Services.LanguageService.Get(key);

    // Display label
    private static string Label(ContentType t) => t switch
    {
        ContentType.ResourcePack => "resource pack",
        ContentType.Shader       => "shader",
        ContentType.Plugin       => "plugin",
        ContentType.Datapack     => "datapack",
        _                        => "resource pack"
    };

    private static string TargetDir(ContentType t, string instanceName) => t switch
    {
        ContentType.ResourcePack => PathService.ResourcePacksDir(instanceName),
        ContentType.Shader       => PathService.ShaderPacksDir(instanceName),
        ContentType.Plugin       => PathService.PluginsDir(instanceName),
        ContentType.Datapack     => PathService.DatapacksDir(instanceName),
        _                        => PathService.ResourcePacksDir(instanceName)
    };

    public ContentCommand(ContentType type, AppState state, InstanceStore instances, ModStore mods, ModrinthService modrinth)
    {
        _type      = type;
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
            case "search":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] {Label(_type)} search [grey]<query>[/]");
                else await SearchAsync(string.Join(' ', args[1..]));
                break;

            case "install":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] {Label(_type)} install [grey]<#|slug>[/]");
                else await InstallAsync(args[1]);
                break;

            case "details" or "info":
                if (args.Length < 2) AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] {Label(_type)} details [grey]<#|slug>[/]");
                else await DetailsAsync(args[1]);
                break;

            case "open":
                OpenFolder();
                break;

            default:
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Unknown subcommand:[/] {Markup.Escape(args[0])}");
                PrintUsage();
                break;
        }
    }

    // ── search ────────────────────────────────────────────────────────────────

    private async Task SearchAsync(string query)
    {
        var (_, instance) = RequireInstance();
        if (instance is null) return;

        // Shaders don't need a loader/version facet; resource packs just need version;
        // plugins need version + loader (bukkit/paper/etc.) but we simplify to version only.
        string? mcVersion = _type == ContentType.Shader ? null : instance.MinecraftVersion;
        string? loader    = null; // no loader facet for content (Modrinth handles compatibility)

        ModSearchHit[]? hits = null;
        var label = Label(_type);
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync($"Searching Modrinth for {label}s matching '{Markup.Escape(query)}'...", async _ =>
            {
                hits = await _modrinth.SearchAsync(query, mcVersion, loader, ProjectType(_type));
            });

        if (hits is null || hits.Length == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("content.no_results")}[/]");
            return;
        }

        _state.LastSearchHits = hits;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]#[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn($"[bold]{L("content.col_name")}[/]").NoWrap())
            .AddColumn(new TableColumn($"[bold]{L("content.col_author")}[/]").NoWrap())
            .AddColumn(new TableColumn($"[bold]{L("content.col_downloads")}[/]").RightAligned().NoWrap())
            .AddColumn($"[bold]{L("content.col_description")}[/]");

        for (var i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            table.AddRow(
                $"[dim]{i + 1}[/]",
                $"[{UiTheme.AccentMarkup}]{Markup.Escape(h.Title)}[/]",
                Markup.Escape(h.Author),
                FormatDownloads(h.Downloads),
                Markup.Escape(h.Description.Length > 60 ? h.Description[..57] + "..." : h.Description));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{string.Format(L("content.install_hint"), label)}[/]");
    }

    // ── install ───────────────────────────────────────────────────────────────

    private async Task InstallAsync(string idOrNumber)
    {
        var (name, instance) = RequireInstance();
        if (name is null || instance is null) return;

        string projectIdOrSlug;
        if (int.TryParse(idOrNumber, out var num))
        {
            if (_state.LastSearchHits.Length == 0)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("content.no_search_results")}[/] {string.Format(L("content.search_first"), Label(_type))}");
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

        // Warn if installing a shader with no known shader loader mod present
        if (_type == ContentType.Shader)
        {
            var installedMods = _mods.GetAll(name);
            var hasShaderLoader = installedMods.Any(m =>
                ShaderLoaderSlugs.Contains(m.Slug) ||
                ShaderLoaderSlugs.Contains(m.Name));

            if (!hasShaderLoader)
            {
                AnsiConsole.MarkupLine("[yellow]Warning:[/] No shader loader detected (Iris, Oculus, OptiFine).");
                AnsiConsole.MarkupLine("[dim]The shader pack will be downloaded but won't work until you install a shader loader.[/]");
                if (!AnsiConsole.Confirm(L("content.install_anyway"))) return;
            }
        }

        var mcVersion = _type == ContentType.Shader ? null : instance.MinecraftVersion;

        ModrinthProject? project = null;
        ModrinthVersion? version = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("content.resolving"), async _ =>
            {
                project = await _modrinth.GetProjectAsync(projectIdOrSlug);
                if (project is not null)
                    version = await _modrinth.GetCompatibleVersionAsync(project.Id, mcVersion, null);
            });

        if (project is null || version is null)
        {
            AnsiConsole.MarkupLine($"[yellow]{L("content.no_compatible")}[/]");
            return;
        }

        var destDir = TargetDir(_type, name);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, version.FileName);

        if (File.Exists(dest))
        {
            AnsiConsole.MarkupLine($"[dim]{string.Format(L("content.already_installed"), Markup.Escape(project.Title))}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"{string.Format(L("content.installing"), $"[{UiTheme.AccentMarkup}]{Markup.Escape(project.Title)}[/]", $"[dim]{Markup.Escape(version.VersionNumber)}[/]")}");

        var ok = await _modrinth.DownloadAsync(version.DownloadUrl, dest, version.Sha512);
        if (ok)
        {
            AnsiConsole.MarkupLine($"[dim]{L("content.installed_to")}[/] {Markup.Escape(destDir)}");
            if (_type == ContentType.Datapack)
                AnsiConsole.MarkupLine($"[dim]{L("content.datapack_hint")}[/]");
        }
        else
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("content.download_failed")}[/]");
    }

    // ── open ──────────────────────────────────────────────────────────────────

    private void OpenFolder()
    {
        var (name, _) = RequireInstance();
        if (name is null) return;

        var dir = TargetDir(_type, name);
        Directory.CreateDirectory(dir);

        PlatformHelper.OpenFolder(dir);
        AnsiConsole.MarkupLine($"[dim]Opened[/] {Markup.Escape(dir)}");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // ── details ───────────────────────────────────────────────────────────────

    private async Task DetailsAsync(string idOrNumber)
    {
        var slug = ResolveSlug(idOrNumber);
        if (slug is null) return;

        ModrinthProjectDetails? details = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("content.fetching_details"), async _ =>
            {
                details = await _modrinth.GetProjectDetailsAsync(slug);
            });

        if (details is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("content.fetch_failed")}[/]");
            return;
        }

        PrintDetails(details);

        AnsiConsole.WriteLine();
        if (AnsiConsole.Confirm(string.Format(L("content.install_confirm"), Label(_type))))
            await InstallAsync(details.Slug);
    }

    private string? ResolveSlug(string idOrNumber)
    {
        if (int.TryParse(idOrNumber, out var num))
        {
            if (_state.LastSearchHits.Length == 0)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("content.no_search_results")}[/] {string.Format(L("content.search_first"), Label(_type))}");
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
        AnsiConsole.MarkupLine($"[dim]modrinth.com/project/{Markup.Escape(d.Slug)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {L("content.details_downloads")} : [{UiTheme.AccentMarkup}]{FormatDownloads(d.Downloads)}[/]   {L("content.details_followers")}: {FormatDownloads(d.Followers)}");
        AnsiConsole.MarkupLine($"  {L("content.details_client")} : {d.ClientSide}   {L("content.details_server")}: {d.ServerSide}");
        if (d.Categories.Length > 0)
            AnsiConsole.MarkupLine($"  {L("content.details_tags")} : {Markup.Escape(string.Join(", ", d.Categories))}");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[dim]{L("content.details_description")}[/]").RuleStyle("grey dim").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Markup.Escape(StripMarkdown(d.Body)));
        AnsiConsole.WriteLine();
        var links = new List<string>();
        if (d.SourceUrl  is not null) links.Add($"{L("content.link_source")}: {d.SourceUrl}");
        if (d.IssuesUrl  is not null) links.Add($"{L("content.link_issues")}: {d.IssuesUrl}");
        if (d.WikiUrl    is not null) links.Add($"{L("content.link_wiki")}: {d.WikiUrl}");
        if (d.DiscordUrl is not null) links.Add($"{L("content.link_discord")}: {d.DiscordUrl}");
        if (links.Count > 0)
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(string.Join("  |  ", links))}[/]");
    }

    private static string StripMarkdown(string md)
    {
        if (md.Length > 2000) md = md[..2000] + "\n[...]";

        var lines = md.Split('\n');
        var result = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("### "))      line = line[4..];
            else if (line.StartsWith("## "))  line = line[3..];
            else if (line.StartsWith("# "))   line = line[2..];
            if (line is "---" or "***" or "___") line = "─────────────────────";
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\*{1,3}(.+?)\*{1,3}", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"`(.+?)`", "$1");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[(.+?)\]\(.+?\)", "$1");
            result.AppendLine(line);
        }
        return result.ToString().TrimEnd();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

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

    private static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000     => $"{n / 1_000.0:0.#}K",
        _            => n.ToString()
    };

    private void PrintUsage()
    {
        var l = Label(_type);
        AnsiConsole.MarkupLine($"Usage: [{UiTheme.AccentMarkup}]{l}[/] [grey]<search|install|details|open>[/]");
    }
}


