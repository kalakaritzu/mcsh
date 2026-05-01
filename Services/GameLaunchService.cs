using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using McSH;
using McSH.Models;
using Spectre.Console;

namespace McSH.Services;

/// <summary>
/// Downloads all required Minecraft client files and launches the game process.
/// </summary>
public class GameLaunchService
{
    private static readonly HttpClient Http = new();

    static GameLaunchService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.1.0");

    private readonly ProcessTracker _tracker;
    private readonly InstanceStore  _store;
    private readonly MojangService  _mojang;
    private readonly AuthService    _auth;
    private readonly FabricService  _fabric;
    private readonly ForgeService   _forge;

    /// <summary>
    /// Invoked after the game exits (clean or crash) so Program.cs can reprint the REPL prompt.
    /// Without this, the background exit message prints over the waiting readline and the user
    /// sees no prompt until they press Enter blind.
    /// </summary>
    public Action? ReprintPrompt { get; set; }

    public GameLaunchService(
        ProcessTracker tracker,
        InstanceStore  store,
        MojangService  mojang,
        AuthService    auth,
        FabricService  fabric,
        ForgeService   forge)
    {
        _tracker = tracker;
        _store   = store;
        _mojang  = mojang;
        _auth    = auth;
        _fabric  = fabric;
        _forge   = forge;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public async Task<bool> PrepareAndLaunchAsync(Instance instance, string? quickPlayArg = null)
    {
        if (instance.IsServer)
            return await PrepareAndLaunchServerAsync(instance);

        if (SettingsService.Current.AutoBackupBeforeLaunch)
        {
            try
            {
                await AutoBackupWorldsAsync(instance);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]{McSH.Services.LanguageService.Get("launch.backup_failed")} {Markup.Escape(ex.Message)}[/]");
            }
        }
        return await PrepareAndLaunchClientAsync(instance, quickPlayArg);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DEDICATED SERVER
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<bool> PrepareAndLaunchServerAsync(Instance instance)
    {
        var ver     = instance.MinecraftVersion;
        var name    = instance.Name;
        var instDir = PathService.InstanceDir(instance.Name);
        Directory.CreateDirectory(instDir);

        // 1. Fetch version JSON (needed for Java version + server JAR URL)
        JsonDocument? vDoc = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(McSH.Services.LanguageService.Get("launch.preparing"), async _ =>
            {
                vDoc = await _mojang.GetVersionJsonAsync(ver);
            });

        if (vDoc is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not fetch version metadata for {Markup.Escape(ver)}.[/]");
            return false;
        }

        var root = vDoc.RootElement;

        // 2. Ensure correct Java version
        if (root.TryGetProperty("javaVersion", out var jv) &&
            jv.TryGetProperty("majorVersion", out var reqVerEl))
        {
            if (!await EnsureJavaAsync(reqVerEl.GetInt32()))
                return false;
        }
        else
        {
            if (!await EnsureJavaAsync(21)) return false; // safe default for 1.17+
        }

        // 3. Download server JAR if not already present
        var serverJar = Path.Combine(instDir, "server.jar");
        if (!File.Exists(serverJar))
        {
            if (!root.TryGetProperty("downloads", out var dl) ||
                !dl.TryGetProperty("server", out var serverDl) ||
                !serverDl.TryGetProperty("url", out var urlEl))
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]No server download found for Minecraft {Markup.Escape(ver)}.[/]");
                return false;
            }

            var serverUrl  = urlEl.GetString()!;
            var serverSha1 = serverDl.TryGetProperty("sha1", out var sha1El) ? sha1El.GetString() : null;

            AnsiConsole.MarkupLine($"[dim]Downloading Minecraft {Markup.Escape(ver)} server JAR...[/]");
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"server-{Markup.Escape(ver)}.jar");
                    using var resp = await Http.GetAsync(serverUrl, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? 0;
                    task.MaxValue = total > 0 ? total : 1;

                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    await using var file   = File.Create(serverJar);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read));
                        task.Increment(read);
                    }
                });

            // Verify SHA-1 if provided
            if (serverSha1 is not null)
            {
                await using var fs = File.OpenRead(serverJar);
                var hash = Convert.ToHexString(
                    await System.Security.Cryptography.SHA1.HashDataAsync(fs))
                    .ToLowerInvariant();
                if (!hash.Equals(serverSha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(serverJar);
                    AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Server JAR hash mismatch — download may be corrupt. Try again.[/]");
                    return false;
                }
            }
        }

        // 4. Write eula.txt (accepted during wizard)
        var eulaPath = Path.Combine(instDir, "eula.txt");
        if (!File.Exists(eulaPath))
            await File.WriteAllTextAsync(eulaPath, "#By using this McSH server instance you agree to the Minecraft EULA.\neula=true\n");

        // 5. Build JVM arguments
        var logPath = Path.Combine(instDir, "server.log");
        var startInfo = new ProcessStartInfo
        {
            FileName               = _javaExe,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = instDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
        };

        startInfo.ArgumentList.Add($"-Xmx{instance.RamMb}M");
        startInfo.ArgumentList.Add($"-Xms{instance.RamMb / 2}M");
        startInfo.ArgumentList.Add("-XX:+UseG1GC");
        startInfo.ArgumentList.Add("-XX:+ParallelRefProcEnabled");
        startInfo.ArgumentList.Add("-XX:MaxGCPauseMillis=200");
        startInfo.ArgumentList.Add("-XX:+UnlockExperimentalVMOptions");
        startInfo.ArgumentList.Add("-XX:+DisableExplicitGC");
        startInfo.ArgumentList.Add("-XX:G1HeapRegionSize=8M");
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add("server.jar");
        startInfo.ArgumentList.Add("--nogui");

        if (!string.IsNullOrWhiteSpace(instance.ExtraJvmArgs))
            foreach (var a in instance.ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                startInfo.ArgumentList.Insert(startInfo.ArgumentList.Count - 2, a); // before -jar

        Process? process;
        try { process = Process.Start(startInfo); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to start server:[/] {Markup.Escape(ex.Message)}");
            return false;
        }
        if (process is null) return false;

        // 6. Drain output to log file, tee to channel for ready-detection
        var logLines = System.Threading.Channels.Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                await using var fw = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
                var sem = new System.Threading.SemaphoreSlim(1, 1);

                async Task DrainAsync(StreamReader reader)
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        await sem.WaitAsync();
                        try   { await fw.WriteLineAsync(line); }
                        finally { sem.Release(); }
                        logLines.Writer.TryWrite(line);
                    }
                }

                await Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
            }
            catch { }
            finally { logLines.Writer.TryComplete(); }
        });

        // 7. Wait for "Done" signal (server finished loading)
        var serverReady = false;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync("Starting server...", async ctx =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    await foreach (var line in logLines.Reader.ReadAllAsync(cts.Token))
                    {
                        if (line.Contains("Done ("))        { serverReady = true; cts.Cancel(); }
                        else if (line.Contains("Preparing")) ctx.Status = "Preparing world...";
                        else if (line.Contains("Loading"))   ctx.Status = "Loading...";
                    }
                }
                catch (OperationCanceledException) { }
            });

        if (!serverReady)
        {
            await Task.Delay(200);
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Server exited during startup (code {process.ExitCode}).[/]");
            if (File.Exists(logPath))
                AnsiConsole.MarkupLine($"[dim]Log: {Markup.Escape(logPath)}[/]");
            return false;
        }

        _tracker.Register(name, process);
        AnsiConsole.MarkupLine($"Server [{UiTheme.AccentMarkup}]{Markup.Escape(ver)}[/] is running.");
        AnsiConsole.MarkupLine($"[dim]Log: {Markup.Escape(logPath)}[/]");
        AnsiConsole.MarkupLine($"[dim]Use 'console' to attach. Use 'instance stop {Markup.Escape(name)}' to stop.[/]");

        var launchTime = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync(); } catch { return; }
            var elapsed  = DateTime.UtcNow - launchTime;
            var duration = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{(int)elapsed.TotalSeconds}s";
            AnsiConsole.MarkupLine(process.ExitCode == 0
                ? $"\n[dim]Server stopped after {duration}.[/]"
                : $"\n[{UiTheme.AccentMarkup}]Server crashed (code {process.ExitCode}, ran {duration}).[/]");
            _tracker.Remove(name);
            ReprintPrompt?.Invoke();
        });

        return true;
    }

    private static async Task AutoBackupWorldsAsync(Instance instance)
    {
        var savesDir = Path.Combine(PathService.InstanceDir(instance.Name), "saves");
        if (!Directory.Exists(savesDir) || !Directory.EnumerateDirectories(savesDir).Any()) return;

        var backupDir  = PathService.InstanceBackupsDir(instance.Name);
        Directory.CreateDirectory(backupDir);
        var stamp      = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupPath = Path.Combine(backupDir, $"{instance.Name}-auto-{stamp}.zip");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(McSH.Services.LanguageService.Get("launch.backing_up"), async _ =>
                await Task.Run(() => ZipFile.CreateFromDirectory(savesDir, backupPath)));

        var size = new FileInfo(backupPath).Length;
        AnsiConsole.MarkupLine($"[dim]{McSH.Services.LanguageService.Get("launch.backup_done")} ({FormatBytes(size)})[/]");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.#} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:0.#} MB",
        >= 1_024         => $"{bytes / 1_024.0:0.#} KB",
        _                => $"{bytes} B",
    };

    // ══════════════════════════════════════════════════════════════════════════
    // CLIENT
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<bool> PrepareAndLaunchClientAsync(Instance instance, string? quickPlayArg = null)
    {
        var ver  = instance.MinecraftVersion;
        var name = instance.Name;

        // 1+2. Fetch version JSON and loader profile in parallel.
        // Passing the cached loader version skips the network call to resolve the latest version,
        // so both fetches usually resolve from disk cache with no network round-trips.
        var cache = _store.GetLaunchCache(name);

        JsonDocument? vDoc        = null;
        JsonDocument? loaderDoc   = null;
        string?       loaderVersion = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync(McSH.Services.LanguageService.Get("launch.preparing"), async _ =>
            {
                var vDocTask = _mojang.GetVersionJsonAsync(ver);

                // For Fabric/Quilt: pass the cached loader version so GetProfileAsync reads from
                // disk without an extra network round-trip to find the current latest version.
                Task<(JsonDocument? Doc, string? LoaderVersion)> loaderTask =
                    instance.Loader is ModLoader.Fabric or ModLoader.Quilt
                        ? _fabric.GetProfileAsync(ver, instance.Loader, cache?.LoaderVersion)
                        : Task.FromResult<(JsonDocument?, string?)>((null, null));

                await Task.WhenAll(vDocTask, loaderTask);

                vDoc = await vDocTask;
                (loaderDoc, loaderVersion) = await loaderTask;
            });

        if (vDoc is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not fetch version metadata for {Markup.Escape(ver)}.[/]");
            return false;
        }

        var root = vDoc.RootElement;

        if (instance.Loader is ModLoader.Forge or ModLoader.NeoForge)
            return await PrepareAndLaunchForgeAsync(instance, root, cache, quickPlayArg);

        if (instance.Loader is ModLoader.Fabric or ModLoader.Quilt && loaderDoc is null)
            AnsiConsole.MarkupLine($"[yellow]Could not fetch loader profile — launching without loader.[/]");

        var loaderRoot = loaderDoc?.RootElement as JsonElement?;

        // 3. Fast-check — skip vanilla downloads if cache matches and client JAR exists
        var clientJar   = PathService.ClientJarPath(ver);
        bool skipDownload = cache is not null
            && cache.MinecraftVersion == ver
            && File.Exists(clientJar);

        if (skipDownload)
        {
            AnsiConsole.MarkupLine("[dim]Fast-check passed. Skipping download.[/]");
        }
        else
        {
            if (!await DownloadClientFilesAsync(instance, root)) return false;

            cache = new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
            _store.SaveLaunchCache(name, cache);
        }

        // 4. Download loader libraries (always checks existence — skips files already present)
        if (loaderRoot.HasValue)
        {
            if (!await DownloadLoaderLibrariesAsync(loaderRoot.Value)) return false;
        }

        // 5. Extract natives — skip if already done for this version
        if (cache?.NativesVersion != ver)
        {
            ExtractNatives(instance, root);
            cache ??= new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
            cache.NativesVersion = ver;
            _store.SaveLaunchCache(name, cache);
        }

        // 6. Get or build classpath — invalidate if loader version changed
        string classpath;
        bool classpathValid = cache?.MinecraftVersion == ver
            && cache.Classpath is not null
            && cache.LoaderVersion == loaderVersion;

        if (classpathValid)
        {
            classpath = cache!.Classpath!;
        }
        else
        {
            classpath = BuildClasspath(instance, root, loaderRoot);
            cache ??= new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
            cache.Classpath     = classpath;
            cache.LoaderVersion = loaderVersion;
            _store.SaveLaunchCache(name, cache);
        }

        // 7. Launch
        return await LaunchClient(instance, root, classpath, loaderRoot, quickPlayArg);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FORGE / NEOFORGE CLIENT
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full prepare-and-launch pipeline for Forge/NeoForge instances.
    /// Uses the win_args.txt argfile generated by the official installer
    /// instead of building a classpath manually.
    /// </summary>
    private async Task<bool> PrepareAndLaunchForgeAsync(
        Instance instance, JsonElement vanillaRoot, LaunchCache? cache, string? quickPlayArg = null)
    {
        var ver  = instance.MinecraftVersion;
        var name = instance.Name;

        // 1. Install Forge/NeoForge if this is the first launch for this instance
        string? loaderVersionId;

        var cachedVersionId = cache?.LoaderVersion;
        var forgeJsonPath   = cachedVersionId is not null
            ? PathService.VersionJsonPath(cachedVersionId)
            : null;

        if (forgeJsonPath is not null && File.Exists(forgeJsonPath))
        {
            loaderVersionId = cachedVersionId;
            AnsiConsole.MarkupLine($"[dim]{instance.Loader} {Markup.Escape(loaderVersionId!)} (cached).[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]First launch: installing {instance.Loader} for Minecraft {Markup.Escape(ver)}...[/]");
            AnsiConsole.MarkupLine("[dim]This takes about a minute — only runs once per instance.[/]");

            var (fvId, fDoc) = await _forge.InstallAsync(ver, instance.Loader);
            fDoc?.Dispose();
            if (fvId is null) return false;

            loaderVersionId = fvId;
            cache ??= new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
            cache.LoaderVersion = loaderVersionId;
            _store.SaveLaunchCache(name, cache);
        }

        // 2. Download vanilla client files (client JAR + libraries + asset index + assets).
        //    The installer already placed patched JARs and Forge libraries, so existence checks
        //    inside DownloadClientFilesAsync will skip those — we mainly need the assets here.
        var clientJar    = PathService.ClientJarPath(ver);
        bool skipDownload = cache is not null
            && cache.MinecraftVersion == ver
            && File.Exists(clientJar);

        if (skipDownload)
        {
            AnsiConsole.MarkupLine("[dim]Fast-check passed. Skipping vanilla download.[/]");
        }
        else
        {
            if (!await DownloadClientFilesAsync(instance, vanillaRoot)) return false;
            cache ??= new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
            cache.MinecraftVersion = ver;
            cache.LastSuccess      = DateTime.UtcNow;
            _store.SaveLaunchCache(name, cache);
        }

        // 3. Extract natives
        if (cache?.NativesVersion != ver)
        {
            ExtractNatives(instance, vanillaRoot);
            cache ??= new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
            cache.NativesVersion = ver;
            _store.SaveLaunchCache(name, cache);
        }

        // 4. Load Forge version.json from disk (installed by ForgeService)
        if (loaderVersionId is null) { AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Loader version ID is missing.[/]"); return false; }

        var forgeVersionJsonPath = PathService.VersionJsonPath(loaderVersionId);
        if (!File.Exists(forgeVersionJsonPath))
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]{instance.Loader} version.json not found.[/]");
            AnsiConsole.MarkupLine("[dim]Delete the instance and create it again to re-run the installer.[/]");
            return false;
        }

        using var forgeDoc = JsonDocument.Parse(await File.ReadAllTextAsync(forgeVersionJsonPath));
        var loaderRoot = forgeDoc.RootElement as JsonElement?;

        // 5. Download any Forge libraries that have a URL (skips already-present files)
        if (!await DownloadLoaderLibrariesAsync(forgeDoc.RootElement)) return false;

        // 6. Build classpath (vanilla libs + Forge libs + client JAR)
        var classpath = BuildClasspath(instance, vanillaRoot, loaderRoot);

        // 7. Launch via the shared client launch path (handles shim detection automatically)
        return await LaunchClient(instance, vanillaRoot, classpath, loaderRoot, quickPlayArg);
    }

    // ── Download all client files ─────────────────────────────────────────────

    private async Task<bool> DownloadClientFilesAsync(Instance instance, JsonElement root)
    {
        var ver = instance.MinecraftVersion;

        // Build list of files to download (client JAR + libraries)
        var batch = new List<(string Label, string Url, string Dest, string? Sha1)>();

        // Client JAR
        if (root.TryGetProperty("downloads", out var dl) &&
            dl.TryGetProperty("client", out var clientDl))
        {
            batch.Add((
                $"client.jar  [dim]({ver})[/]",
                clientDl.GetProperty("url").GetString()!,
                PathService.ClientJarPath(ver),
                clientDl.TryGetProperty("sha1", out var s) ? s.GetString() : null));
        }

        // Libraries and native JARs
        if (root.TryGetProperty("libraries", out var libs))
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!IsLibraryAllowed(lib)) continue;

                if (!lib.TryGetProperty("downloads", out var libDl)) continue;

                // Regular artifact
                if (libDl.TryGetProperty("artifact", out var artifact) &&
                    artifact.TryGetProperty("path", out var pathEl))
                {
                    var dest = Path.Combine(PathService.LibrariesDir,
                        pathEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(dest))
                        batch.Add((
                            Path.GetFileName(dest),
                            artifact.GetProperty("url").GetString()!,
                            dest,
                            artifact.TryGetProperty("sha1", out var sha) ? sha.GetString() : null));
                }

                // Native classifier (platform-specific)
                if (lib.TryGetProperty("natives", out var nativesMap) &&
                    nativesMap.TryGetProperty(PlatformHelper.McOsName, out var winKey) &&
                    libDl.TryGetProperty("classifiers", out var classifiers))
                {
                    var key = winKey.GetString()!.Replace("${arch}", "64");
                    if (classifiers.TryGetProperty(key, out var nativeDl) &&
                        nativeDl.TryGetProperty("path", out var nPathEl))
                    {
                        var dest = Path.Combine(PathService.LibrariesDir,
                            nPathEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));

                        if (!File.Exists(dest))
                            batch.Add((
                                Path.GetFileName(dest),
                                nativeDl.GetProperty("url").GetString()!,
                                dest,
                                nativeDl.TryGetProperty("sha1", out var sha) ? sha.GetString() : null));
                    }
                }
            }
        }

        // Asset index
        string assetIndexId = string.Empty;
        if (root.TryGetProperty("assetIndex", out var ai))
        {
            assetIndexId = ai.GetProperty("id").GetString() ?? ver;
            var indexPath = PathService.AssetIndexPath(assetIndexId);
            if (!File.Exists(indexPath))
                batch.Add((
                    $"asset index  [dim]({assetIndexId})[/]",
                    ai.GetProperty("url").GetString()!,
                    indexPath,
                    ai.TryGetProperty("sha1", out var sha) ? sha.GetString() : null));
        }

        // Download the batch
        if (batch.Count > 0)
        {
            try
            {
                if (SettingsService.Current.UiDownloadProgress)
                {
                    await AnsiConsole.Progress()
                        .AutoClear(false)
                        .Columns(
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new DownloadedColumn(),
                            new TransferSpeedColumn(),
                            new RemainingTimeColumn())
                        .StartAsync(async ctx =>
                        {
                            var sem = new SemaphoreSlim(8);
                            await Task.WhenAll(batch.Select(async item =>
                            {
                                var (label, url, dest, _) = item;
                                var task = ctx.AddTask(label);
                                await sem.WaitAsync();
                                try { await DownloadFileAsync(url, dest, task); }
                                finally { sem.Release(); }
                            }));
                        });
                }
                else
                {
                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(UiTheme.SpinnerStyle)
                        .StartAsync("Downloading files...", async _ =>
                        {
                            var sem = new SemaphoreSlim(8);
                            await Task.WhenAll(batch.Select(async item =>
                            {
                                var (_, url, dest, _) = item;
                                await sem.WaitAsync();
                                try { await DownloadFileAsync(url, dest); }
                                finally { sem.Release(); }
                            }));
                        });
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Download failed:[/] {Markup.Escape(ex.Message)}");
                return false;
            }
        }

        // Asset objects (separate pass — potentially thousands of small files)
        if (!string.IsNullOrEmpty(assetIndexId))
            await DownloadAssetObjectsAsync(assetIndexId);

        return true;
    }

    // ── Loader library download (Maven coordinate format OR vanilla downloads format) ──

    private async Task<bool> DownloadLoaderLibrariesAsync(JsonElement loaderRoot)
    {
        if (!loaderRoot.TryGetProperty("libraries", out var libs)) return true;

        var batch = new List<(string Url, string Dest)>();

        foreach (var lib in libs.EnumerateArray())
        {
            if (!IsLibraryAllowed(lib)) continue;

            string? url  = null;
            string? dest = null;

            if (lib.TryGetProperty("name", out var nameEl) &&
                lib.TryGetProperty("url",  out var urlEl))
            {
                // Maven format (Fabric / Quilt)
                var relativePath = MavenNameToRelativePath(nameEl.GetString()!);
                if (relativePath is null) continue;
                dest = Path.Combine(PathService.LibrariesDir, relativePath);
                if (File.Exists(dest)) continue;
                var repoUrl = urlEl.GetString()!.TrimEnd('/');
                var urlPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                url = $"{repoUrl}/{urlPath}";
            }
            else if (lib.TryGetProperty("downloads", out var dl) &&
                     dl.TryGetProperty("artifact",   out var art) &&
                     art.TryGetProperty("path",       out var pathEl) &&
                     art.TryGetProperty("url",        out var artUrlEl))
            {
                // Vanilla downloads format (Forge / NeoForge version.json)
                var rawUrl = artUrlEl.GetString()!;
                if (string.IsNullOrEmpty(rawUrl)) continue; // bundled in installer, already extracted
                dest = Path.Combine(PathService.LibrariesDir,
                    pathEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dest)) continue;
                url = rawUrl;
            }
            else continue;

            batch.Add((url!, dest!));
        }

        if (batch.Count == 0) return true;

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(UiTheme.SpinnerStyle)
                .StartAsync($"Downloading {batch.Count} loader library/libraries...", async _ =>
                {
                    var sem = new SemaphoreSlim(8);
                    await Task.WhenAll(batch.Select(async item =>
                    {
                        await sem.WaitAsync();
                        try { await DownloadFileAsync(item.Url, item.Dest); }
                        finally { sem.Release(); }
                    }));
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to download loader libraries:[/] {Markup.Escape(ex.Message)}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a Maven coordinate ("net.fabricmc:fabric-loader:0.16.9") to a relative
    /// file path ("net\fabricmc\fabric-loader\0.16.9\fabric-loader-0.16.9.jar").
    /// Returns null if the coordinate is malformed.
    /// </summary>
    private static string? MavenNameToRelativePath(string mavenName)
    {
        var parts = mavenName.Split(':');
        if (parts.Length < 3) return null;

        var groupPath  = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact   = parts[1];
        var version    = parts[2];
        var classifier = parts.Length >= 4 ? parts[3] : null;

        var filename = classifier is not null
            ? $"{artifact}-{version}-{classifier}.jar"
            : $"{artifact}-{version}.jar";

        return Path.Combine(groupPath, artifact, version, filename);
    }

    // ── Asset objects download ────────────────────────────────────────────────

    private async Task DownloadAssetObjectsAsync(string assetIndexId)
    {
        var indexPath = PathService.AssetIndexPath(assetIndexId);
        if (!File.Exists(indexPath)) return;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        if (!doc.RootElement.TryGetProperty("objects", out var objects)) return;

        var missing = new List<(string Hash, long Size)>();
        foreach (var obj in objects.EnumerateObject())
        {
            var hash = obj.Value.GetProperty("hash").GetString()!;
            var size = obj.Value.GetProperty("size").GetInt64();
            if (!File.Exists(PathService.AssetObjectPath(hash)))
                missing.Add((hash, size));
        }

        if (missing.Count == 0) return;

        if (!SettingsService.Current.UiDownloadProgress)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(UiTheme.SpinnerStyle)
                .StartAsync($"Downloading assets ({missing.Count} files)...", async _ =>
                {
                    var sem = new SemaphoreSlim(32);
                    await Task.WhenAll(missing.Select(async item =>
                    {
                        var (hash, _) = item;
                        await sem.WaitAsync();
                        try
                        {
                            var prefix = hash[..2];
                            var url    = $"https://resources.download.minecraft.net/{prefix}/{hash}";
                            var dest   = PathService.AssetObjectPath(hash);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            var bytes = await Http.GetByteArrayAsync(url);
                            await File.WriteAllBytesAsync(dest, bytes);
                        }
                        catch { /* skip individual asset failures — game will fall back */ }
                        finally { sem.Release(); }
                    }));
                });
            return;
        }

        AnsiConsole.MarkupLine($"Downloading [dim]{missing.Count}[/] missing asset(s)...");

        var totalBytes = missing.Sum(m => m.Size);

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn())
            .StartAsync(async ctx =>
            {
                var progressTask = ctx.AddTask($"assets  [dim]({missing.Count} files)[/]");
                progressTask.MaxValue = totalBytes > 0 ? totalBytes : missing.Count;

                // Download all assets concurrently, capped at 32 simultaneous connections.
                var sem = new SemaphoreSlim(32);
                await Task.WhenAll(missing.Select(async item =>
                {
                    var (hash, size) = item;
                    await sem.WaitAsync();
                    try
                    {
                        var prefix = hash[..2];
                        var url    = $"https://resources.download.minecraft.net/{prefix}/{hash}";
                        var dest   = PathService.AssetObjectPath(hash);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        var bytes = await Http.GetByteArrayAsync(url);
                        await File.WriteAllBytesAsync(dest, bytes);
                        progressTask.Increment(totalBytes > 0 ? size : 1);
                    }
                    catch { /* skip individual asset failures — game will fall back */ }
                    finally { sem.Release(); }
                }));
            });
    }

    // ── Native extraction ─────────────────────────────────────────────────────

    private static void ExtractNatives(Instance instance, JsonElement root)
    {
        if (!root.TryGetProperty("libraries", out var libs)) return;

        var nativesDir = PathService.NativesDir(instance.Name);
        Directory.CreateDirectory(nativesDir);

        foreach (var lib in libs.EnumerateArray())
        {
            if (!IsLibraryAllowed(lib)) continue;
            if (!lib.TryGetProperty("natives", out var nativesMap)) continue;
            if (!nativesMap.TryGetProperty(PlatformHelper.McOsName, out var winKey)) continue;

            var key = winKey.GetString()!.Replace("${arch}", "64");

            if (!lib.TryGetProperty("downloads", out var libDl)) continue;
            if (!libDl.TryGetProperty("classifiers", out var classifiers)) continue;
            if (!classifiers.TryGetProperty(key, out var nativeDl)) continue;
            if (!nativeDl.TryGetProperty("path", out var pathEl)) continue;

            var jarPath = Path.Combine(PathService.LibrariesDir,
                pathEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(jarPath)) continue;

            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name.Length == 0) continue; // directory entry
                    if (entry.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;

                    entry.ExtractToFile(Path.Combine(nativesDir, entry.Name), overwrite: true);
                }
            }
            catch { /* skip corrupt native JARs */ }
        }
    }

    // ── Client process launch ─────────────────────────────────────────────────

    private async Task<bool> LaunchClient(
        Instance instance, JsonElement root, string classpath, JsonElement? loaderRoot = null, string? quickPlayArg = null)
    {
        var ver      = instance.MinecraftVersion;
        var name     = instance.Name;
        var instDir  = PathService.InstanceDir(name);

        // Minecraft writes saves, options.txt, logs, etc. here — must exist before launch.
        Directory.CreateDirectory(instDir);

        if (!_auth.IsAuthenticated)
            AnsiConsole.MarkupLine($"[yellow]{McSH.Services.LanguageService.Get("launch.offline")}[/]");

        // Ensure the correct Java version is available, installing it if needed.
        if (root.TryGetProperty("javaVersion", out var jvEl) &&
            jvEl.TryGetProperty("majorVersion", out var reqVerEl))
        {
            var required = reqVerEl.GetInt32();
            if (!await EnsureJavaAsync(required))
                return false;
        }

        var nativesDir = PathService.NativesDir(name);

        // Loader profile overrides mainClass (e.g. Fabric's KnotClient)
        var mainClass =
            (loaderRoot.HasValue && loaderRoot.Value.TryGetProperty("mainClass", out var lmc)
                ? lmc.GetString() : null)
            ?? (root.TryGetProperty("mainClass", out var vmc) ? vmc.GetString() : null)
            ?? "net.minecraft.client.main.Main";

        var assetIndexId = root.TryGetProperty("assetIndex", out var ai) &&
                           ai.TryGetProperty("id", out var aiId)
            ? aiId.GetString()! : ver;

        var subs = new Dictionary<string, string>
        {
            ["${auth_player_name}"]  = _auth.PlayerName    ?? "Player",
            ["${auth_access_token}"] = _auth.MinecraftToken ?? "0",
            ["${auth_uuid}"]         = _auth.PlayerUuid    ?? "0",
            ["${auth_session}"]      = _auth.MinecraftToken ?? "0",
            ["${user_type}"]         = "msa",
            ["${version_name}"]      = ver,
            ["${game_directory}"]    = instDir,
            ["${assets_root}"]       = PathService.AssetsDir,
            ["${assets_index_name}"] = assetIndexId,
            ["${version_type}"]      = "release",
            ["${natives_directory}"] = nativesDir,
            ["${launcher_name}"]     = "McSH",
            ["${launcher_version}"]  = "0.1.0",
            ["${classpath}"]          = classpath,
            // NeoForge / Forge bootstrap module-path substitutions
            ["${library_directory}"]  = PathService.LibrariesDir,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            // Suppress any unrecognised substitutions so they don't leak into args
            ["${resolution_width}"]  = instance.Fullscreen ? "854" : instance.WindowWidth.ToString(),
            ["${resolution_height}"] = instance.Fullscreen ? "480" : instance.WindowHeight.ToString(),
        };

        var jvmArgs  = new List<string>();
        var gameArgs = new List<string>();

        // Memory
        jvmArgs.Add($"-Xmx{instance.RamMb}M");
        jvmArgs.Add($"-Xms{instance.RamMb / 2}M");

        // Performance: Aikar G1GC flags — reduces GC pauses and pre-touches heap pages,
        // resulting in faster initial load and smoother gameplay.
        jvmArgs.Add("-XX:+UseG1GC");
        jvmArgs.Add("-XX:+ParallelRefProcEnabled");
        jvmArgs.Add("-XX:MaxGCPauseMillis=200");
        jvmArgs.Add("-XX:+UnlockExperimentalVMOptions");
        jvmArgs.Add("-XX:+DisableExplicitGC");
        jvmArgs.Add("-XX:+AlwaysPreTouch");
        jvmArgs.Add("-XX:G1NewSizePercent=30");
        jvmArgs.Add("-XX:G1MaxNewSizePercent=40");
        jvmArgs.Add("-XX:G1HeapRegionSize=8M");
        jvmArgs.Add("-XX:G1ReservePercent=20");
        jvmArgs.Add("-XX:G1HeapWastePercent=5");
        jvmArgs.Add("-XX:G1MixedGCCountTarget=4");
        jvmArgs.Add("-XX:InitiatingHeapOccupancyPercent=15");
        jvmArgs.Add("-XX:G1MixedGCLiveThresholdPercent=90");
        jvmArgs.Add("-XX:G1RSetUpdatingPauseTimePercent=5");
        jvmArgs.Add("-XX:SurvivorRatio=32");
        jvmArgs.Add("-XX:+PerfDisableSharedMem");
        jvmArgs.Add("-XX:MaxTenuringThreshold=1");

        if (root.TryGetProperty("arguments", out var arguments))
        {
            // Modern format (1.13+)
            if (arguments.TryGetProperty("jvm", out var jvmEl))
                CollectArgs(jvmEl, jvmArgs, subs);
            if (arguments.TryGetProperty("game", out var gameEl))
                CollectArgs(gameEl, gameArgs, subs);
        }
        else if (root.TryGetProperty("minecraftArguments", out var legacyArgs))
        {
            // Legacy format (pre-1.13)
            jvmArgs.Add($"-Djava.library.path={nativesDir}");
            { jvmArgs.Add("-cp"); jvmArgs.Add(classpath); }

            foreach (var token in legacyArgs.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                gameArgs.Add(ApplySubs(token, subs));
        }

        // Merge loader-specific args (e.g. Fabric's -DFabricMcEmu JVM flag, Forge's --launchTarget)
        if (loaderRoot.HasValue && loaderRoot.Value.TryGetProperty("arguments", out var loaderArguments))
        {
            if (loaderArguments.TryGetProperty("jvm",  out var ljvm))  CollectArgs(ljvm,  jvmArgs,  subs);
            if (loaderArguments.TryGetProperty("game", out var lgame)) CollectArgs(lgame, gameArgs, subs);
        }

        // Extra user-defined JVM args (appended after standard flags)
        if (!string.IsNullOrWhiteSpace(instance.ExtraJvmArgs))
        {
            foreach (var arg in instance.ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                jvmArgs.Add(arg);
        }

        // Window size / fullscreen
        if (instance.Fullscreen)
        {
            gameArgs.Add("--fullscreen");
        }

        // Quick play — drop the player directly into a world or server (Minecraft 1.20+)
        if (!string.IsNullOrEmpty(quickPlayArg))
        {
            if (quickPlayArg.StartsWith("s:"))
            {
                gameArgs.Add("--quickPlaySingleplayer");
                gameArgs.Add(quickPlayArg[2..]);
            }
            else if (quickPlayArg.StartsWith("m:"))
            {
                gameArgs.Add("--quickPlayMultiplayer");
                gameArgs.Add(quickPlayArg[2..]);
            }
        }

        var logPath = Path.Combine(instDir, "launcher.log");

        // Pre-launch hook
        if (!string.IsNullOrWhiteSpace(instance.PreLaunchCommand))
        {
            AnsiConsole.MarkupLine($"[dim]{McSH.Services.LanguageService.Get("launch.pre_launch")}[/]");
            try
            {
                var parts = instance.PreLaunchCommand.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                using var pre = Process.Start(new ProcessStartInfo
                {
                    FileName         = parts[0],
                    Arguments        = parts.Length > 1 ? parts[1] : "",
                    UseShellExecute  = false,
                    WorkingDirectory = instDir,
                });
                pre?.WaitForExit();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]{McSH.Services.LanguageService.Get("launch.pre_launch_failed")} {Markup.Escape(ex.Message)}[/]");
            }
        }

        // Build argument list: [wrapper-args] java [jvm-args] mainClass [game-args]
        var useWrapper = !string.IsNullOrWhiteSpace(instance.WrapperCommand);
        var wrapperParts = useWrapper
            ? instance.WrapperCommand!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        var startInfo = new ProcessStartInfo
        {
            FileName               = useWrapper ? wrapperParts[0] : _javaExe,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = instDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        // If wrapper: [extra wrapper args] java [jvm-args] mainClass [game-args]
        if (useWrapper)
            for (int i = 1; i < wrapperParts.Length; i++)
                startInfo.ArgumentList.Add(wrapperParts[i]);

        if (useWrapper)
            startInfo.ArgumentList.Add(_javaExe);

        foreach (var arg in jvmArgs)
            startInfo.ArgumentList.Add(arg);

        startInfo.ArgumentList.Add(mainClass);

        foreach (var arg in gameArgs)
            startInfo.ArgumentList.Add(arg);

        // Environment variables
        if (!string.IsNullOrWhiteSpace(instance.EnvVars))
        {
            foreach (var line in instance.EnvVars.Split(
                new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                    startInfo.Environment[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        Process? process;
        try { process = Process.Start(startInfo); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Failed to start Java:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Try running 'instance run' again — McSH will re-install Java if needed.[/]");
            return false;
        }

        if (process is null) return false;

        // Drain stdout + stderr to a log file in the background,
        // and tee every line to a channel so the progress display can watch milestones.
        var logLines = System.Threading.Channels.Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                await using var fw = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
                var sem = new System.Threading.SemaphoreSlim(1, 1);

                async Task DrainAsync(StreamReader reader)
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        await sem.WaitAsync();
                        try   { await fw.WriteLineAsync(line); }
                        finally { sem.Release(); }
                        logLines.Writer.TryWrite(line);
                    }
                }

                await Task.WhenAll(
                    DrainAsync(process.StandardOutput),
                    DrainAsync(process.StandardError));
            }
            catch { }
            finally { logLines.Writer.TryComplete(); }
        });

        // Live progress: log milestones drive the status label.
        // On Windows we unblock the moment the LWJGL window handle becomes non-zero.
        // On Linux/macOS MainWindowHandle is always zero, so we use a log line instead.
        var windowReady = false;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync("Starting JVM...", async ctx =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));

                // Read log lines: update status labels and (on Linux) detect window-ready.
                var logTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var line in logLines.Reader.ReadAllAsync(cts.Token))
                        {
                            if      (line.Contains("Setting user:"))             ctx.Status = "Authenticating...";
                            else if (line.Contains("Backend library:"))          ctx.Status = "Initializing renderer...";
                            else if (line.Contains("Initializing game"))         ctx.Status = "Loading game engine...";
                            else if (line.Contains("Reloading ResourceManager")) ctx.Status = "Loading resources...";
                            else if (!PlatformHelper.IsWindows &&
                                     line.Contains("Minecraft finished loading")) { windowReady = true; cts.Cancel(); }
                        }
                    }
                    catch (OperationCanceledException) { }
                });

                if (PlatformHelper.IsWindows)
                {
                    // Poll for the LWJGL window handle — non-zero the instant the window appears.
                    try
                    {
                        while (!process.HasExited)
                        {
                            process.Refresh();
                            if (process.MainWindowHandle != IntPtr.Zero)
                            {
                                windowReady = true;
                                return;
                            }
                            await Task.Delay(100, cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        windowReady = true; // 5-min timeout — assume loaded
                    }
                }
                else
                {
                    // On Linux stdout/stderr don't carry the "finished loading" line —
                    // poll the log file directly until we see it or the process exits.
                    try
                    {
                        while (!process.HasExited && !windowReady)
                        {
                            await Task.Delay(300, cts.Token);
                            if (File.Exists(logPath))
                            {
                                var text = await File.ReadAllTextAsync(logPath, cts.Token);
                                if (text.Contains("Minecraft finished loading"))
                                {
                                    windowReady = true;
                                    return;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { windowReady = true; }
                }
            });

        if (!windowReady)
        {
            await Task.Delay(200); // let the log writer flush the last lines
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Minecraft exited (code {process.ExitCode}).[/]");
            ShowLatestCrashReport(instDir);
            if (File.Exists(logPath))
                AnsiConsole.MarkupLine($"[dim]Error log: {Markup.Escape(logPath)}[/]");
            return false;
        }

        _tracker.Register(name, process);
        AnsiConsole.MarkupLine($"Minecraft [{UiTheme.AccentMarkup}]{Markup.Escape(ver)}[/] {McSH.Services.LanguageService.Get("launch.ready")}");
        AnsiConsole.MarkupLine($"[dim]Log: {Markup.Escape(logPath)}[/]");
        AnsiConsole.MarkupLine($"[dim]{McSH.Services.LanguageService.Get("launch.kill_hint")}[/]");

        var launchTime = DateTime.UtcNow;

        // Monitor in background: when the game exits, show run time and check for crashes.
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync(); } catch { return; }
            var elapsed = DateTime.UtcNow - launchTime;
            var duration = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                : elapsed.TotalMinutes >= 1
                    ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                    : $"{(int)elapsed.TotalSeconds}s";
            if (process.ExitCode == 0)
            {
                AnsiConsole.MarkupLine($"\n[dim]{McSH.Services.LanguageService.Get("launch.closed")} {duration}.[/]");
            }
            else
            {
                await Task.Delay(500);
                AnsiConsole.MarkupLine($"\n[{UiTheme.AccentMarkup}]{McSH.Services.LanguageService.Get("launch.crashed")}[/] [dim](code {process.ExitCode}, ran for {duration}).[/]");
                ShowLatestCrashReport(instDir);
            }

            // Post-exit hook
            if (!string.IsNullOrWhiteSpace(instance.PostExitCommand))
            {
                AnsiConsole.MarkupLine($"[dim]{McSH.Services.LanguageService.Get("launch.post_exit")}[/]");
                try
                {
                    var parts = instance.PostExitCommand.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    using var post = Process.Start(new ProcessStartInfo
                    {
                        FileName        = parts[0],
                        Arguments       = parts.Length > 1 ? parts[1] : "",
                        UseShellExecute = false,
                    });
                    post?.WaitForExit();
                }
                catch { }
            }

            // Clean up the process handle — Remove() disposes the Process object
            // and unregisters the instance so IsRunning() returns false immediately.
            _tracker.Remove(name);

            // Re-draw the REPL prompt so the user knows they're back in the shell.
            // The background task's output lands over the waiting readline; without this
            // the user sees the crash/exit message but no visible prompt below it.
            ReprintPrompt?.Invoke();
        });

        return true;
    }

    private static void ShowLatestCrashReport(string instDir)
    {
        var crashDir = Path.Combine(instDir, "crash-reports");
        if (!Directory.Exists(crashDir)) return;

        var latest = Directory.GetFiles(crashDir, "*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null) return;

        AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Crash report:[/] [dim]{Markup.Escape(latest)}[/]");
        AnsiConsole.WriteLine();

        try
        {
            var lines = File.ReadAllLines(latest);

            // Print the header description (first non-empty lines up to the separator)
            foreach (var line in lines.Take(6))
                if (!string.IsNullOrWhiteSpace(line))
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(line.Trim())}[/]");

            // Find and print the "Description:" line
            var desc = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Description:", StringComparison.OrdinalIgnoreCase));
            if (desc is not null)
                AnsiConsole.MarkupLine($"  [{UiTheme.AccentMarkup}]{Markup.Escape(desc.Trim())}[/]");

            // Find root cause line
            var cause = lines.FirstOrDefault(l => l.TrimStart().StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase))
                     ?? lines.FirstOrDefault(l => l.Contains("Exception") && !l.TrimStart().StartsWith("at "));
            if (cause is not null)
                AnsiConsole.MarkupLine($"  [{UiTheme.AccentMarkup}]{Markup.Escape(cause.Trim())}[/]");
        }
        catch { /* file may be locked */ }

        AnsiConsole.WriteLine();
    }

    // ── Java executable path (may point to a McSH-managed JDK) ───────────────

    /// Full path to java.exe. Set by <see cref="EnsureJavaAsync"/>; defaults to "java" (system PATH).
    private string _javaExe = "java";

    /// Ensures the required Java major version is available, downloading and installing
    /// a managed JDK via Adoptium if the system Java is missing or too old.
    private async Task<bool> EnsureJavaAsync(int required)
    {
        // 0. User-specified custom Java path takes priority over everything.
        var custom = SettingsService.Current.CustomJavaPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            _javaExe = custom;
            AnsiConsole.MarkupLine($"[dim]Using custom Java: {Markup.Escape(custom)}[/]");
            return true;
        }

        // 1. Check for a McSH-managed JDK first (no PATH dependency).
        var managedDir = PathService.ManagedJdkDir(required);
        if (Directory.Exists(managedDir))
        {
            var existing = Directory.GetFiles(managedDir, PlatformHelper.JavaExeName, SearchOption.AllDirectories)
                                    .FirstOrDefault();
            if (existing is not null)
            {
                _javaExe = existing;
                AnsiConsole.MarkupLine($"[dim]Java {required} detected (McSH managed).[/]");
                return true;
            }
        }

        // 2. Check system Java.
        var actual = await GetJavaMajorVersionAsync();

        // Cache may be stale — re-detect if it looks wrong.
        if (actual > 0 && actual != required)
            actual = await DetectJavaMajorVersionAsync();

        if (actual == required)
        {
            _javaExe = "java";
            AnsiConsole.MarkupLine($"[dim]Java {actual} detected (requires {required}).[/]");
            return true;
        }

        // 3. System Java is missing, too old, OR too new (mods can require an exact major version).
        string reason;
        if (actual <= 0)
            reason = "Java is not installed";
        else if (actual < required)
            reason = $"Java {required} is required but Java {actual} is installed";
        else
            reason = $"Java {required} is required but Java {actual} is installed — some mods require an exact version";

        AnsiConsole.MarkupLine($"[yellow]{reason}.[/]");

        if (!AnsiConsole.Confirm($"Install Java {required} automatically?"))
        {
            AnsiConsole.MarkupLine($"[dim]Get Java {required} from: https://adoptium.net[/]");
            return false;
        }

        return await DownloadAndInstallJavaAsync(required);
    }

    /// Downloads the latest Temurin JDK zip from Adoptium for the given major version,
    /// extracts it to the McSH-managed JDK directory, and sets <see cref="_javaExe"/>.
    private async Task<bool> DownloadAndInstallJavaAsync(int major)
    {
        // Resolve download URL from Adoptium API.
        string? downloadUrl = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(UiTheme.SpinnerStyle)
            .StartAsync("Fetching Java download info...", async _ =>
            {
                try
                {
                    var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                                 $"?architecture=x64&image_type=jdk&os={PlatformHelper.AdoptiumOs}&vendor=eclipse";
                    var json = await Http.GetStringAsync(apiUrl);
                    var doc  = JsonDocument.Parse(json);
                    downloadUrl = doc.RootElement[0]
                        .GetProperty("binary")
                        .GetProperty("package")
                        .GetProperty("link")
                        .GetString();
                }
                catch { }
            });

        if (downloadUrl is null)
        {
            AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not fetch Java {major} download URL.[/]");
            AnsiConsole.MarkupLine($"[dim]Install Java {major} manually: https://adoptium.net[/]");
            return false;
        }

        var destDir  = PathService.ManagedJdkDir(major);
        var zipPath  = Path.Combine(Path.GetTempPath(), $"mcsh-jdk{major}{PlatformHelper.JdkArchiveExt}");

        try
        {
            // Download with progress bar.
            var downloaded = false;
            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                         new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[bold]Downloading Java {major}[/]");
                    try
                    {
                        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        var total = response.Content.Headers.ContentLength ?? -1L;
                        task.MaxValue = total > 0 ? total : 100;

                        await using var stream = await response.Content.ReadAsStreamAsync();
                        await using var file   = File.Create(zipPath);
                        var  buffer   = new byte[81920];
                        long received = 0;
                        int  read;
                        while ((read = await stream.ReadAsync(buffer)) > 0)
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read));
                            received += read;
                            if (total > 0) task.Value = received;
                        }
                        task.Value    = task.MaxValue;
                        downloaded    = true;
                    }
                    catch { }
                });

            if (!downloaded)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Download failed.[/]");
                return false;
            }

            // Extract.
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(UiTheme.SpinnerStyle)
                .StartAsync($"Extracting Java {major}...", async _ =>
                {
                    await Task.Run(async () =>
                    {
                        if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
                        Directory.CreateDirectory(destDir);

                        if (PlatformHelper.IsWindows)
                        {
                            ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
                        }
                        else
                        {
                            // Linux/macOS: Adoptium ships .tar.gz
                            using var gz  = new System.IO.Compression.GZipStream(
                                File.OpenRead(zipPath), System.IO.Compression.CompressionMode.Decompress);
                            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gz, destDir, overwriteFiles: true);
                        }
                    });
                });

            // Locate java binary (sits under a versioned subfolder, e.g. jdk-21.0.3+9/bin/java).
            var javaExe = Directory.GetFiles(destDir, PlatformHelper.JavaExeName, SearchOption.AllDirectories)
                                   .FirstOrDefault();
            if (javaExe is null)
            {
                AnsiConsole.MarkupLine($"[{UiTheme.AccentMarkup}]Could not locate Java binary after extraction.[/]");
                return false;
            }

            // On Linux/macOS the extracted binary may not have the execute bit set.
            if (!PlatformHelper.IsWindows)
            {
#pragma warning disable CA1416
                try { System.IO.File.SetUnixFileMode(javaExe, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
                catch { }
#pragma warning restore CA1416
            }

            _javaExe = javaExe;
            AnsiConsole.MarkupLine($"Java [{UiTheme.AccentMarkup}]{major}[/] installed successfully.");
            return true;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    // ── Java version detection ────────────────────────────────────────────────

    private static async Task<int> GetJavaMajorVersionAsync()
    {
        var cachePath = PathService.JavaCachePath;
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<McSH.Models.JavaCache>(
                    await File.ReadAllTextAsync(cachePath));
                if (cached is not null && (DateTime.UtcNow - cached.CachedAt).TotalDays < 30)
                    return cached.MajorVersion;
            }
            catch { }
        }

        var version = await DetectJavaMajorVersionAsync();
        if (version > 0)
        {
            try
            {
                await File.WriteAllTextAsync(cachePath,
                    JsonSerializer.Serialize(new McSH.Models.JavaCache
                    {
                        MajorVersion = version,
                        CachedAt     = DateTime.UtcNow
                    }));
            }
            catch { }
        }
        return version;
    }

    private static async Task<int> DetectJavaMajorVersionAsync()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName              = "java",
                ArgumentList          = { "-version" },
                UseShellExecute       = false,
                RedirectStandardError = true,  // java -version writes to stderr
                CreateNoWindow        = true
            });
            if (p is null) return -1;
            var output = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            p.Dispose();

            // "java version "21.0.3"" or "openjdk version "17.0.2""
            var m = System.Text.RegularExpressions.Regex.Match(output, @"version ""(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var ver))
                return ver;
        }
        catch { }
        return -1;
    }

    // ── Classpath builder ─────────────────────────────────────────────────────

    private static string BuildClasspath(Instance instance, JsonElement root, JsonElement? loaderRoot = null)
    {
        // Keyed by group/artifact (version directory stripped) so loader libs override vanilla
        // duplicates (e.g. Fabric ships asm-9.9 while Minecraft ships asm-9.6).
        var entries = new Dictionary<string, string>();

        // Vanilla libraries
        if (root.TryGetProperty("libraries", out var libs))
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!IsLibraryAllowed(lib)) continue;
                if (!lib.TryGetProperty("downloads", out var libDl)) continue;
                if (!libDl.TryGetProperty("artifact", out var artifact)) continue;
                if (!artifact.TryGetProperty("path", out var pathEl)) continue;

                var fullPath = Path.Combine(PathService.LibrariesDir,
                    pathEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                    entries[ArtifactKey(fullPath)] = fullPath;
            }
        }

        // Loader libraries (Fabric/Quilt: Maven format; Forge/NeoForge: vanilla downloads format)
        // Added after vanilla so loader versions win on conflicts.
        if (loaderRoot.HasValue && loaderRoot.Value.TryGetProperty("libraries", out var loaderLibs))
        {
            foreach (var lib in loaderLibs.EnumerateArray())
            {
                if (!IsLibraryAllowed(lib)) continue;

                string? fullPath = null;

                if (lib.TryGetProperty("name", out var nameEl))
                {
                    // Maven format (Fabric / Quilt)
                    var rel = MavenNameToRelativePath(nameEl.GetString()!);
                    if (rel is not null)
                        fullPath = Path.Combine(PathService.LibrariesDir, rel);
                }
                else if (lib.TryGetProperty("downloads", out var libDl) &&
                         libDl.TryGetProperty("artifact", out var artifact) &&
                         artifact.TryGetProperty("path", out var pathEl))
                {
                    // Vanilla downloads format (Forge / NeoForge)
                    fullPath = Path.Combine(PathService.LibrariesDir,
                        pathEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                }

                if (fullPath is not null && File.Exists(fullPath))
                    entries[ArtifactKey(fullPath)] = fullPath;
            }
        }

        var clientJar = PathService.ClientJarPath(instance.MinecraftVersion);
        if (File.Exists(clientJar))
            entries[ArtifactKey(clientJar)] = clientJar;

        return string.Join(Path.PathSeparator, entries.Values); // ; on Windows, : on Linux/macOS
    }

    /// <summary>
    /// Returns a deduplication key for a library JAR that is version-agnostic but
    /// classifier-aware. Two JARs with the same group/artifact but different versions
    /// (e.g. asm-9.6.jar vs asm-9.9.jar) get the same key → later entry wins.
    /// Two JARs with different classifiers (e.g. lwjgl-glfw-3.3.3.jar vs
    /// lwjgl-glfw-3.3.3-natives-linux.jar) get different keys → both are kept.
    ///
    /// Maven layout: &lt;libsDir&gt;/&lt;group&gt;/&lt;artifact&gt;/&lt;version&gt;/&lt;artifact&gt;-&lt;version&gt;[-classifier].jar
    /// </summary>
    private static string ArtifactKey(string fullPath)
    {
        var rel = Path.GetRelativePath(PathService.LibrariesDir, fullPath)
                      .Replace('\\', '/');
        var parts = rel.Split('/');
        if (parts.Length < 3) return rel;

        var version      = parts[^2];                          // e.g. "9.6"
        var filename     = parts[^1];                          // e.g. "asm-9.6.jar"
        var groupArtifact = string.Join("/", parts[..^2]);     // e.g. "org/ow2/asm/asm"

        // Strip "-<version>" from the filename so asm-9.6.jar and asm-9.9.jar
        // share a key, while asm-9.6.jar and asm-9.6-sources.jar do not.
        var versionToken = "-" + version;
        var idx = filename.IndexOf(versionToken, StringComparison.Ordinal);
        var normalizedFilename = idx >= 0 ? filename.Remove(idx, versionToken.Length) : filename;

        return groupArtifact + "/" + normalizedFilename; // e.g. "org/ow2/asm/asm/asm.jar"
    }

    // ── Rules evaluation ──────────────────────────────────────────────────────

    private static bool IsLibraryAllowed(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules)) return true; // no rules → always include

        bool allowed = false; // default-deny when rules exist
        foreach (var rule in rules.EnumerateArray())
        {
            var action = rule.GetProperty("action").GetString();
            bool matches = true;

            if (rule.TryGetProperty("os", out var os) &&
                os.TryGetProperty("name", out var osName))
                matches = osName.GetString() == PlatformHelper.McOsName;

            if (matches)
                allowed = action == "allow";
        }
        return allowed;
    }

    // ── Argument collection ───────────────────────────────────────────────────

    private static void CollectArgs(
        JsonElement argsEl, List<string> target, Dictionary<string, string> subs)
    {
        foreach (var arg in argsEl.EnumerateArray())
        {
            if (arg.ValueKind == JsonValueKind.String)
            {
                target.Add(ApplySubs(arg.GetString()!, subs));
            }
            else if (arg.ValueKind == JsonValueKind.Object)
            {
                if (!EvalArgRules(arg)) continue;

                if (!arg.TryGetProperty("value", out var val)) continue;

                if (val.ValueKind == JsonValueKind.String)
                    target.Add(ApplySubs(val.GetString()!, subs));
                else if (val.ValueKind == JsonValueKind.Array)
                    foreach (var v in val.EnumerateArray())
                        target.Add(ApplySubs(v.GetString()!, subs));
            }
        }
    }

    private static bool EvalArgRules(JsonElement argObj)
    {
        if (!argObj.TryGetProperty("rules", out var rules)) return true;

        bool allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action  = rule.GetProperty("action").GetString();
            bool matches = true;

            if (rule.TryGetProperty("os", out var os) &&
                os.TryGetProperty("name", out var osName))
                matches = matches && osName.GetString() == PlatformHelper.McOsName;

            if (rule.TryGetProperty("features", out _))
                matches = false; // no optional features are enabled; skip feature-gated args

            if (matches)
                allowed = action == "allow";
        }
        return allowed;
    }

    private static string ApplySubs(string s, Dictionary<string, string> subs)
    {
        foreach (var (k, v) in subs)
            s = s.Replace(k, v);
        return s;
    }

    // ── Pre-warm ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs silently in the background when an instance is selected.
    /// Extracts natives, builds the classpath, and primes the Java version cache
    /// so that <see cref="PrepareAndLaunchClientAsync"/> can skip all of that work.
    /// </summary>
    public async Task PreWarmAsync(Instance instance)
    {
        try
        {
            var ver  = instance.MinecraftVersion;
            var name = instance.Name;

            var vDoc = await _mojang.GetVersionJsonAsync(ver);
            if (vDoc is null) return;
            var root = vDoc.RootElement;

            // Fetch loader profile
            JsonDocument? loaderDoc     = null;
            string?       loaderVersion = null;

            if (instance.Loader is ModLoader.Fabric or ModLoader.Quilt)
            {
                // Pass the cached loader version to skip the network version-resolution call.
                (loaderDoc, loaderVersion) = await _fabric.GetProfileAsync(
                    ver, instance.Loader, _store.GetLaunchCache(name)?.LoaderVersion);
            }
            else if (instance.Loader is ModLoader.Forge or ModLoader.NeoForge)
            {
                // Don't run the full installer in pre-warm — just load from disk if already installed
                var cachedId = _store.GetLaunchCache(name)?.LoaderVersion;
                if (cachedId is not null && File.Exists(PathService.VersionJsonPath(cachedId)))
                {
                    loaderVersion = cachedId;
                    try { loaderDoc = JsonDocument.Parse(await File.ReadAllTextAsync(PathService.VersionJsonPath(cachedId))); }
                    catch { }
                }
            }

            var loaderRoot = loaderDoc?.RootElement as JsonElement?;

            // Download loader libraries (only missing ones)
            if (loaderRoot.HasValue)
                await DownloadLoaderLibrariesAsync(loaderRoot.Value);

            var cache = _store.GetLaunchCache(name);

            if (cache?.NativesVersion != ver)
            {
                ExtractNatives(instance, root);
                cache ??= new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
                cache.NativesVersion = ver;
                _store.SaveLaunchCache(name, cache);
            }

            bool classpathValid = cache?.MinecraftVersion == ver
                && cache.Classpath is not null
                && cache.LoaderVersion == loaderVersion;

            if (!classpathValid)
            {
                var cp = BuildClasspath(instance, root, loaderRoot);
                cache ??= new LaunchCache { MinecraftVersion = ver, LastSuccess = DateTime.UtcNow };
                cache.Classpath     = cp;
                cache.LoaderVersion = loaderVersion;
                _store.SaveLaunchCache(name, cache);
            }

            await GetJavaMajorVersionAsync();
        }
        catch { }
    }

    // ── Shared utilities ──────────────────────────────────────────────────────

    private static async Task DownloadFileAsync(string url, string dest, ProgressTask task)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        task.MaxValue = total > 0 ? total : 100;

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file   = File.Create(dest);

        var  buffer   = new byte[81920];
        long received = 0;
        int  read;

        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            received  += read;
            task.Value = total > 0 ? received : task.Value + 1;
        }
    }

    private static async Task DownloadFileAsync(string url, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file   = File.Create(dest);
        await stream.CopyToAsync(file);
    }

    private static async Task<string> ComputeSha1Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA1.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

