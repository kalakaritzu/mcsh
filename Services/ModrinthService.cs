using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using McSH.Models;

namespace McSH.Services;

// ── Public return types ───────────────────────────────────────────────────────

public record ModrinthProject(string Id, string Slug, string Title);

public record ModrinthProjectDetails(
    string   Id,
    string   Slug,
    string   Title,
    string   Description,
    string   Body,
    string[] Categories,
    long     Downloads,
    long     Followers,
    string   ClientSide,
    string   ServerSide,
    string?  SourceUrl,
    string?  IssuesUrl,
    string?  WikiUrl,
    string?  DiscordUrl);

public record ModrinthVersion(
    string   Id,
    string   ProjectId,
    string   VersionNumber,
    string   FileName,
    string   DownloadUrl,
    string   Sha512,
    ModrinthDependency[] Dependencies);

public record ModrinthDependency(
    string? ProjectId,
    string? VersionId,
    string  DependencyType);

/// <summary>The primary downloadable file of a modpack version.</summary>
public record ModpackVersionFile(
    string   Url,
    string   Filename,
    string   VersionNumber,
    string[] GameVersions);

/// <summary>A single version entry for a modpack, used in the version picker.</summary>
public record ModpackVersionEntry(
    string   VersionNumber,
    string   Name,
    string[] GameVersions,
    string[] Loaders,
    DateTime DatePublished,
    string   Url,
    string   Filename,
    string   VersionId);

// ─────────────────────────────────────────────────────────────────────────────

public class ModrinthService
{
    private static readonly HttpClient Http = new();

    static ModrinthService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.1.0");

    private const string Base = "https://api.modrinth.com/v2";

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches Modrinth for the given project type.
    /// Pass null for mcVersion or loader to omit those facets (e.g. shaders don't need a loader).
    /// </summary>
    public async Task<ModSearchHit[]> SearchAsync(
        string  query,
        string? mcVersion,
        string? loader,
        string  projectType = "mod")
    {
        var facetParts = new List<string> { $"[\"project_type:{projectType}\"]" };
        if (mcVersion is not null) facetParts.Add($"[\"versions:{mcVersion}\"]");
        if (loader    is not null) facetParts.Add($"[\"categories:{loader}\"]");
        var facets = "[" + string.Join(",", facetParts) + "]";

        var url =
            $"{Base}/search?query={Uri.EscapeDataString(query)}" +
            $"&facets={Uri.EscapeDataString(facets)}&limit=15";

        try
        {
            var resp = await Http.GetFromJsonAsync<SearchResponse>(url);
            return resp?.Hits.Select(h => new ModSearchHit(
                h.ProjectId, h.Slug, h.Title, h.Author, h.Downloads, h.Description))
                .ToArray() ?? [];
        }
        catch { return []; }
    }

    // ── Project lookup ────────────────────────────────────────────────────────

    public async Task<ModrinthProject?> GetProjectAsync(string idOrSlug)
    {
        try
        {
            var p = await Http.GetFromJsonAsync<ProjectResponse>($"{Base}/project/{idOrSlug}");
            return p is null ? null : new ModrinthProject(p.Id, p.Slug, p.Title);
        }
        catch { return null; }
    }

    public async Task<ModrinthProjectDetails?> GetProjectDetailsAsync(string idOrSlug)
    {
        try
        {
            var p = await Http.GetFromJsonAsync<ProjectDetailsResponse>($"{Base}/project/{idOrSlug}");
            if (p is null) return null;
            return new ModrinthProjectDetails(
                p.Id, p.Slug, p.Title, p.Description, p.Body,
                p.Categories, p.Downloads, p.Followers,
                p.ClientSide, p.ServerSide,
                p.SourceUrl, p.IssuesUrl, p.WikiUrl, p.DiscordUrl);
        }
        catch { return null; }
    }

    // ── Version resolution ────────────────────────────────────────────────────

    /// <summary>Returns the latest version of a project compatible with the given MC version and loader.</summary>
    public async Task<ModrinthVersion?> GetCompatibleVersionAsync(
        string projectId, string? mcVersion, string? loader)
    {
        var url = $"{Base}/project/{projectId}/version?";
        if (mcVersion is not null) url += $"game_versions={Uri.EscapeDataString($"[\"{mcVersion}\"]")}&";
        if (loader    is not null) url += $"loaders={Uri.EscapeDataString($"[\"{loader}\"]")}&";
        url = url.TrimEnd('&', '?');

        try
        {
            var versions = await Http.GetFromJsonAsync<VersionResponse[]>(url);
            var v = versions?.FirstOrDefault();
            return v is null ? null : MapVersion(v);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the primary downloadable file of the latest version of a modpack project.
    /// Used by ModpackCommand to get the .mrpack download URL.
    /// </summary>
    public async Task<ModpackVersionFile?> GetModpackLatestFileAsync(string projectId)
    {
        try
        {
            var json = await Http.GetStringAsync($"{Base}/project/{Uri.EscapeDataString(projectId)}/version");
            var doc  = JsonDocument.Parse(json);

            foreach (var ver in doc.RootElement.EnumerateArray())
            {
                var versionNumber = ver.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : "";
                var gameVersions  = ver.TryGetProperty("game_versions", out var gv)
                    ? gv.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
                    : Array.Empty<string>();

                if (!ver.TryGetProperty("files", out var filesElem)) continue;
                foreach (var file in filesElem.EnumerateArray())
                {
                    if (file.TryGetProperty("primary", out var primary) && primary.GetBoolean())
                    {
                        return new ModpackVersionFile(
                            file.GetProperty("url").GetString()!,
                            file.GetProperty("filename").GetString()!,
                            versionNumber,
                            gameVersions);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Returns all versions of a modpack project, newest first, each with its loaders,
    /// game versions, published date, and primary file download URL.
    /// </summary>
    public async Task<ModpackVersionEntry[]> GetAllModpackVersionsAsync(string projectId)
    {
        try
        {
            var json    = await Http.GetStringAsync($"{Base}/project/{Uri.EscapeDataString(projectId)}/version");
            var doc     = JsonDocument.Parse(json);
            var entries = new List<ModpackVersionEntry>();

            foreach (var ver in doc.RootElement.EnumerateArray())
            {
                var versionNumber = ver.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : "";
                var name          = ver.TryGetProperty("name",           out var nm) ? nm.GetString() ?? "" : versionNumber;
                var gameVersions  = ver.TryGetProperty("game_versions",  out var gv)
                    ? gv.EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : [];
                var loaders = ver.TryGetProperty("loaders", out var lo)
                    ? lo.EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : [];
                var published = ver.TryGetProperty("date_published", out var dp) &&
                                DateTime.TryParse(dp.GetString(), out var dt)
                    ? dt.ToUniversalTime()
                    : DateTime.MinValue;

                string url      = "";
                string filename = "";
                if (ver.TryGetProperty("files", out var files))
                {
                    foreach (var file in files.EnumerateArray())
                    {
                        if (file.TryGetProperty("primary", out var primary) && primary.GetBoolean())
                        {
                            url      = file.TryGetProperty("url",      out var u) ? u.GetString() ?? "" : "";
                            filename = file.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "";
                            break;
                        }
                    }
                }

                var versionId = ver.TryGetProperty("id", out var vid) ? vid.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(url))
                    entries.Add(new ModpackVersionEntry(versionNumber, name, gameVersions, loaders, published, url, filename, versionId));
            }

            return [.. entries];
        }
        catch { return []; }
    }

    /// <summary>Returns a specific version by its ID.</summary>
    public async Task<ModrinthVersion?> GetVersionAsync(string versionId)
    {
        try
        {
            var v = await Http.GetFromJsonAsync<VersionResponse>($"{Base}/version/{versionId}");
            return v is null ? null : MapVersion(v);
        }
        catch { return null; }
    }

    // ── Recursive dependency resolution ───────────────────────────────────────

    /// <summary>
    /// Resolves the given project and all its required dependencies recursively.
    /// Returns a flat list ordered so dependencies come before the mods that need them.
    /// Skips any project whose ID is already in <paramref name="alreadyInstalled"/>.
    /// </summary>
    public async Task<List<(ModrinthProject Project, ModrinthVersion Version)>> ResolveAllAsync(
        string projectIdOrSlug,
        string mcVersion,
        string loader,
        IEnumerable<string> alreadyInstalled)
    {
        var visited = new HashSet<string>(alreadyInstalled, StringComparer.OrdinalIgnoreCase);
        var results = new List<(ModrinthProject, ModrinthVersion)>();
        await ResolveRecursiveAsync(projectIdOrSlug, mcVersion, loader, visited, results);
        return results;
    }

    private async Task ResolveRecursiveAsync(
        string projectIdOrSlug,
        string mcVersion,
        string loader,
        HashSet<string> visited,
        List<(ModrinthProject, ModrinthVersion)> results)
    {
        var project = await GetProjectAsync(projectIdOrSlug);
        if (project is null) return;
        if (!visited.Add(project.Id)) return; // already resolved or installed

        var version = await GetCompatibleVersionAsync(project.Id, mcVersion, loader);
        if (version is null) return; // no compatible version — skip silently

        // Resolve required deps first (depth-first → deps end up before their dependents)
        foreach (var dep in version.Dependencies)
        {
            if (dep.DependencyType != "required") continue;

            if (dep.VersionId is not null)
            {
                // Pinned to a specific version
                var pinned = await GetVersionAsync(dep.VersionId);
                if (pinned is null) continue;
                if (visited.Contains(pinned.ProjectId)) continue;

                var depProject = await GetProjectAsync(pinned.ProjectId);
                if (depProject is null) continue;

                if (visited.Add(depProject.Id))
                {
                    results.Add((depProject, pinned));
                    // Recurse deps of this pinned dep
                    foreach (var subDep in pinned.Dependencies)
                    {
                        if (subDep.DependencyType == "required" && subDep.ProjectId is not null)
                            await ResolveRecursiveAsync(subDep.ProjectId, mcVersion, loader, visited, results);
                    }
                }
            }
            else if (dep.ProjectId is not null)
            {
                await ResolveRecursiveAsync(dep.ProjectId, mcVersion, loader, visited, results);
            }
        }

        results.Add((project, version));
    }

    // ── Download + verify ─────────────────────────────────────────────────────

    public async Task<bool> DownloadAsync(string url, string destPath, string expectedSha512)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var file   = File.Create(destPath))
            await stream.CopyToAsync(file);

        // Verify hash
        await using var fs = File.OpenRead(destPath);
        var hash = Convert.ToHexString(await SHA512.HashDataAsync(fs)).ToLowerInvariant();

        if (!hash.Equals(expectedSha512, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destPath);
            return false;
        }

        return true;
    }

    // ── JSON deserialization types ────────────────────────────────────────────

    private static ModrinthVersion MapVersion(VersionResponse v)
    {
        var primary = v.Files.FirstOrDefault(f => f.Primary) ?? v.Files.FirstOrDefault();
        return new ModrinthVersion(
            v.Id,
            v.ProjectId,
            v.VersionNumber,
            primary?.Filename ?? string.Empty,
            primary?.Url      ?? string.Empty,
            primary?.Hashes.Sha512 ?? string.Empty,
            v.Dependencies.Select(d => new ModrinthDependency(
                d.ProjectId, d.VersionId, d.DependencyType)).ToArray());
    }

    private record SearchResponse(
        [property: JsonPropertyName("hits")] List<SearchHit> Hits);

    private record SearchHit(
        [property: JsonPropertyName("project_id")]  string ProjectId,
        [property: JsonPropertyName("slug")]         string Slug,
        [property: JsonPropertyName("title")]        string Title,
        [property: JsonPropertyName("author")]       string Author,
        [property: JsonPropertyName("downloads")]    long   Downloads,
        [property: JsonPropertyName("description")]  string Description);

    private record ProjectResponse(
        [property: JsonPropertyName("id")]    string Id,
        [property: JsonPropertyName("slug")]  string Slug,
        [property: JsonPropertyName("title")] string Title);

    private record ProjectDetailsResponse(
        [property: JsonPropertyName("id")]          string   Id,
        [property: JsonPropertyName("slug")]         string   Slug,
        [property: JsonPropertyName("title")]        string   Title,
        [property: JsonPropertyName("description")]  string   Description,
        [property: JsonPropertyName("body")]         string   Body,
        [property: JsonPropertyName("categories")]   string[] Categories,
        [property: JsonPropertyName("downloads")]    long     Downloads,
        [property: JsonPropertyName("followers")]    long     Followers,
        [property: JsonPropertyName("client_side")]  string   ClientSide,
        [property: JsonPropertyName("server_side")]  string   ServerSide,
        [property: JsonPropertyName("source_url")]   string?  SourceUrl,
        [property: JsonPropertyName("issues_url")]   string?  IssuesUrl,
        [property: JsonPropertyName("wiki_url")]     string?  WikiUrl,
        [property: JsonPropertyName("discord_url")]  string?  DiscordUrl);

    private record VersionResponse(
        [property: JsonPropertyName("id")]             string             Id,
        [property: JsonPropertyName("project_id")]     string             ProjectId,
        [property: JsonPropertyName("version_number")] string             VersionNumber,
        [property: JsonPropertyName("files")]          List<VersionFile>  Files,
        [property: JsonPropertyName("dependencies")]   List<DepResponse>  Dependencies);

    private record VersionFile(
        [property: JsonPropertyName("filename")] string      Filename,
        [property: JsonPropertyName("url")]      string      Url,
        [property: JsonPropertyName("hashes")]   FileHashes  Hashes,
        [property: JsonPropertyName("primary")]  bool        Primary);

    private record FileHashes(
        [property: JsonPropertyName("sha512")] string Sha512);

    private record DepResponse(
        [property: JsonPropertyName("project_id")]     string? ProjectId,
        [property: JsonPropertyName("version_id")]     string? VersionId,
        [property: JsonPropertyName("dependency_type")] string DependencyType);
}

