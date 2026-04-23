using McSH;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using McSH.Models;
using Spectre.Console;

namespace McSH.Services;

/// <summary>
/// Imports and exports Modrinth .mrpack files.
/// </summary>
public class MrpackService
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    static MrpackService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.1.0");

    private readonly InstanceStore _store;
    private readonly ModStore      _mods;

    public MrpackService(InstanceStore store, ModStore mods)
    {
        _store = store;
        _mods  = mods;
    }

    public async Task<string?> ImportAsync(string mrpackPath, int ramMb,
        string? modrinthProjectId = null, string? modrinthVersionId = null, string? modrinthVersionNumber = null)
    {
        if (!File.Exists(mrpackPath))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]File not found:[/] {Markup.Escape(mrpackPath)}");
            return null;
        }

        // 1. Open ZIP and parse index
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(mrpackPath); }
        catch
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to open .mrpack — is it a valid ZIP?[/]");
            return null;
        }

        using (archive)
        {

        var indexEntry = archive.GetEntry("modrinth.index.json");
        if (indexEntry is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Invalid .mrpack: missing modrinth.index.json[/]");
            return null;
        }

        MrpackIndex index;
        try
        {
            await using var stream = indexEntry.Open();
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(stream, JsonOpts)
                    ?? throw new InvalidOperationException("null result");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to parse modrinth.index.json:[/] {Markup.Escape(ex.Message)}");
            return null;
        }

        // 2. Resolve Minecraft version and mod loader
        if (!index.Dependencies.TryGetValue("minecraft", out var mcVersion) || string.IsNullOrEmpty(mcVersion))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]modrinth.index.json is missing a 'minecraft' dependency.[/]");
            return null;
        }

        ModLoader loader;
        if      (index.Dependencies.ContainsKey("fabric-loader")) loader = ModLoader.Fabric;
        else if (index.Dependencies.ContainsKey("quilt-loader"))  loader = ModLoader.Quilt;
        else if (index.Dependencies.ContainsKey("neoforge"))      loader = ModLoader.NeoForge;
        else if (index.Dependencies.ContainsKey("forge"))         loader = ModLoader.Forge;
        else                                                       loader = ModLoader.Vanilla;

        // 3. Pick a unique instance name
        var baseName = SanitizeName(index.Name);
        var instName = baseName;
        var suffix = 2;
        while (_store.Exists(instName))
            instName = $"{baseName} ({suffix++})";

        // 4. Summary + confirm
        var clientFiles = index.Files
            .Where(f => f.Env?.Client != "unsupported" && f.Downloads.Count > 0)
            .ToList();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Importing:[/] [{UiTheme.AccentMarkup}]{Markup.Escape(index.Name)}[/]");
        AnsiConsole.MarkupLine($"  Minecraft : {mcVersion}");
        AnsiConsole.MarkupLine($"  Loader    : {loader}");
        AnsiConsole.MarkupLine($"  Files     : {clientFiles.Count}");
        AnsiConsole.MarkupLine($"  Instance  : {Markup.Escape(instName)}");
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Proceed?"))
        {
            return null;
        }

        // 5. Create instance
        _store.Save(new Instance
        {
            Name                  = instName,
            MinecraftVersion      = mcVersion,
            Loader                = loader,
            RamMb                 = ramMb,
            CreatedAt             = DateTime.UtcNow,
            ModrinthProjectId     = modrinthProjectId,
            ModrinthVersionId     = modrinthVersionId,
            ModrinthVersionNumber = modrinthVersionNumber,
        });

        var instanceDir = PathService.InstanceDir(instName);

        // 6. Download files
        var failed = new List<string>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[dim]Downloading...[/]", maxValue: clientFiles.Count);

                foreach (var file in clientFiles)
                {
                    task.Description = Markup.Escape(Path.GetFileName(file.Path));

                    var destPath = Path.Combine(
                        instanceDir,
                        file.Path.Replace('/', Path.DirectorySeparatorChar));

                    var sha512 = file.Hashes.GetValueOrDefault("sha512") ?? string.Empty;
                    var ok     = await DownloadFileAsync(file.Downloads[0], destPath, sha512);

                    if (!ok)
                    {
                        failed.Add(file.Path);
                    }
                    else if (file.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileName = Path.GetFileName(file.Path);
                        _mods.Add(instName, new ModEntry
                        {
                            Name     = Path.GetFileNameWithoutExtension(fileName),
                            FileName = fileName,
                            Sha512   = sha512,
                            Enabled  = true,
                            Source   = "modrinth"
                        });
                    }

                    task.Increment(1);
                }
            });

        // 7. Extract overrides
        ExtractOverrides(archive, instanceDir, "overrides/");
        ExtractOverrides(archive, instanceDir, "overrides-client/");

        // 8. Report
        AnsiConsole.WriteLine();
        if (failed.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]{failed.Count} file(s) failed to download:[/] {string.Join(", ", failed.Select(f => Markup.Escape(Path.GetFileName(f))))}");

        AnsiConsole.MarkupLine($"Instance [{UiTheme.AccentMarkup}]{Markup.Escape(instName)}[/] imported.");
        return instName;

        } // end using (archive)
    }

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a standards-compliant .mrpack from an existing instance.
    /// Modrinth-sourced mods are listed in the index with their CDN URLs.
    /// Manually imported mods and config files are bundled in overrides/.
    /// Returns the output file path, or null on failure.
    /// </summary>
    public async Task<string?> ExportAsync(Instance instance, string versionId = "1.0.0")
    {
        var instanceDir = PathService.InstanceDir(instance.Name);
        if (!Directory.Exists(instanceDir))
        {
            AnsiConsole.MarkupLine(
                $"[{UiTheme.AccentMarkup}]Instance folder not found:[/] {Markup.Escape(instanceDir)}");
            return null;
        }

        // ── Build modrinth.index.json ──────────────────────────────────────

        var allMods = _mods.GetAll(instance.Name);

        // Mods with a Modrinth origin → go in the index (downloaded by the installer)
        var indexFiles = new List<MrpackFile>();
        // Mods without a download URL → go in overrides/mods/
        var overrideMods = new List<ModEntry>();

        foreach (var mod in allMods.Where(m => m.Enabled))
        {
            if (mod.Source == "modrinth" &&
                !string.IsNullOrEmpty(mod.ProjectId) &&
                !string.IsNullOrEmpty(mod.VersionId) &&
                !string.IsNullOrEmpty(mod.FileName))
            {
                // Construct the canonical Modrinth CDN URL
                var url = $"https://cdn.modrinth.com/data/{mod.ProjectId}/versions/{mod.VersionId}/{Uri.EscapeDataString(mod.FileName)}";

                // Compute SHA-1 from disk for the second hash field
                var filePath = Path.Combine(PathService.ModsDir(instance.Name), mod.FileName);
                var sha1 = File.Exists(filePath)
                    ? await ComputeSha1Async(filePath)
                    : string.Empty;

                indexFiles.Add(new MrpackFile
                {
                    Path      = $"mods/{mod.FileName}",
                    Downloads = [url],
                    FileSize  = File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
                    Hashes    = new Dictionary<string, string>
                    {
                        ["sha512"] = mod.Sha512,
                        ["sha1"]   = sha1
                    }
                });
            }
            else
            {
                overrideMods.Add(mod);
            }
        }

        // Build dependencies block
        var deps = new Dictionary<string, string>
        {
            ["minecraft"] = instance.MinecraftVersion
        };

        // Try to find the loader version from a cached profile JSON filename
        var loaderKey = instance.Loader switch
        {
            ModLoader.Fabric   => "fabric-loader",
            ModLoader.Quilt    => "quilt-loader",
            ModLoader.Forge    => "forge",
            ModLoader.NeoForge => "neoforge",
            _                  => null
        };

        if (loaderKey is not null)
        {
            var loaderVersion = FindLoaderVersion(instance.Loader, instance.MinecraftVersion);
            deps[loaderKey] = loaderVersion ?? "unknown";
        }

        var index = new MrpackIndex
        {
            FormatVersion = 1,
            Game          = "minecraft",
            VersionId     = versionId,
            Name          = instance.Name,
            Files         = indexFiles,
            Dependencies  = deps
        };

        // ── Build ZIP ──────────────────────────────────────────────────────

        Directory.CreateDirectory(PathService.ExportsDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safe      = string.Concat(instance.Name.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var outPath   = Path.Combine(PathService.ExportsDir, $"{safe}-{timestamp}.mrpack");

        AnsiConsole.MarkupLine(
            $"Building .mrpack for [{UiTheme.AccentMarkup}]{Markup.Escape(instance.Name)}[/][dim]...[/]");

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);

            // 1. modrinth.index.json
            var indexJson = JsonSerializer.Serialize(index,
                new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

            var indexEntry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            using (var w = new StreamWriter(indexEntry.Open()))
                w.Write(indexJson);

            // 2. overrides/mods/ — manual mods
            foreach (var mod in overrideMods)
            {
                var src = Path.Combine(PathService.ModsDir(instance.Name), mod.FileName);
                if (!File.Exists(src)) continue;
                zip.CreateEntryFromFile(src, $"overrides/mods/{mod.FileName}", CompressionLevel.Fastest);
            }

            // 3. overrides/config/ — config folder
            var configDir = Path.Combine(instanceDir, "config");
            if (Directory.Exists(configDir))
                AddDirectoryToZip(zip, configDir, instanceDir, "overrides");

            // 4. overrides/ — other loose override files (options.txt, etc.)
            foreach (var file in new[] { "options.txt", "servers.dat", "usercache.json" })
            {
                var src = Path.Combine(instanceDir, file);
                if (File.Exists(src))
                    zip.CreateEntryFromFile(src, $"overrides/{file}", CompressionLevel.Optimal);
            }
        });

        return outPath;
    }

    private static string? FindLoaderVersion(ModLoader loader, string mcVersion)
    {
        // Scan the versions cache directory for a matching loader profile JSON
        // Filenames follow the pattern: fabric-<mcVersion>-<loaderVersion>.json
        if (!Directory.Exists(PathService.VersionsDir)) return null;

        var prefix = loader switch
        {
            ModLoader.Fabric   => $"fabric-{mcVersion}-",
            ModLoader.Quilt    => $"quilt-{mcVersion}-",
            ModLoader.Forge    => $"forge-{mcVersion}-",
            ModLoader.NeoForge => $"neoforge-{mcVersion}-",
            _                  => null
        };

        if (prefix is null) return null;

        var match = Directory.GetFiles(PathService.VersionsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        return match?[prefix.Length..];
    }

    private static async Task<string> ComputeSha1Async(string filePath)
    {
        await using var fs = File.OpenRead(filePath);
        var bytes = await SHA1.HashDataAsync(fs);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AddDirectoryToZip(ZipArchive zip, string dirPath, string rootPath, string zipPrefix)
    {
        foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, $"{zipPrefix}/{relative}", CompressionLevel.Fastest);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void ExtractOverrides(ZipArchive archive, string instanceDir, string prefix)
    {
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relative)) continue;

            var dest = Path.Combine(instanceDir, relative);

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static async Task<bool> DownloadFileAsync(string url, string destPath, string expectedSha512)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var net  = await response.Content.ReadAsStreamAsync())
            await using (var file = File.Create(destPath))
                await net.CopyToAsync(file);

            if (!string.IsNullOrEmpty(expectedSha512))
            {
                await using var fs = File.OpenRead(destPath);
                var hash = Convert.ToHexString(await SHA512.HashDataAsync(fs)).ToLowerInvariant();
                if (!hash.Equals(expectedSha512, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(destPath);
                    return false;
                }
            }

            return true;
        }
        catch
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            return false;
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var result  = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrEmpty(result) ? "Imported Modpack" : result;
    }
}


