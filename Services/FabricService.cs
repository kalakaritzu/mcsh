using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using McSH.Models;

namespace McSH.Services;

/// <summary>
/// Fetches Fabric and Quilt loader profiles from their respective Meta APIs.
/// Both use the same profile JSON format; only the base URL and main class differ.
/// </summary>
public class FabricService
{
    private static readonly HttpClient Http = new();

    static FabricService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.1.0");

    private const string FabricMeta = "https://meta.fabricmc.net/v2";
    private const string QuiltMeta  = "https://meta.quiltmc.org/v3";

    /// <summary>
    /// Returns the Fabric or Quilt loader profile JSON and the resolved loader version.
    /// Result is cached to disk — subsequent calls are instant.
    /// </summary>
    public async Task<(JsonDocument? Doc, string? LoaderVersion)> GetProfileAsync(
        string mcVersion, ModLoader loader, string? loaderVersion = null)
    {
        var metaBase  = loader == ModLoader.Quilt ? QuiltMeta : FabricMeta;
        var loaderKey = loader == ModLoader.Quilt ? "quilt"   : "fabric";

        loaderVersion ??= await GetLatestLoaderVersionAsync(mcVersion, metaBase);
        if (loaderVersion is null) return (null, null);

        var cachePath = PathService.LoaderProfilePath(loaderKey, mcVersion, loaderVersion);
        if (File.Exists(cachePath))
        {
            try { return (JsonDocument.Parse(await File.ReadAllTextAsync(cachePath)), loaderVersion); }
            catch { /* fall through and re-download */ }
        }

        try
        {
            var url = $"{metaBase}/versions/loader" +
                      $"/{Uri.EscapeDataString(mcVersion)}" +
                      $"/{Uri.EscapeDataString(loaderVersion)}/profile/json";

            var json = await Http.GetStringAsync(url);

            Directory.CreateDirectory(PathService.VersionsDir);
            await File.WriteAllTextAsync(cachePath, json);

            return (JsonDocument.Parse(json), loaderVersion);
        }
        catch { return (null, null); }
    }

    private static async Task<string?> GetLatestLoaderVersionAsync(string mcVersion, string metaBase)
    {
        try
        {
            var entries = await Http.GetFromJsonAsync<LoaderEntry[]>(
                $"{metaBase}/versions/loader/{Uri.EscapeDataString(mcVersion)}");
            return entries?.FirstOrDefault()?.Loader?.Version;
        }
        catch { return null; }
    }

    private record LoaderEntry(
        [property: JsonPropertyName("loader")] LoaderInfo? Loader);

    private record LoaderInfo(
        [property: JsonPropertyName("version")] string Version);
}

