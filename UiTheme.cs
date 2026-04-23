using Spectre.Console;

namespace McSH;

public record ThemeDefinition(
    string Name,
    string Label,
    string AccentMarkup,
    string AccentStrongMarkup,
    ConsoleColor BannerColor
);

public static class UiTheme
{
    public static readonly IReadOnlyDictionary<string, ThemeDefinition> Themes =
        new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["crimson"] = new("crimson", "Crimson", "red",     "darkred",     ConsoleColor.DarkRed),
            ["arctic"]  = new("arctic",  "Arctic",  "cyan",    "darkcyan",    ConsoleColor.DarkCyan),
            ["forest"]  = new("forest",  "Forest",  "green",   "darkgreen",   ConsoleColor.DarkGreen),
            ["amber"]   = new("amber",   "Amber",   "yellow",  "darkyellow",  ConsoleColor.DarkYellow),
            ["violet"]  = new("violet",  "Violet",  "magenta", "darkmagenta", ConsoleColor.DarkMagenta),
            ["steel"]   = new("steel",   "Steel",   "white",   "grey",        ConsoleColor.White),
        };

    public static string     AccentMarkup       { get; private set; } = "red";
    public static string     AccentStrongMarkup { get; private set; } = "darkred";
    public static Style      SpinnerStyle       { get; private set; } = Style.Parse("red");
    public static ConsoleColor BannerColor      { get; private set; } = ConsoleColor.DarkRed;
    public static string     ActiveTheme        { get; private set; } = "crimson";

    public static void Apply(string themeName)
    {
        if (!Themes.TryGetValue(themeName, out var def))
            def = Themes["crimson"];

        AccentMarkup       = def.AccentMarkup;
        AccentStrongMarkup = def.AccentStrongMarkup;
        SpinnerStyle       = Style.Parse(def.AccentMarkup);
        BannerColor        = def.BannerColor;
        ActiveTheme        = def.Name;
    }
}
