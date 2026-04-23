using System.Text.Json;
using System.Text.Json.Serialization;

namespace McSH.Services;

public class MojangService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static MojangService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.1.0");

    private const string ManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    // ── Manifest cache ────────────────────────────────────────────────────────

    private static string?  _cachedManifestJson;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _manifestLock = new(1, 1);

    private async Task<string?> GetManifestJsonAsync()
    {
        if (_cachedManifestJson is not null && DateTime.UtcNow < _cacheExpiry)
            return _cachedManifestJson;

        await _manifestLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock.
            if (_cachedManifestJson is not null && DateTime.UtcNow < _cacheExpiry)
                return _cachedManifestJson;

            var json = await Http.GetStringAsync(ManifestUrl);
            _cachedManifestJson = json;
            _cacheExpiry        = DateTime.UtcNow.AddMinutes(10);
            return json;
        }
        catch { return _cachedManifestJson; } // return stale on error
        finally { _manifestLock.Release(); }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns all stable release version IDs, newest first.</summary>
    public async Task<string[]> GetReleaseVersionsAsync()
    {
        try
        {
            var json     = await GetManifestJsonAsync();
            if (json is null) return [];
            var manifest = JsonSerializer.Deserialize<VersionManifest>(json);
            return manifest?.Versions
                .Where(v => v.Type == "release")
                .Select(v => v.Id)
                .ToArray() ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Returns the full version metadata JSON for <paramref name="version"/>.
    /// Caches the result to disk so subsequent calls are instant.
    /// </summary>
    public async Task<JsonDocument?> GetVersionJsonAsync(string version)
    {
        // Return cached copy if available
        var cached = PathService.VersionJsonPath(version);
        if (File.Exists(cached))
        {
            try { return JsonDocument.Parse(await File.ReadAllTextAsync(cached)); }
            catch { /* fall through and re-download */ }
        }

        try
        {
            var manifestJson = await GetManifestJsonAsync();
            if (manifestJson is null) return null;
            var manifest = JsonSerializer.Deserialize<VersionManifest>(manifestJson);
            var entry    = manifest?.Versions.FirstOrDefault(v => v.Id == version);
            if (entry is null) return null;

            var metaJson = await Http.GetStringAsync(entry.Url);

            Directory.CreateDirectory(PathService.VersionDir(version));
            await File.WriteAllTextAsync(cached, metaJson);

            return JsonDocument.Parse(metaJson);
        }
        catch { return null; }
    }

    // ── Deserialization types ─────────────────────────────────────────────────

    private record VersionManifest(
        [property: JsonPropertyName("versions")] List<VersionEntry> Versions);

    private record VersionEntry(
        [property: JsonPropertyName("id")]   string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")]  string Url);
}
