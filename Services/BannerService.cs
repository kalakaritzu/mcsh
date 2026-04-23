using McSH.Commands;

namespace McSH.Services;

/// <summary>
/// Renders the McSH startup banner: large themed icon on the left,
/// logo + stats + recent list on the right, side by side.
/// </summary>
public static class BannerService
{
    // ── Left icon art ─────────────────────────────────────────────────────────

    private static readonly string[] Icon =
    [
        "        .",
        "     .%%%%%.",
        "    ii%%%%%%%%.",
        "    iiiii%%%%%%%%.",
        "    iiiiiiii%%%%%%%%.",
        "    iiiiiiiiiii%%%%%%%%.",
        "    iiiii:::iiiiii%%%%%%%%.",
        "    iiiii:::::'iiiiii%%%%%%%%.",
        "    iiiii:::::  iiiiiiii%%%%%%%%.",
        "    iiiii:::::    iii%%%%%%%:::::",
        "    iiiii:::::   ii%%%%%%%:::::::",
        "    iiiii::::: ii%%%%%%%:::::::'`",
        "    iiiii:::::i%%%%%%%:::::::'`",
        "    iiiii:::::%%%:::::::'`",
        "    iiiii::::::::::::'`",
        "    iiiii:::::::::'`",
        "    iiiii::::::'`",
        "    iiiii:::'`",
        "    iiiii'`",
    ];

    private const int IconWidth = 42; // visual column width (all chars are ASCII)

    // ── ANSI helpers ─────────────────────────────────────────────────────────

    private static string Rgb(int r, int g, int b)   => $"\x1B[38;2;{r};{g};{b}m";
    private const  string Reset = "\x1B[0m";
    private const  string Dim   = "\x1B[90m";   // dark gray
    private const  string White = "\x1B[97m";   // bright white

    // ── Theme gradient: (darkR, darkG, darkB, brightR, brightG, brightB) ─────

    private static (int r1, int g1, int b1, int r2, int g2, int b2) ThemeGradient() =>
        UiTheme.ActiveTheme switch
        {
            "arctic"  => (  0,  48,  84,  60, 255, 255),
            "forest"  => (  0,  66,   0,  72, 240,  72),
            "amber"   => ( 96,  72,   0, 255, 252,  60),
            "violet"  => ( 60,   0,  84, 222,  66, 255),
            "steel"   => ( 78,  78,  78, 252, 252, 252),
            _         => ( 96,   0,   0, 255,  60,  60),  // crimson (default)
        };

    private static string GradColor(int row, int totalRows)
    {
        var (r1, g1, b1, r2, g2, b2) = ThemeGradient();
        double t = totalRows <= 1 ? 1.0 : (double)row / (totalRows - 1);
        return Rgb(
            Math.Min(255, (int)((r1 + (r2 - r1) * t) * 1.3)),
            Math.Min(255, (int)((g1 + (g2 - g1) * t) * 1.3)),
            Math.Min(255, (int)((b1 + (b2 - b1) * t) * 1.3)));
    }

    // ── Build right-column lines ──────────────────────────────────────────────

    private static List<string> BuildRight(AuthService auth)
    {
        var (r1, g1, b1, r2, g2, b2) = ThemeGradient();
        string accent = Rgb(r2, g2, b2);

        var lines = new List<string>();

        // Filled block logo — gradient applied per row, dark → bright
        string[] logo =
        [
            "███╗   ███╗ ██████╗███████╗██╗  ██╗",
            "████╗ ████║██╔════╝██╔════╝██║  ██║",
            "██╔████╔██║██║     ███████╗███████║",
            "██║╚██╔╝██║██║     ╚════██║██╔══██║",
            "██║ ╚═╝ ██║╚██████╗███████║██║  ██║",
            "╚═╝     ╚═╝ ╚═════╝╚══════╝╚═╝  ╚═╝",
        ];
        for (int i = 0; i < logo.Length; i++)
        {
            double t   = (double)i / (logo.Length - 1);
            string col = Rgb(
                (int)(r1 + (r2 - r1) * t),
                (int)(g1 + (g2 - g1) * t),
                (int)(b1 + (b2 - b1) * t));
            lines.Add(col + logo[i] + Reset);
        }

        // Subtitle / version
        lines.Add("");
        lines.Add(Dim + LanguageService.Get("app.subtitle") + Reset);

        // Signed-in player name
        if (auth.IsAuthenticated && auth.PlayerName is not null)
            lines.Add(Dim + $"  {LanguageService.Get("app.signed_in_as")} "
                           + accent + auth.PlayerName + Reset);

        // Instance count
        try
        {
            var count = new InstanceStore().GetAll().Count;
            if (count > 0)
            {
                var word = count == 1
                    ? LanguageService.Get("app.instance_singular")
                    : LanguageService.Get("app.instances");
                lines.Add(Dim + $"  {count} {word}" + Reset);
            }
        }
        catch { /* never crash the banner */ }

        // Random tip
        string[] tips =
        [
            LanguageService.Get("tip.0"),  LanguageService.Get("tip.1"),
            LanguageService.Get("tip.2"),  LanguageService.Get("tip.3"),
            LanguageService.Get("tip.4"),  LanguageService.Get("tip.5"),
            LanguageService.Get("tip.6"),  LanguageService.Get("tip.7"),
            LanguageService.Get("tip.8"),  LanguageService.Get("tip.9"),
            LanguageService.Get("tip.10"), LanguageService.Get("tip.11"),
            LanguageService.Get("tip.12"), LanguageService.Get("tip.13"),
        ];
        lines.Add(Dim + "  " + tips[Random.Shared.Next(tips.Length)] + Reset);
        lines.Add("");

        // Recent list
        if (SettingsService.Current.ShowRecentOnStartup
            && SettingsService.Current.RecentOnStartupCount > 0)
        {
            try
            {
                var all     = new RecentService().GetRecent();
                var entries = all.Take(SettingsService.Current.RecentOnStartupCount).ToList();
                if (entries.Count > 0)
                {
                    // "Jump back in" header — white, not themed
                    lines.Add(White + "  " + LanguageService.Get("recent.header") + Reset);
                    lines.Add("");

                    int nameW = Math.Max(4, entries.Max(e => e.DisplayName.Length));
                    int instW = Math.Max(8, entries.Max(e => e.InstanceName.Length));

                    for (int i = 0; i < entries.Count; i++)
                    {
                        var e    = entries[i];
                        var num  = $"{i + 1}".PadLeft(2);
                        var name = e.DisplayName.PadRight(nameW);
                        var type = (e.IsServer
                            ? LanguageService.Get("recent.multiplayer")
                            : LanguageService.Get("recent.singleplayer")).PadRight(12);
                        var inst = e.InstanceName.PadRight(instW);
                        var lp   = e.IsServer || e.LastPlayed == DateTime.MinValue
                            ? "—"
                            : RecentService.RelativeTime(e.LastPlayed);

                        var typeColor = e.IsServer ? Dim : accent;
                        lines.Add(
                            Dim       + $"  {num}  " + Reset +
                            White     + name + "  "  + Reset +
                            typeColor + type          + Reset +
                            Dim       + $"  {inst}  {lp}" + Reset);
                    }

                    lines.Add("");
                    lines.Add(Dim + "  " + LanguageService.Get("recent.run_hint") + Reset);
                }
            }
            catch { /* never crash the banner */ }
        }

        return lines;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public static void Print(AuthService auth)
    {
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine();

        var right     = BuildRight(auth);
        int totalRows = Math.Max(Icon.Length, right.Count);

        for (int i = 0; i < totalRows; i++)
        {
            var iconLine  = i < Icon.Length  ? Icon[i]  : "";
            var rightLine = i < right.Count  ? right[i] : "";

            Console.Write(GradColor(i, totalRows));
            Console.Write(iconLine.PadRight(IconWidth));
            Console.Write(Reset);
            Console.WriteLine(rightLine);
        }

        Console.WriteLine();
    }
}
