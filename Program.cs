using Figgle;
using McSH;
using McSH.Commands;
using McSH.Services;
using McSH.State;

// ── Encoding ──────────────────────────────────────────────────────────────────
Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── Settings ──────────────────────────────────────────────────────────────────
SettingsService.Load();

// ── Quick-launch mode ─────────────────────────────────────────────────────────
// If arguments are passed on the command line (e.g. "mcsh run main"),
// dispatch them as a single command and exit immediately — no REPL.
if (args.Length > 0)
{
    var quickAuth = new AuthService();
    quickAuth.TryLoadFromDisk();
    _ = quickAuth.TryRefreshInBackgroundAsync();
    var quickState  = new AppState();
    var quickRouter = new CommandRouter(quickState, quickAuth);
    try { await quickRouter.DispatchAsync(string.Join(" ", args)); }
    catch (Exception ex) { Spectre.Console.AnsiConsole.MarkupLine($"[red]Error:[/] {Spectre.Console.Markup.Escape(ex.Message)}"); }
    return;
}

// ── Services ──────────────────────────────────────────────────────────────────
var auth = new AuthService();
auth.TryLoadFromDisk(); // synchronous peek so the banner can show the player name

// ── Banner ────────────────────────────────────────────────────────────────────
if (SettingsService.Current.ShowBannerOnStartup)
{
    Console.Clear();
    McSH.Services.BannerService.Print(auth);
}

// Refresh stored Microsoft/Minecraft tokens in the background.
// Runs concurrently with the REPL — startup is never blocked.
_ = auth.TryRefreshInBackgroundAsync();

static async Task<string?> GetModUpdateNoticeAsync(string? instanceName)
{
    if (instanceName is null) return null;
    try
    {
        var store = new InstanceStore();
        var inst  = store.Get(instanceName);
        if (inst is null || inst.Loader == McSH.Models.ModLoader.Vanilla) return null;

        var modStore     = new ModStore();
        var modrinthMods = modStore.GetAll(instanceName)
            .Where(m => m.Source == "modrinth" && !string.IsNullOrEmpty(m.ProjectId))
            .ToList();
        if (modrinthMods.Count == 0) return null;

        var modrinth = new ModrinthService();
        var loader   = inst.Loader switch
        {
            McSH.Models.ModLoader.Fabric   => "fabric",
            McSH.Models.ModLoader.Quilt    => "quilt",
            McSH.Models.ModLoader.Forge    => "forge",
            McSH.Models.ModLoader.NeoForge => "neoforge",
            _                              => "fabric"
        };

        var tasks = modrinthMods.Select(async mod =>
        {
            var latest = await modrinth.GetCompatibleVersionAsync(
                mod.ProjectId, inst.MinecraftVersion, loader);
            return (mod, latest);
        });

        var results = await Task.WhenAll(tasks);
        var outdated = results
            .Where(r => r.latest is not null &&
                        !r.latest.Id.Equals(r.mod.VersionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (outdated.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        var modUpdateMsg = McSH.Services.LanguageService.Get("app.mod_updates_available")
            .Replace("{0}", outdated.Count.ToString())
            .Replace("{1}", outdated.Count == 1 ? "" : "s");
        sb.AppendLine(
            $"[dim]{modUpdateMsg} [/][{UiTheme.AccentMarkup}]{Spectre.Console.Markup.Escape(instanceName)}[/][dim] → {McSH.Services.LanguageService.Get("app.mod_run_update")}[/]");
        foreach (var (mod, latest) in outdated)
            sb.AppendLine(
                $"[dim]  · {Spectre.Console.Markup.Escape(mod.Name)}  {Spectre.Console.Markup.Escape(mod.VersionNumber)} → {Spectre.Console.Markup.Escape(latest!.VersionNumber)}[/]");
        return sb.ToString().TrimEnd();
    }
    catch { return null; /* offline, API error, etc. */ }
}

static async Task CheckForUpdateAsync()
{
    var currentVersion = SelfUpdateCommand.CurrentVersion;
    const string apiUrl = "https://api.github.com/repos/kalakaritzu/mcsh/releases/latest";
    try
    {
        using var http = new System.Net.Http.HttpClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher");
        var json = await http.GetStringAsync(apiUrl);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var latest = tag.TrimStart('v');
        if (Version.TryParse(latest, out var lv) &&
            Version.TryParse(currentVersion, out var cv) &&
            lv > cv)
        {
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[dim]{McSH.Services.LanguageService.Get("app.update_available")}[/] [{UiTheme.AccentMarkup}]v{latest}[/] [dim]({McSH.Services.LanguageService.Get("app.you_have")} v{currentVersion})[/]");
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[dim]{McSH.Services.LanguageService.Get("app.type_update")}[/]");
            Spectre.Console.AnsiConsole.WriteLine();
        }
    }
    catch { /* silently ignore — no internet, rate limit, etc. */ }
}

// ── State & Router ────────────────────────────────────────────────────────────
var state  = new AppState();
state.LoadSession();   // restore active instance from last session
var router = new CommandRouter(state, auth);

// Check for mod upgrades on the active instance — wait up to 3s so the notice
// appears before the first prompt rather than racing against user input.
var modNoticeTask = GetModUpdateNoticeAsync(state.ActiveInstance);
if (await Task.WhenAny(modNoticeTask, Task.Delay(3000)) == modNoticeTask)
{
    var notice = await modNoticeTask;
    if (notice is not null)
    {
        Spectre.Console.AnsiConsole.MarkupLine(notice);
        Spectre.Console.AnsiConsole.WriteLine();
    }
}

// Check GitHub for a newer McSH release — wait up to 2s so the notice always
// appears before the prompt rather than printing over it mid-session.
var updateTask = CheckForUpdateAsync();
await Task.WhenAny(updateTask, Task.Delay(2000));

// If a session was restored, start pre-warming the active instance immediately
// so that by the time the user types 'i run', all slow work is already done.
if (state.ActiveInstance is not null)
{
    var preWarmInst = new InstanceStore().Get(state.ActiveInstance);
    if (preWarmInst is not null)
        _ = router.Launcher.PreWarmAsync(preWarmInst);
}

// ── Prompt helper (shared with the ReprintPrompt callback below) ──────────────
string WritePrompt()
{
    var showInst   = SettingsService.Current.PromptShowActiveInstance && state.ActiveInstance is not null;
    var showPlayer = SettingsService.Current.PromptShowPlayerName && auth.PlayerName is not null;
    var enhanced   = SettingsService.Current.UiEnhancedPrompt;
    string text;

    if (!enhanced)
    {
        text = showInst || showPlayer
            ? $"> [{(showInst ? state.ActiveInstance : null)}{(showInst && showPlayer ? " | " : "")}{(showPlayer ? auth.PlayerName : null)}]: "
            : "> ";
        Console.Write(text);
    }
    else
    {
        text = "> ";
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(">");
        Console.Write(" ");

        if (showInst || showPlayer)
        {
            Console.Write("[");
            if (showInst)
            {
                Console.ForegroundColor = UiTheme.BannerColor;
                Console.Write(state.ActiveInstance);
                Console.ForegroundColor = ConsoleColor.DarkGray;
            }

            if (showInst && showPlayer)
                Console.Write(" | ");

            if (showPlayer)
            {
                Console.ForegroundColor = UiTheme.BannerColor;
                Console.Write(auth.PlayerName);
                Console.ForegroundColor = ConsoleColor.DarkGray;
            }

            Console.Write("]");
            Console.Write(": ");
        }

        Console.ResetColor();
    }

    return text;
}

// When the game exits (crash or clean) in the background, its Task.Run fires while
// the REPL readline is still blocking.  Without this the crash/exit message appears
// but no prompt follows — the user doesn't know they can type again.
router.Launcher.ReprintPrompt = () =>
{
    Console.WriteLine(); // ensure we start on a clean line after the exit message
    WritePrompt();
};

// ── Command history ───────────────────────────────────────────────────────────
var _commandHistory = new List<string>();

// ── REPL ──────────────────────────────────────────────────────────────────────
while (true)
{
    var promptText = WritePrompt();

    var line = (SettingsService.Current.AutoCompleteEnabled && !Console.IsInputRedirected)
        ? ReadLineWithTabCompletion(promptText, state.ActiveInstance)
        : Console.ReadLine();

    // EOF (Ctrl+Z on Windows / Ctrl+D on Unix)
    if (line is null) break;

    line = line.Trim();
    if (line.Length == 0) continue;

    if (line is "exit" or "quit")
    {
        Console.WriteLine(McSH.Services.LanguageService.Get("app.goodbye"));
        break;
    }

    if (SettingsService.Current.ClearScreenOnCommand)
        Console.Clear();
    else
        UiService.WriteCommandFrameHeader(line);

    if (_commandHistory.Count == 0 || _commandHistory[^1] != line)
        _commandHistory.Add(line);

    try { await router.DispatchAsync(line); }
    catch (Exception ex) { Spectre.Console.AnsiConsole.MarkupLine($"[red]Error:[/] {Spectre.Console.Markup.Escape(ex.Message)}"); }

    if (!SettingsService.Current.ClearScreenOnCommand)
        UiService.WriteCommandFrameFooter();
}

static string[] GetCompletions(string? activeInstance)
{
    // Top-level verbs + common aliases.
    var top =
        new[]
        {
            "help", "h", "?", "ref", "c", "auth", "a", "login", "console",
            "settings", "set", "s",
            "instance", "i", "ins", "inst",
            "mod", "m",
            "resourcepack", "rp", "resourcepacks",
            "shader", "shaders",
            "plugin", "pl", "plugins",
            "datapack", "dp", "datapacks",
            "modpack", "mp",
            "run", "r", "launch",
            "recent",
            "java",
            "skin", "skins",
            "update",
            "restart",
            "clear", "cls",
            "version", "ver",
            "exit", "quit",
        };

    // Subcommands.
    var instanceSubs    = new[] { "list", "ls", "create", "new", "import", "select", "use", "deselect", "run", "stop", "kill", "delete", "rm", "remove", "rename", "mv", "clone", "copy", "duplicate", "open", "info", "export", "backup", "update", "worlds", "crash", "config", "cfg", "prism", "multimc", "mrpack" };
    var modSubs         = new[] { "search", "install", "uninstall", "remove", "rm", "details", "info", "import", "toggle", "open", "list", "ls", "profile" };
    var contentSubs     = new[] { "search", "install", "details", "info", "open" };

    // "instance <sub>"
    var inst = instanceSubs.Select(s => $"instance {s}")
        .Concat(instanceSubs.Select(s => $"i {s}"))
        .Concat(instanceSubs.Select(s => $"ins {s}"))
        .Concat(instanceSubs.Select(s => $"inst {s}"));

    // Instance-name completions for common verbs that take <name>.
    var instanceNameVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "run", "stop", "kill", "select", "use", "delete", "rm", "remove", "open", "info"
    };

    var instNames = GetInstanceNames();
    var instNameCompletions =
        instanceNameVerbs.SelectMany(v => instNames.Select(n => $"instance {v} {n}"))
            .Concat(instanceNameVerbs.SelectMany(v => instNames.Select(n => $"i {v} {n}")))
            .Concat(instanceNameVerbs.SelectMany(v => instNames.Select(n => $"ins {v} {n}")))
            .Concat(instanceNameVerbs.SelectMany(v => instNames.Select(n => $"inst {v} {n}")));

    // "mod <sub>"
    var mods = modSubs.Select(s => $"mod {s}")
        .Concat(modSubs.Select(s => $"m {s}"));

    // "resourcepack / shader / plugin <sub>"
    var content = contentSubs.Select(s => $"resourcepack {s}")
        .Concat(contentSubs.Select(s => $"rp {s}"))
        .Concat(contentSubs.Select(s => $"resourcepacks {s}"))
        .Concat(contentSubs.Select(s => $"shader {s}"))
        .Concat(contentSubs.Select(s => $"shaders {s}"))
        .Concat(contentSubs.Select(s => $"plugin {s}"))
        .Concat(contentSubs.Select(s => $"pl {s}"))
        .Concat(contentSubs.Select(s => $"plugins {s}"));

    // "auth <sub>"
    var authSubs = new[] { "login", "logout", "status", "accounts", "switch", "remove", "whoami" };
    var auth = authSubs.Select(s => $"auth {s}")
        .Concat(authSubs.Select(s => $"a {s}"));

    // "modpack <sub>"
    var modpackSubs = new[] { "search", "install", "update" };
    var modpacks = modpackSubs.Select(s => $"modpack {s}")
        .Concat(modpackSubs.Select(s => $"mp {s}"));

    // "settings <sub>"
    var settingsSubs = new[] { "show", "reset", "theme", "language", "recent" };
    var themeNames   = new[] { "crimson", "arctic", "forest", "amber", "violet", "steel" };
    var settings = settingsSubs.Select(s => $"settings {s}")
        .Concat(settingsSubs.Select(s => $"set {s}"))
        .Concat(settingsSubs.Select(s => $"s {s}"))
        .Concat(themeNames.Select(t => $"settings theme {t}"))
        .Concat(themeNames.Select(t => $"set theme {t}"))
        .Concat(themeNames.Select(t => $"s theme {t}"));

    // When an instance is active, encourage console usage.
    var contextual = activeInstance is not null ? new[] { "console" } : Array.Empty<string>();

    // "recent <sub>"
    var recentSubs = new[] { "run" };
    var recent = recentSubs.Select(s => $"recent {s}");

    // "run/launch <name>" / "r <name>" quick-launch completions.
    var runCompletions = instNames.Select(n => $"run {n}")
        .Concat(instNames.Select(n => $"r {n}"))
        .Concat(instNames.Select(n => $"launch {n}"));

    return top.Concat(inst).Concat(instNameCompletions).Concat(runCompletions).Concat(recent).Concat(mods).Concat(modpacks).Concat(content).Concat(auth).Concat(settings).Concat(contextual)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string[] GetInstanceNames()
{
    try
    {
        var store = new InstanceStore();
        return store.GetAll()
            .Select(i => i.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    catch
    {
        return [];
    }
}

string? ReadLineWithTabCompletion(string prompt, string? activeInstance)
{
    var completions = GetCompletions(activeInstance);
    var buffer = new System.Text.StringBuilder();

    string[] matches = [];
    var matchIndex = -1;
    var lastPrefix = "";

    // History: navigate with ↑/↓
    var historyIndex = _commandHistory.Count; // points past the end = "new input"
    string? savedBuffer = null;

    while (true)
    {
        var keyInfo = Console.ReadKey(intercept: true);

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return buffer.ToString();
        }

        if (keyInfo.Key == ConsoleKey.Z && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Console.WriteLine();
            return null;
        }

        if (keyInfo.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length > 0)
            {
                buffer.Length--;
                Console.Write("\b \b");
            }
            matches = []; matchIndex = -1; lastPrefix = "";
            continue;
        }

        if (keyInfo.Key == ConsoleKey.UpArrow)
        {
            if (_commandHistory.Count == 0) continue;
            if (historyIndex == _commandHistory.Count)
                savedBuffer = buffer.ToString(); // save current input
            if (historyIndex > 0)
            {
                historyIndex--;
                ReplaceBuffer(buffer, _commandHistory[historyIndex]);
            }
            matches = []; matchIndex = -1; lastPrefix = "";
            continue;
        }

        if (keyInfo.Key == ConsoleKey.DownArrow)
        {
            if (historyIndex < _commandHistory.Count - 1)
            {
                historyIndex++;
                ReplaceBuffer(buffer, _commandHistory[historyIndex]);
            }
            else if (historyIndex < _commandHistory.Count)
            {
                historyIndex = _commandHistory.Count;
                ReplaceBuffer(buffer, savedBuffer ?? "");
            }
            matches = []; matchIndex = -1; lastPrefix = "";
            continue;
        }

        if (keyInfo.Key == ConsoleKey.Tab)
        {
            var prefix = buffer.ToString();
            if (!prefix.Equals(lastPrefix, StringComparison.Ordinal))
            {
                matches = completions
                    .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                matchIndex = -1;
                lastPrefix = prefix;
            }
            if (matches.Length == 0) continue;
            matchIndex = (matchIndex + 1) % matches.Length;
            ReplaceBuffer(buffer, matches[matchIndex]);
            continue;
        }

        if (keyInfo.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow
            or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.PageUp or ConsoleKey.PageDown)
            continue;

        var ch = keyInfo.KeyChar;
        if (ch == '\0') continue;

        buffer.Append(ch);
        Console.Write(ch);
        matches = []; matchIndex = -1; lastPrefix = "";
        historyIndex = _commandHistory.Count; // any new char resets history position
    }
}

void ReplaceBuffer(System.Text.StringBuilder buffer, string newText)
{
    var current = buffer.ToString();
    if (current.Length > 0)
    {
        Console.Write(new string('\b', current.Length));
        Console.Write(new string(' ', current.Length));
        Console.Write(new string('\b', current.Length));
    }
    buffer.Clear();
    buffer.Append(newText);
    Console.Write(newText);
}
