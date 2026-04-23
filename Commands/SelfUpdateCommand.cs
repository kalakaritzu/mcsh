using System.Runtime.InteropServices;
using Spectre.Console;

namespace McSH.Commands;

public class SelfUpdateCommand
{
    public static readonly string CurrentVersion =
        typeof(SelfUpdateCommand).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";
    private const string ApiUrl = "https://api.github.com/repos/kalakaritzu/mcsh/releases/latest";

    public async Task ExecuteAsync()
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // ── 1. Check GitHub for latest release ───────────────────────────────
        AnsiConsole.MarkupLine($"[dim]{McSH.Services.LanguageService.Get("update.checking")}[/]");

        string latestVersion;
        string downloadUrl;
        string assetName;

        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher");
            var json = await http.GetStringAsync(ApiUrl);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            latestVersion = tag.TrimStart('v');

            if (!Version.TryParse(latestVersion, out var lv) ||
                !Version.TryParse(CurrentVersion, out var cv) ||
                lv <= cv)
            {
                AnsiConsole.MarkupLine(
                    $"[{UiTheme.AccentMarkup}]{McSH.Services.LanguageService.Get("update.up_to_date")}[/] [dim](v{CurrentVersion}).[/]");
                return;
            }

            assetName = isWindows
                ? $"McSH-{latestVersion}.msi"
                : $"McSH-{latestVersion}-linux-x64.tar.gz";

            downloadUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assetsElem))
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameElem) &&
                        nameElem.GetString()?.Equals(assetName, StringComparison.OrdinalIgnoreCase) == true &&
                        asset.TryGetProperty("browser_download_url", out var urlElem))
                    {
                        downloadUrl = urlElem.GetString() ?? string.Empty;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Could not find[/] [{UiTheme.AccentMarkup}]{Markup.Escape(assetName)}[/] [red]in the latest release.[/]");
                return;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to check for updates: {Markup.Escape(ex.Message)}[/]");
            return;
        }

        // ── 2. Confirm ────────────────────────────────────────────────────────
        AnsiConsole.MarkupLine(
            $"Update available: [{UiTheme.AccentMarkup}]v{latestVersion}[/] [dim](you have v{CurrentVersion})[/]");

        if (Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine($"[dim]{McSH.Services.LanguageService.Get("update.run_in_repl")}[/]");
            return;
        }

        if (!AnsiConsole.Confirm(McSH.Services.LanguageService.Get("update.confirm")))
            return;

        // ── 3. Download ───────────────────────────────────────────────────────
        var tempPath = Path.Combine(Path.GetTempPath(), assetName);

        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher");

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[{UiTheme.AccentMarkup}]Downloading {Markup.Escape(assetName)}[/]");

                    using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? -1;

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var file   = File.Create(tempPath);

                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read));
                        downloaded += read;
                        if (total > 0)
                            task.Value = (double)downloaded / total * 100;
                    }
                    task.Value = 100;
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Download failed: {Markup.Escape(ex.Message)}[/]");
            try { File.Delete(tempPath); } catch { }
            return;
        }

        // ── 4. Apply ──────────────────────────────────────────────────────────
        if (isWindows)
        {
            AnsiConsole.MarkupLine($"[dim]{McSH.Services.LanguageService.Get("update.installer")}[/]");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "msiexec",
                Arguments       = $"/i \"{tempPath}\"",
                UseShellExecute = true,
            });
            Environment.Exit(0);
        }
        else
        {
            // Linux: extract the binary from the tar.gz and replace the current executable.
            var extractDir = Path.Combine(Path.GetTempPath(), $"mcsh-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);

            try
            {
                var tar = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "tar",
                    Arguments              = $"-xzf \"{tempPath}\" -C \"{extractDir}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                if (tar is null)
                {
                    AnsiConsole.MarkupLine("[red]Could not start 'tar' — is it installed and on your PATH?[/]");
                    return;
                }
                await tar.WaitForExitAsync();

                var extractedBinary = Path.Combine(extractDir, "mcsh");
                if (!File.Exists(extractedBinary))
                {
                    AnsiConsole.MarkupLine("[red]Could not find mcsh binary in the downloaded archive.[/]");
                    return;
                }

                // Stage to a stable temp path so the sudo fallback path stays valid
                // after we clean up the extract dir below.
                var stagingPath = Path.Combine(Path.GetTempPath(), "mcsh-update");
                File.Copy(extractedBinary, stagingPath, overwrite: true);

                var currentExe = Environment.ProcessPath ?? "/usr/local/bin/mcsh";

                try
                {
#pragma warning disable CA1416
                    File.SetUnixFileMode(stagingPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
                    File.Copy(stagingPath, currentExe, overwrite: true);
                    try { File.Delete(stagingPath); } catch { }
                    AnsiConsole.MarkupLine(
                        $"[{UiTheme.AccentMarkup}]Updated to v{Markup.Escape(latestVersion)}.[/] " +
                        "[dim]Restart McSH to use the new version.[/]");
                }
                catch (UnauthorizedAccessException)
                {
                    AnsiConsole.MarkupLine("[dim]Could not write to installation path (permission denied). Run:[/]");
                    AnsiConsole.MarkupLine(
                        $"[{UiTheme.AccentMarkup}]  sudo cp \"{Markup.Escape(stagingPath)}\" \"{Markup.Escape(currentExe)}\"[/]");
                }
            }
            finally
            {
                try { Directory.Delete(extractDir, recursive: true); } catch { }
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
