using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McSH.Models;

namespace McSH.Services;

public record MojangSkin(string Id, string State, string Url, string Variant);
public record MojangCape(string Id, string State, string Url, string Alias);
public record MojangProfile(string Id, string Name, List<MojangSkin> Skins, List<MojangCape> Capes);

public class SkinService
{
    private static readonly HttpClient Http = new();

    static SkinService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("McSH-Launcher/0.5.0");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // ── Local skin store ──────────────────────────────────────────────────────

    public List<SkinEntry> GetAll()
    {
        var path = PathService.SkinsManifestPath;
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<SkinEntry>>(File.ReadAllText(path), JsonOpts) ?? []; }
        catch { return []; }
    }

    public void Save(List<SkinEntry> skins)
    {
        Directory.CreateDirectory(PathService.SkinsDir);
        File.WriteAllText(PathService.SkinsManifestPath,
            JsonSerializer.Serialize(skins, JsonOpts));
    }

    public (SkinEntry? Entry, string? Error) Import(string sourcePath, string name, string model)
    {
        if (!File.Exists(sourcePath))
            return (null, $"File not found: {sourcePath}");
        if (!sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return (null, "Only .png files can be imported as skins.");

        Directory.CreateDirectory(PathService.SkinsDir);

        var safeBase = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fileName = safeBase + ".png";
        var dest     = Path.Combine(PathService.SkinsDir, fileName);

        // Avoid name collision
        int n = 1;
        while (File.Exists(dest))
            dest = Path.Combine(PathService.SkinsDir, $"{safeBase}_{n++}.png");
        fileName = Path.GetFileName(dest);

        File.Copy(sourcePath, dest, overwrite: false);

        var entry = new SkinEntry { Name = name, FileName = fileName, Model = model };
        var all   = GetAll();
        all.Add(entry);
        Save(all);
        return (entry, null);
    }

    public void Delete(SkinEntry entry)
    {
        var file = Path.Combine(PathService.SkinsDir, entry.FileName);
        if (File.Exists(file)) File.Delete(file);
        var all = GetAll();
        all.RemoveAll(s => s.FileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase));
        Save(all);
    }

    // ── Mojang profile API ────────────────────────────────────────────────────

    public async Task<MojangProfile?> GetProfileAsync(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var skins = new List<MojangSkin>();
            if (root.TryGetProperty("skins", out var sk))
                foreach (var s in sk.EnumerateArray())
                    skins.Add(new MojangSkin(
                        s.GetProperty("id").GetString() ?? "",
                        s.GetProperty("state").GetString() ?? "",
                        s.GetProperty("url").GetString() ?? "",
                        s.TryGetProperty("variant", out var v) ? v.GetString() ?? "CLASSIC" : "CLASSIC"));

            var capes = new List<MojangCape>();
            if (root.TryGetProperty("capes", out var cp))
                foreach (var c in cp.EnumerateArray())
                    capes.Add(new MojangCape(
                        c.GetProperty("id").GetString() ?? "",
                        c.GetProperty("state").GetString() ?? "",
                        c.GetProperty("url").GetString() ?? "",
                        c.TryGetProperty("alias", out var a) ? a.GetString() ?? "" : ""));

            return new MojangProfile(
                root.GetProperty("id").GetString() ?? "",
                root.GetProperty("name").GetString() ?? "",
                skins, capes);
        }
        catch (Exception ex)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Profile fetch failed: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
            return null;
        }
    }

    public async Task<bool> UploadSkinAsync(string token, string pngPath, string model)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(model), "variant");

            var bytes      = await File.ReadAllBytesAsync(pngPath);
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "file", Path.GetFileName(pngPath));

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.minecraftservices.com/minecraft/profile/skins");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = form;

            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Skin upload failed: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
            return false;
        }
    }

    public async Task<bool> SetActiveCapeAsync(string token, string? capeId)
    {
        try
        {
            using var req = capeId is null
                ? new HttpRequestMessage(HttpMethod.Delete,
                    "https://api.minecraftservices.com/minecraft/profile/capes/active")
                : new HttpRequestMessage(HttpMethod.Put,
                    "https://api.minecraftservices.com/minecraft/profile/capes/active")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { capeId }),
                        Encoding.UTF8, "application/json"),
                };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Spectre.Console.AnsiConsole.MarkupLine($"[dim]Cape update failed: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
            return false;
        }
    }
}
