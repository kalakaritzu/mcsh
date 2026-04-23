using System.Diagnostics;
using McSH;
using McSH.Services;
using McSH.State;
using Spectre.Console;

namespace McSH.Commands;

/// <summary>
/// Attaches the McSH terminal to the stdout/stderr of the active running instance.
/// Lines containing [ERROR]/[FATAL] are printed red; [WARN] lines are printed yellow.
/// Press ESC to detach and return to the REPL.
/// </summary>
public class ConsoleCommand
{
    private readonly AppState _state;
    private readonly ProcessTracker _tracker;

    public ConsoleCommand(AppState state, ProcessTracker tracker)
    {
        _state   = state;
        _tracker = tracker;
    }

    private static string L(string key) => LanguageService.Get(key);

    public async Task ExecuteAsync()
    {
        _tracker.Purge(); // remove any processes that have already exited

        if (_state.ActiveInstance is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{L("console.no_active")}[/]");
            return;
        }

        var name    = _state.ActiveInstance;
        var process = _tracker.Get(name);

        if (process is null || process.HasExited)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{Markup.Escape(name)} {L("console.not_running")}[/]");
            return;
        }

        // stdout/stderr are already drained to the log file by the launch service,
        // so read from the file instead — same as tail -f.
        var logPath = Path.Combine(PathService.InstanceDir(name), "launcher.log");
        if (!File.Exists(logPath))
        {
            AnsiConsole.MarkupLine($"[dim]{L("console.log_not_found")}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"{L("console.attached")} [{UiTheme.AccentMarkup}]{Markup.Escape(name)}[/]. {L("console.press_esc")}");
        AnsiConsole.Write(new Rule().RuleStyle("grey dim"));

        using var cts = new CancellationTokenSource();
        var tailTask = TailLogAsync(logPath, process, cts.Token);

        while (true)
        {
            // Process exited on its own.
            if (process.HasExited)
            {
                _tracker.Remove(name);
                if (_state.ActiveInstance == name)
                    _state.ActiveInstance = null;

                AnsiConsole.Write(new Rule().RuleStyle("grey dim"));
                AnsiConsole.MarkupLine($"[dim]{L("console.process_exited")}[/]");
                break;
            }

            // User pressed ESC.
            if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
            {
                AnsiConsole.Write(new Rule().RuleStyle("grey dim"));
                AnsiConsole.MarkupLine($"[dim]{L("console.detached")}[/]");
                break;
            }

            await Task.Delay(50);
        }

        await cts.CancelAsync();
        try { await tailTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }

    // ── Log file tailer (tail -f equivalent) ─────────────────────────────────

    private static async Task TailLogAsync(string logPath, Process process, CancellationToken token)
    {
        try
        {
            using var fs     = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);

            // Start from the end — only show output produced after attaching.
            fs.Seek(0, SeekOrigin.End);

            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is not null)
                {
                    Colorize(line);
                }
                else
                {
                    // EOF — poll until more data arrives or process exits.
                    if (process.HasExited) break;
                    await Task.Delay(50, token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static void Colorize(string line)
    {
        if (SettingsService.Current.ConsoleShowTimestamps)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            line = $"[{ts}] {line}";
        }

        var escaped = Markup.Escape(line);

        if (line.Contains("[ERROR]") || line.Contains("[FATAL]"))
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{escaped}[/]");
        else if (SettingsService.Current.ConsoleHighlightWarnings && line.Contains("[WARN]"))
            AnsiConsole.MarkupLine($"[yellow]{escaped}[/]");
        else
            Console.WriteLine(line);
    }
}


