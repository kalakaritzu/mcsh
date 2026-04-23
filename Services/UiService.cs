using McSH;
using Spectre.Console;

namespace McSH.Services;

public static class UiService
{
    public static void WriteCommandFrameHeader(string command)
    {
        if (!SettingsService.Current.UiCommandFraming) return;
        if (SettingsService.Current.ClearScreenOnCommand) return;

        AnsiConsole.Write(new Rule().RuleStyle("grey dim"));
        AnsiConsole.MarkupLine($"[dim]› {Markup.Escape(command)}[/]");
    }

    public static void WriteCommandFrameFooter()
    {
        if (!SettingsService.Current.UiCommandFraming) return;
        if (SettingsService.Current.ClearScreenOnCommand) return;

        AnsiConsole.Write(new Rule().RuleStyle("grey dim"));
        AnsiConsole.WriteLine();
    }

    public static void Success(string title, string message)
    {
        if (!SettingsService.Current.UiPanels)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{Markup.Escape(title)}[/] {message}");
            return;
        }

        var panel = new Panel(new Markup(message))
        {
            Header = new PanelHeader($"[{UiTheme.AccentMarkup}]{Markup.Escape(title)}[/]", Justify.Left),
            Border = BoxBorder.Rounded
        };
        panel.BorderStyle = Style.Parse("grey dim");
        AnsiConsole.Write(panel);
    }

    public static void Info(string title, string message)
    {
        if (!SettingsService.Current.UiPanels)
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(title)}[/] {message}");
            return;
        }

        var panel = new Panel(new Markup(message))
        {
            Header = new PanelHeader($"[dim]{Markup.Escape(title)}[/]", Justify.Left),
            Border = BoxBorder.Rounded
        };
        panel.BorderStyle = Style.Parse("grey dim");
        AnsiConsole.Write(panel);
    }
}



