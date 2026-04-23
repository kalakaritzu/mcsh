using McSH;
using McSH.Models;
using McSH.Services;
using McSH.State;
using Spectre.Console;

namespace McSH.Commands;

/// <summary>
/// Searches Modrinth for modpacks, installs them as fully-configured instances,
/// and checks installed modpack instances for available updates.
/// </summary>
public class ModpackCommand
{
    private static readonly HttpClient Http = new();

    static ModpackCommand() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.5.0");

    private readonly AppState        _state;
    private readonly InstanceStore   _store;
    private readonly ModrinthService _modrinth;
    private readonly MrpackService   _mrpack;
    private string _lastQuery = "";

    private static string L(string key) => McSH.Services.LanguageService.Get(key);

    public ModpackCommand(AppState state, InstanceStore store, ModrinthService modrinth, MrpackService mrpack)
    {
        _state    = state;
        _store    = store;
        _modrinth = modrinth;
        _mrpack   = mrpack;
    }

    public async Task ExecuteAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "search" or "s":
                await SearchAsync(args[1..]);
                break;

            case "install" or "i" or "get":
                await InstallAsync(args[1..]);
                break;

            case "update" or "u" or "check":
                await UpdateAsync();
                break;

            default:
                AnsiConsole.MarkupLine(
                    $"[{UiTheme.AccentMarkup}]Usage:[/]  modpack search <query>   [dim]·[/]   modpack install <#|slug>   [dim]·[/]   modpack update");
                break;
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private async Task SearchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            if (!string.IsNullOrEmpty(_lastQuery)) { await SearchAsync([_lastQuery]); return; }
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] modpack search <query>");
            return;
        }

        var query = string.Join(" ", args);
        ModSearchHit[]? hits = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("modpack.searching"), async _ =>
            {
                hits = await _modrinth.SearchAsync(query, mcVersion: null, loader: null, projectType: "modpack");
            });

        if (hits is null || hits.Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]No modpacks found.[/]");
            return;
        }

        _state.LastModpackHits = hits;
        _lastQuery = query;

        var table = new Table()
            .BorderStyle(Style.Parse("grey dim"))
            .AddColumn(new TableColumn("[dim]#[/]").RightAligned())
            .AddColumn(L("modpack.col_name"))
            .AddColumn(L("modpack.col_author"))
            .AddColumn(new TableColumn(L("modpack.col_downloads")).RightAligned())
            .AddColumn(L("modpack.col_description"));

        for (var i = 0; i < hits.Length; i++)
        {
            var h    = hits[i];
            var desc = h.Description.Length > 62
                ? h.Description[..59] + "..."
                : h.Description;
            var dl = h.Downloads >= 1_000_000
                ? $"{h.Downloads / 1_000_000.0:0.#}M"
                : h.Downloads >= 1_000
                    ? $"{h.Downloads / 1_000}K"
                    : h.Downloads.ToString();

            table.AddRow(
                $"[dim]{i + 1}[/]",
                $"[bold]{Markup.Escape(h.Title)}[/]",
                Markup.Escape(h.Author),
                dl,
                $"[dim]{Markup.Escape(desc)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]{L("modpack.install_hint")}[/]");
    }

    // ── Install ───────────────────────────────────────────────────────────────

    private async Task InstallAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Usage:[/] modpack install <#|slug>");
            return;
        }

        // Resolve project from search-result number or slug.
        string projectId;
        string projectTitle;

        if (int.TryParse(args[0], out var idx))
        {
            if (_state.LastModpackHits.Length == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[{UiTheme.AccentMarkup}]No recent search results.[/] Run 'modpack search <query>' first.");
                return;
            }

            if (idx < 1 || idx > _state.LastModpackHits.Length)
            {
                AnsiConsole.MarkupLine(
                    $"[{UiTheme.AccentMarkup}]Pick a number between 1 and {_state.LastModpackHits.Length}.[/]");
                return;
            }

            var hit  = _state.LastModpackHits[idx - 1];
            projectId    = hit.ProjectId;
            projectTitle = hit.Title;
        }
        else
        {
            projectId    = args[0];
            projectTitle = args[0];
        }

        // Fetch all versions.
        ModpackVersionEntry[]? versions = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("modpack.fetching_versions"), async _ =>
            {
                versions = await _modrinth.GetAllModpackVersionsAsync(projectId);
            });

        if (versions is null || versions.Length == 0)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No versions found for this modpack.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(projectTitle)}[/]  [dim]— {versions.Length} version{(versions.Length == 1 ? "" : "s")} available[/]");
        AnsiConsole.WriteLine();

        // Group by MC version (first game version listed per entry), newest MC first.
        var groups = versions
            .GroupBy(v => v.GameVersions.FirstOrDefault() ?? "Unknown")
            .OrderByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build a SelectionPrompt with groups as headers.
        var prompt = new SelectionPrompt<ModpackVersionEntry>()
            .Title(L("modpack.select_version"))
            .PageSize(16)
            .UseConverter(v =>
            {
                var loaders   = v.Loaders.Length > 0
                    ? string.Join(", ", v.Loaders.Select(l => char.ToUpper(l[0]) + l[1..]))
                    : "Vanilla";
                var mcVer     = v.GameVersions.FirstOrDefault() ?? "?";
                var age       = FormatAge(v.DatePublished);
                return $"{Markup.Escape(v.VersionNumber)}  [dim]{Markup.Escape(loaders)}  ·  {Markup.Escape(mcVer)}  ·  {age}[/]";
            });

        foreach (var group in groups)
        {
            prompt.AddChoiceGroup(
                // Dummy sentinel entry used as group header — never selectable.
                new ModpackVersionEntry($"── Minecraft {group.Key} ──", "", [], [], DateTime.MinValue, "", "", ""),
                group.ToArray());
        }

        var selected = AnsiConsole.Prompt(prompt);

        // Skip if the user somehow landed on a group header (empty URL).
        if (string.IsNullOrEmpty(selected.Url))
        {
            AnsiConsole.MarkupLine("[dim]No version selected.[/]");
            return;
        }

        // RAM allocation.
        var ramMb = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title(L("modpack.ram_prompt"))
                .AddChoices(1024, 2048, 3072, 4096, 6144, 8192, 12288, 16384)
                .UseConverter(mb => mb >= 1024 ? $"{mb / 1024} GB" : $"{mb} MB"));

        // Download .mrpack with progress bar.
        var tmpPath    = Path.Combine(Path.GetTempPath(), selected.Filename);
        var downloaded = false;

        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                         new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[bold]Downloading {Markup.Escape(selected.Filename)}[/]");
                    try
                    {
                        using var response = await Http.GetAsync(selected.Url, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();

                        var total = response.Content.Headers.ContentLength ?? -1L;
                        task.MaxValue = total > 0 ? total : 100;

                        await using var stream = await response.Content.ReadAsStreamAsync();
                        await using var file   = File.Create(tmpPath);
                        var  buf      = new byte[81920];
                        long received = 0;
                        int  read;

                        while ((read = await stream.ReadAsync(buf)) > 0)
                        {
                            await file.WriteAsync(buf.AsMemory(0, read));
                            received += read;
                            if (total > 0) task.Value = received;
                        }

                        task.Value = task.MaxValue;
                        downloaded = true;
                    }
                    catch { }
                });

            if (!downloaded)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Download failed.[/]");
                return;
            }

            await _mrpack.ImportAsync(tmpPath, ramMb, projectId, selected.VersionId, selected.VersionNumber);
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    private async Task UpdateAsync()
    {
        var tracked = _store.GetAll()
            .Where(i => i.ModrinthProjectId is not null)
            .ToList();

        if (tracked.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No modpack instances to check. Install a modpack with 'modpack install' first.[/]");
            return;
        }

        // Fetch latest version for every tracked instance in parallel.
        ModpackVersionEntry?[] latest = [];
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(L("modpack.checking_updates"), async _ =>
            {
                var tasks = tracked.Select(i => _modrinth.GetAllModpackVersionsAsync(i.ModrinthProjectId!));
                var results = await Task.WhenAll(tasks);
                latest = results.Select(r => r.FirstOrDefault()).ToArray();
            });

        // Build status table.
        var table = new Table()
            .BorderStyle(Style.Parse("grey dim"))
            .AddColumn(L("modpack.col_instance"))
            .AddColumn(L("modpack.col_installed"))
            .AddColumn(L("modpack.col_latest"))
            .AddColumn(L("modpack.col_status"));

        var toUpdate = new List<(Instance Inst, ModpackVersionEntry Ver)>();

        for (var i = 0; i < tracked.Count; i++)
        {
            var inst       = tracked[i];
            var latestVer  = latest[i];
            var installed  = inst.ModrinthVersionNumber ?? "unknown";

            string status;
            if (latestVer is null)
                status = "[dim]?[/]";
            else if (latestVer.VersionId == inst.ModrinthVersionId)
                status = $"[dim]{L("modpack.status_up_to_date")}[/]";
            else
            {
                status = $"[{UiTheme.AccentMarkup}]{L("modpack.status_update_available")}[/]";
                toUpdate.Add((inst, latestVer));
            }

            table.AddRow(
                Markup.Escape(inst.Name),
                $"[dim]{Markup.Escape(installed)}[/]",
                latestVer is not null ? Markup.Escape(latestVer.VersionNumber) : "[dim]?[/]",
                status);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);

        if (toUpdate.Count == 0)
        {
            AnsiConsole.MarkupLine($"\n[dim]{L("modpack.up_to_date")}[/]");
            return;
        }

        AnsiConsole.WriteLine();

        // Let user choose which instances to update.
        var choices = AnsiConsole.Prompt(
            new MultiSelectionPrompt<(Instance Inst, ModpackVersionEntry Ver)>()
                .Title(L("modpack.select_update"))
                .NotRequired()
                .AddChoices(toUpdate)
                .UseConverter(x =>
                    $"{Markup.Escape(x.Inst.Name)}  [dim]{Markup.Escape(x.Inst.ModrinthVersionNumber ?? "?")} → {Markup.Escape(x.Ver.VersionNumber)}[/]"));

        if (choices.Count == 0) return;

        foreach (var (inst, ver) in choices)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"{L("modpack.updating")} [bold]{Markup.Escape(inst.Name)}[/]  [dim]{Markup.Escape(inst.ModrinthVersionNumber ?? "?")} → {Markup.Escape(ver.VersionNumber)}[/]");

            var tmpPath    = Path.Combine(Path.GetTempPath(), ver.Filename);
            var downloaded = false;

            try
            {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                             new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask($"[bold]{L("modpack.downloading")} {Markup.Escape(ver.Filename)}[/]");
                        try
                        {
                            using var response = await Http.GetAsync(ver.Url, HttpCompletionOption.ResponseHeadersRead);
                            response.EnsureSuccessStatusCode();
                            var total = response.Content.Headers.ContentLength ?? -1L;
                            task.MaxValue = total > 0 ? total : 100;

                            await using var stream = await response.Content.ReadAsStreamAsync();
                            await using var file   = File.Create(tmpPath);
                            var  buf      = new byte[81920];
                            long received = 0;
                            int  read;
                            while ((read = await stream.ReadAsync(buf)) > 0)
                            {
                                await file.WriteAsync(buf.AsMemory(0, read));
                                received += read;
                                if (total > 0) task.Value = received;
                            }
                            task.Value = task.MaxValue;
                            downloaded = true;
                        }
                        catch { }
                    });

                if (!downloaded)
                {
                    AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("modpack.download_failed")}[/]");
                    continue;
                }

                var newName = await _mrpack.ImportAsync(tmpPath, inst.RamMb,
                    inst.ModrinthProjectId, ver.VersionId, ver.VersionNumber);

                // Offer to copy worlds from the old instance to the new one.
                if (newName is not null)
                {
                    var oldSaves = Path.Combine(PathService.InstanceDir(inst.Name), "saves");
                    var newSaves = Path.Combine(PathService.InstanceDir(newName),   "saves");
                    if (Directory.Exists(oldSaves) && Directory.GetDirectories(oldSaves).Length > 0)
                    {
                        AnsiConsole.WriteLine();
                        if (AnsiConsole.Confirm(string.Format(L("modpack.copy_worlds_confirm"), $"[bold]{Markup.Escape(inst.Name)}[/]")))
                        {
                            await AnsiConsole.Status()
                                .Spinner(Spinner.Known.Dots)
                                .SpinnerStyle(UiTheme.SpinnerStyle)
                                .StartAsync(L("modpack.copying_worlds_spinner"), async _ =>
                                {
                                    await Task.Run(() =>
                                    {
                                        Directory.CreateDirectory(newSaves);
                                        foreach (var worldDir in Directory.GetDirectories(oldSaves))
                                        {
                                            var dest = Path.Combine(newSaves, Path.GetFileName(worldDir));
                                            CopyDirectory(worldDir, dest);
                                        }
                                    });
                                });
                            AnsiConsole.MarkupLine($"[dim]{L("modpack.worlds_copied")}[/]");
                        }
                    }
                }
            }
            finally
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static string FormatAge(DateTime published)
    {
        if (published == DateTime.MinValue) return "unknown";
        var age = DateTime.UtcNow - published;
        if (age.TotalDays < 1)   return "today";
        if (age.TotalDays < 2)   return "yesterday";
        if (age.TotalDays < 7)   return $"{(int)age.TotalDays}d ago";
        if (age.TotalDays < 30)  return $"{(int)(age.TotalDays / 7)}w ago";
        if (age.TotalDays < 365) return $"{(int)(age.TotalDays / 30)}mo ago";
        return $"{(int)(age.TotalDays / 365)}y ago";
    }
}
