using McSH;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using McSH.Models;
using Spectre.Console;

namespace McSH.Services;

/// <summary>
/// Handles Forge and NeoForge installation by running the official installer JAR
/// with --installClient, which handles all patching, downloading, and setup internally.
/// </summary>
public partial class ForgeService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    static ForgeService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.1.0");

    // ── Version resolution ────────────────────────────────────────────────────

    public async Task<string?> GetLatestForgeVersionAsync(string mcVersion)
    {
        try
        {
            var xml = await Http.GetStringAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
            return ParseLatestVersion(xml, mcVersion + "-");
        }
        catch { return null; }
    }

    public async Task<string?> GetLatestNeoForgeVersionAsync(string mcVersion)
    {
        try
        {
            var xml = await Http.GetStringAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
            var prefix = NeoForgePrefix(mcVersion);
            return prefix is null ? null : ParseLatestVersion(xml, prefix);
        }
        catch { return null; }
    }

    private static string? ParseLatestVersion(string xml, string versionPrefix)
    {
        return VersionTagRegex().Matches(xml)
            .Select(m => m.Groups[1].Value)
            .Where(v => v.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();
    }

    private static string? NeoForgePrefix(string mcVersion)
    {
        var p = mcVersion.Split('.');
        return p.Length switch
        {
            >= 3 => $"{p[1]}.{p[2]}.",
            2    => $"{p[1]}.",
            _    => null
        };
    }

    // ── Installation entry point ──────────────────────────────────────────────

    /// <summary>
    /// Downloads and runs the official Forge/NeoForge installer with --installClient.
    /// Returns the version ID and parsed version.json on success, or (null, null) on failure.
    /// </summary>
    public async Task<(string? VersionId, JsonDocument? VersionDoc)> InstallAsync(
        string mcVersion, ModLoader loader)
    {
        var (installerUrl, loaderVersionStr) = await ResolveInstallerUrlAsync(mcVersion, loader);
        if (installerUrl is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not find a {loader} build for Minecraft {Markup.Escape(mcVersion)}.[/]");
            return (null, null);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"mcsh-{Guid.NewGuid():N}.jar");
        try
        {
            AnsiConsole.MarkupLine($"[dim]Downloading {loader} {Markup.Escape(loaderVersionStr!)} installer...[/]");

            using (var response = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file   = File.Create(tempPath);
                await stream.CopyToAsync(file);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Installer download failed:[/] {Markup.Escape(ex.Message)}");
            return (null, null);
        }

        try
        {
            return await RunInstallerAsync(tempPath, loader);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ── Run installer ─────────────────────────────────────────────────────────

    private static async Task<(string? VersionId, JsonDocument? VersionDoc)> RunInstallerAsync(
        string installerJarPath, ModLoader loader)
    {
        // Peek at version.json inside the installer to learn the version ID up front
        string? versionId;
        using (var zip = ZipFile.OpenRead(installerJarPath))
        {
            var versionEntry = zip.GetEntry("version.json");
            if (versionEntry is null)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Installer JAR is missing version.json.[/]");
                return (null, null);
            }
            using var doc = await JsonDocument.ParseAsync(versionEntry.Open());
            versionId = doc.RootElement.TryGetProperty("id", out var idElem)
                ? idElem.GetString()
                : null;
        }

        if (versionId is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not read version ID from installer.[/]");
            return (null, null);
        }

        AnsiConsole.MarkupLine($"[dim]Running {loader} installer — this takes about a minute...[/]");

        // The Forge installer checks for launcher_profiles.json to verify the target is a
        // valid Minecraft install directory. Write a stub if it doesn't exist yet.
        var profilesPath = Path.Combine(PathService.RootDir, "launcher_profiles.json");
        var wroteProfiles = false;
        if (!File.Exists(profilesPath))
        {
            await File.WriteAllTextAsync(profilesPath,
                "{\"profiles\":{},\"selectedProfile\":\"(Default)\",\"clientToken\":\"McSH\",\"authenticationDatabase\":{}}");
            wroteProfiles = true;
        }

        // Run: java -jar <installer.jar> --installClient <McSH root dir>
        // The installer places version.json and libraries under the given root dir,
        // matching our existing directory structure.
        var startInfo = new ProcessStartInfo
        {
            FileName               = "java",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerJarPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(PathService.RootDir);

        Process? process;
        try { process = Process.Start(startInfo); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to launch installer:[/] {Markup.Escape(ex.Message)}");
            return (null, null);
        }

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var exitCode = process.ExitCode;
        process.Dispose();

        if (wroteProfiles)
            try { File.Delete(profilesPath); } catch { }

        if (exitCode != 0)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{loader} installer failed (exit code {exitCode}).[/]");
            var combined = (stdout + "\n" + stderr).Trim();
            if (!string.IsNullOrWhiteSpace(combined))
                foreach (var line in combined.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(10))
                    AnsiConsole.MarkupLine($"[dim]  {Markup.Escape(line.TrimEnd())}[/]");
            return (null, null);
        }

        // Load the version.json the installer wrote to our versions dir
        var versionJsonPath = PathService.VersionJsonPath(versionId);
        if (!File.Exists(versionJsonPath))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Installer succeeded but version.json not found:[/] {Markup.Escape(versionJsonPath)}");
            return (null, null);
        }

        var versionDoc = JsonDocument.Parse(await File.ReadAllTextAsync(versionJsonPath));
        AnsiConsole.MarkupLine($"[dim]{loader} installed as[/] [{UiTheme.AccentMarkup}]{Markup.Escape(versionId)}[/][dim].[/]");
        return (versionId, versionDoc);
    }

    // ── URL resolution ────────────────────────────────────────────────────────

    private async Task<(string? Url, string? VersionStr)> ResolveInstallerUrlAsync(
        string mcVersion, ModLoader loader)
    {
        if (loader == ModLoader.NeoForge)
        {
            var v = await GetLatestNeoForgeVersionAsync(mcVersion);
            if (v is null) return (null, null);
            return ($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{v}/neoforge-{v}-installer.jar", v);
        }
        else
        {
            var v = await GetLatestForgeVersionAsync(mcVersion);
            if (v is null) return (null, null);
            return ($"https://maven.minecraftforge.net/net/minecraftforge/forge/{v}/forge-{v}-installer.jar", v);
        }
    }

    // ── Maven coordinate helper (used by GameLaunchService classpath builder) ──

    /// <summary>
    /// Converts "group:artifact:version[:classifier][@ext]" to a relative path
    /// under the libraries directory.
    /// </summary>
    public static string MavenCoordToRelativePath(string coord)
    {
        var ext  = "jar";
        var atIdx = coord.IndexOf('@');
        if (atIdx >= 0) { ext = coord[(atIdx + 1)..]; coord = coord[..atIdx]; }

        var parts      = coord.Split(':');
        var groupPath  = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact   = parts[1];
        var version    = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;
        var filename   = classifier is not null
            ? $"{artifact}-{version}-{classifier}.{ext}"
            : $"{artifact}-{version}.{ext}";

        return Path.Combine(groupPath, artifact, version, filename);
    }

    [GeneratedRegex(@"<version>([^<]+)</version>")]
    private static partial Regex VersionTagRegex();
}


