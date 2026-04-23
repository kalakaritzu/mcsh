using System.Reflection;
using System.Text.Json;

namespace McSH.Services;

/// <summary>
/// Loads a JSON language file and provides fast string lookups.
/// Language files are embedded in the assembly — no external files needed.
/// Supported codes: "en" (default), "es".
/// Falls back to English if the requested language is missing.
/// </summary>
public static class LanguageService
{
    private static Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);

    public static void Load(string langCode)
    {
        if (!string.Equals(langCode, "en", StringComparison.OrdinalIgnoreCase))
        {
            if (TryLoadEmbedded(langCode, out var strings))
            {
                _strings = strings!;
                return;
            }
        }

        // Fall back to English
        if (TryLoadEmbedded("en", out var enStrings))
            _strings = enStrings!;
    }

    private static bool TryLoadEmbedded(string langCode, out Dictionary<string, string>? result)
    {
        var asm = Assembly.GetExecutingAssembly();
        // Embedded resource name: McSH.Languages.<langCode>.json
        var name = $"McSH.Languages.{langCode}.json";

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) { result = null; return false; }

        try
        {
            result = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                     ?? new Dictionary<string, string>();
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    /// <summary>Returns the localised string for <paramref name="key"/>, or the key itself if not found.</summary>
    public static string Get(string key) =>
        _strings.TryGetValue(key, out var s) ? s : key;
}
