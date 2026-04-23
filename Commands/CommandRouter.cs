using McSH;
using McSH.Services;
using McSH.State;
using Spectre.Console;

namespace McSH.Commands;

/// <summary>
/// Parses raw input lines and dispatches to the correct command handler.
/// </summary>
public class CommandRouter
{
    private readonly AppState _state;
    private readonly AuthService _auth;
    private readonly AuthCommand _authCmd;
    private readonly InstanceCommand _instance;
    private readonly ModCommand _mod;
    private readonly HelpCommand _help;
    private readonly ConsoleCommand _console;
    private readonly SettingsCommand _settings;
    private readonly ContentCommand _resourcePack;
    private readonly ContentCommand _shader;
    private readonly ContentCommand _plugin;
    private readonly ContentCommand _datapack;
    private readonly ModpackCommand _modpack;
    private readonly RecentCommand _recent;
    private readonly JavaCommand _java;
    private readonly SkinCommand _skin;
    private readonly GameLaunchService _launcher;

    public GameLaunchService Launcher => _launcher;

    public CommandRouter(AppState state, AuthService auth)
    {
        _state = state;
        _auth  = auth;

        var store     = new InstanceStore();
        var mojang    = new MojangService();
        var fabric    = new FabricService();
        var forge     = new ForgeService();
        var tracker   = new ProcessTracker();
        var launcher  = new GameLaunchService(tracker, store, mojang, auth, fabric, forge);
        var modStore  = new ModStore();
        var modrinth  = new ModrinthService();
        var mrpack    = new MrpackService(store, modStore);

        _launcher     = launcher;
        _recent       = new RecentCommand(launcher);
        _java         = new JavaCommand();
        _skin         = new SkinCommand(auth);
        _authCmd      = new AuthCommand(auth);
        _instance     = new InstanceCommand(state, store, mojang, tracker, auth, launcher, mrpack, modrinth);
        _mod          = new ModCommand(state, store, modStore, modrinth);
        _modpack      = new ModpackCommand(state, store, modrinth, mrpack);
        _help         = new HelpCommand();
        _console      = new ConsoleCommand(state, tracker);
        _settings     = new SettingsCommand();
        _resourcePack = new ContentCommand(ContentCommand.ContentType.ResourcePack, state, store, modStore, modrinth);
        _shader       = new ContentCommand(ContentCommand.ContentType.Shader,       state, store, modStore, modrinth);
        _plugin       = new ContentCommand(ContentCommand.ContentType.Plugin,       state, store, modStore, modrinth);
        _datapack     = new ContentCommand(ContentCommand.ContentType.Datapack,     state, store, modStore, modrinth);
    }

    public async Task DispatchAsync(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;

        var verb = tokens[0].ToLowerInvariant();
        var args = tokens[1..];

        switch (verb)
        {
            case "instance" or "i" or "ins" or "inst":
                await _instance.ExecuteAsync(args);
                break;

            // Quick-launch alias: "run/launch <name>" → "instance run <name>"
            case "run" or "r" or "launch":
                await _instance.ExecuteAsync(["run", ..args]);
                break;

            case "mod" or "m":
                await _mod.ExecuteAsync(args);
                break;

            case "console":
                await _console.ExecuteAsync();
                break;

            case "auth" or "a" or "login":
                await _authCmd.ExecuteAsync(verb == "login" ? ["login"] : args);
                break;

            case "settings" or "set" or "s":
                _settings.Execute(args);
                break;

            case "resourcepack" or "rp" or "resourcepacks":
                await _resourcePack.ExecuteAsync(args);
                break;

            case "shader" or "shaders":
                await _shader.ExecuteAsync(args);
                break;

            case "plugin" or "pl" or "plugins":
                await _plugin.ExecuteAsync(args);
                break;

            case "datapack" or "dp" or "datapacks":
                await _datapack.ExecuteAsync(args);
                break;

            case "modpack" or "mp":
                await _modpack.ExecuteAsync(args);
                break;

            case "recent":
                await _recent.ExecuteAsync(args);
                break;

            case "java":
                await _java.ExecuteAsync();
                break;

            case "skin" or "skins":
                await _skin.ExecuteAsync(args);
                break;

            case "update":
                await new SelfUpdateCommand().ExecuteAsync();
                break;

            case "restart":
                var exe = System.Environment.ProcessPath;
                if (exe is not null)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = exe,
                        UseShellExecute = true,  // opens a new terminal window
                    });
                    System.Environment.Exit(0);
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]{McSH.Services.LanguageService.Get("restart.no_path")}[/]");
                }
                break;

            case "clear" or "cls":
                Console.Clear();
                break;

            case "version" or "ver":
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{McSH.Services.LanguageService.Get("app.version_display")}[/]");
                AnsiConsole.MarkupLine($"[dim].NET {System.Environment.Version}  |  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}[/]");
                break;

            case "help" or "h" or "?":
                _help.Execute();
                break;

            case "ref" or "c":
                _help.ExecuteRef();
                break;

            default:
                AnsiConsole.MarkupLine(
                    $"[{UiTheme.AccentMarkup}]{McSH.Services.LanguageService.Get("app.unknown_command")}[/] {Markup.Escape(verb)}  {McSH.Services.LanguageService.Get("app.type_help")}");
                break;
        }
    }
}


