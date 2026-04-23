namespace McSH.Models;

public class AppSettings
{
    public bool AutoCompleteEnabled { get; set; } = true;

    /// <summary>
    /// When enabled, clears the console right before executing a command so each command's output is shown on a fresh screen.
    /// </summary>
    public bool ClearScreenOnCommand { get; set; } = false;

    public bool ShowBannerOnStartup { get; set; } = false;

    /// <summary>Show the ASCII shape to the left of the banner text.</summary>
    public bool ShowBannerShape { get; set; } = false;

    /// <summary>
    /// When enabled, includes full raw HTTP bodies/errors in some failures.
    /// </summary>
    public bool VerboseErrors { get; set; } = false;

    // ── Added options ─────────────────────────────────────────────────────────

    /// <summary>Show the active instance name in the prompt.</summary>
    public bool PromptShowActiveInstance { get; set; } = true;

    /// <summary>Show signed-in player name in the prompt when available.</summary>
    public bool PromptShowPlayerName { get; set; } = false;

    /// <summary>Ask for confirmation before `instance kill`.</summary>
    public bool ConfirmBeforeKill { get; set; } = true;

    /// <summary>Show timestamps when attached to `console`.</summary>
    public bool ConsoleShowTimestamps { get; set; } = false;

    /// <summary>Highlight [WARN] lines in yellow while attached to `console`.</summary>
    public bool ConsoleHighlightWarnings { get; set; } = true;

    // ── UI enhancements (enabled by default) ──────────────────────────────────

    /// <summary>Use a more informative (but still subtle) prompt.</summary>
    public bool UiEnhancedPrompt { get; set; } = true;

    /// <summary>Draw a lightweight header/rule around each command's output.</summary>
    public bool UiCommandFraming { get; set; } = true;

    /// <summary>Use panels for a few key messages.</summary>
    public bool UiPanels { get; set; } = true;

    /// <summary>Show progress bars for downloads.</summary>
    public bool UiDownloadProgress { get; set; } = true;

    // ── Appearance ────────────────────────────────────────────────────────────

    /// <summary>Active color theme. Options: crimson, arctic, forest, amber, violet, steel.</summary>
    public string Theme { get; set; } = "crimson";

    // ── Recent / Jump back in ─────────────────────────────────────────────────

    /// <summary>Automatically back up worlds before launching an instance.</summary>
    public bool AutoBackupBeforeLaunch { get; set; } = false;

    /// <summary>Show recent worlds and servers in the startup banner.</summary>
    public bool ShowRecentOnStartup { get; set; } = true;

    /// <summary>How many recent entries to show on startup (0 = off).</summary>
    public int RecentOnStartupCount { get; set; } = 4;

    // ── Language ──────────────────────────────────────────────────────────────

    /// <summary>UI language code. Supported: "en" (default), "es".</summary>
    public string Language { get; set; } = "en";

    // ── Download / write throttling ───────────────────────────────────────────

    /// <summary>Maximum number of simultaneous file downloads.</summary>
    public int MaxConcurrentDownloads { get; set; } = 4;

    /// <summary>Maximum number of simultaneous file writes to disk.</summary>
    public int MaxConcurrentWrites { get; set; } = 4;

    // ── Java ──────────────────────────────────────────────────────────────────

    /// <summary>User-specified Java executable path. Null = auto-detect.</summary>
    public string? CustomJavaPath { get; set; } = null;
}

