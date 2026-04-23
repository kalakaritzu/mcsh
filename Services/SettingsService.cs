using System.Text.Json;
using McSH;
using McSH.Models;
using Spectre.Console;

namespace McSH.Services;

public static class SettingsService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            var path = PathService.SettingsPath;
            if (!File.Exists(path))
            {
                Current = new AppSettings();
                Save();
                return;
            }

            var json = File.ReadAllText(path);
            Current = JsonSerializer.Deserialize<AppSettings>(json, Opts) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Current = new AppSettings();
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] failed to load settings, using defaults. {Markup.Escape(ex.Message)}");
        }

        UiTheme.Apply(Current.Theme);
        LanguageService.Load(Current.Language);
    }

    public static void Save()
    {
        Directory.CreateDirectory(PathService.RootDir);
        File.WriteAllText(PathService.SettingsPath, JsonSerializer.Serialize(Current, Opts));
    }

    public static void ResetToDefaults()
    {
        Current = new AppSettings();
        Save();
        UiTheme.Apply(Current.Theme);
    }
}


