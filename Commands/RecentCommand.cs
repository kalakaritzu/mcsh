using McSH.Services;
using Spectre.Console;

namespace McSH.Commands;

public class RecentCommand(GameLaunchService launcher)
{
    private static string L(string key) => LanguageService.Get(key);
    public async Task ExecuteAsync(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            await RunAsync(args);
            return;
        }

        ShowTable();
    }

    // ── Full table (typed manually) ───────────────────────────────────────────

    private static void ShowTable()
    {
        var entries = new RecentService().GetRecent();

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]{L("recent.none")}[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Type[/]")
            .AddColumn("[bold]Instance[/]")
            .AddColumn("[bold]Last Played[/]");

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var type       = TypeMarkup(e.IsServer);
            var lastPlayed = LastPlayedMarkup(e);

            table.AddRow(
                $"[dim]{i + 1}[/]",
                Markup.Escape(e.DisplayName),
                type,
                $"[{UiTheme.AccentMarkup}]{Markup.Escape(e.InstanceName)}[/]",
                lastPlayed
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]  {L("recent.run_hint")}[/]");
    }

    // ── Startup banner display (borderless, limited count) ────────────────────

    public static void PrintStartupList(int limit)
    {
        try
        {
            var all     = new RecentService().GetRecent();
            var entries = limit > 0 ? all.Take(limit).ToList() : all;
            if (entries.Count == 0) return;

            // Column widths
            int nameW  = Math.Max(4, entries.Max(e => e.DisplayName.Length));
            int typeW  = "Singleplayer".Length;
            int instW  = Math.Max(8, entries.Max(e => e.InstanceName.Length));

            Console.ForegroundColor = UiTheme.BannerColor;
            Console.WriteLine("  " + LanguageService.Get("recent.header"));
            Console.ResetColor();
            Console.WriteLine();

            for (int i = 0; i < entries.Count; i++)
            {
                var e    = entries[i];
                var num  = $"{i + 1}".PadLeft(2);
                var name = e.DisplayName.PadRight(nameW);
                var type = e.IsServer ? LanguageService.Get("recent.multiplayer").PadRight(12) : LanguageService.Get("recent.singleplayer");
                var inst = e.InstanceName.PadRight(instW);
                var lp   = e.IsServer || e.LastPlayed == DateTime.MinValue
                    ? "—"
                    : RecentService.RelativeTime(e.LastPlayed);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {num}  ");
                Console.ResetColor();
                Console.Write($"{name}  ");

                if (e.IsServer)
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                else
                    Console.ForegroundColor = UiTheme.BannerColor;
                Console.Write(type);
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {inst}  {lp}");
                Console.ResetColor();
                Console.WriteLine();
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  " + LanguageService.Get("recent.run_hint"));
            Console.ResetColor();
            Console.WriteLine();
        }
        catch { /* never crash the banner */ }
    }

    // ── recent run <#> ────────────────────────────────────────────────────────

    private async Task RunAsync(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var index))
        {
            AnsiConsole.MarkupLine($"[dim]{L("recent.usage")}[/]");
            return;
        }

        var entries = new RecentService().GetRecent();
        if (index < 1 || index > entries.Count)
        {
            AnsiConsole.MarkupLine($"[red]{L("recent.no_entry").Replace("{0}", index.ToString())}[/]");
            return;
        }

        var entry         = entries[index - 1];
        var instanceStore = new InstanceStore();
        var instance      = instanceStore.Get(entry.InstanceName);

        if (instance is null)
        {
            AnsiConsole.MarkupLine($"[red]Instance '{Markup.Escape(entry.InstanceName)}' not found.[/]");
            return;
        }

        string? quickPlayArg = null;
        if (SupportsQuickPlay(instance.MinecraftVersion))
            quickPlayArg = entry.IsServer ? $"m:{entry.FolderOrAddress}" : $"s:{entry.FolderOrAddress}";
        else
            AnsiConsole.MarkupLine(
                $"[dim]Quick play requires Minecraft 1.20+ — launching [/][{UiTheme.AccentMarkup}]{Markup.Escape(instance.Name)}[/][dim] normally.[/]");

        var dest = entry.IsServer
            ? $"server [bold]{Markup.Escape(entry.DisplayName)}[/]"
            : $"world [bold]{Markup.Escape(entry.DisplayName)}[/]";

        AnsiConsole.MarkupLine(
            $"[dim]Launching[/] [{UiTheme.AccentMarkup}]{Markup.Escape(instance.Name)}[/][dim] → {dest}...[/]");

        await launcher.PrepareAndLaunchAsync(instance, quickPlayArg);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TypeMarkup(bool isServer) => isServer
        ? $"[dim]{L("recent.multiplayer")}[/]"
        : $"[{UiTheme.AccentMarkup}]{L("recent.singleplayer")}[/]";

    private static string LastPlayedMarkup(RecentEntry e) =>
        e.IsServer || e.LastPlayed == DateTime.MinValue
            ? "[dim]—[/]"
            : $"[dim]{RecentService.RelativeTime(e.LastPlayed)}[/]";

    private static bool SupportsQuickPlay(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;
        return major > 1 || (major == 1 && minor >= 20);
    }
}
