using System.Diagnostics;
using System.Runtime.InteropServices;

namespace McSH.Services;

public record JavaInstall(string Path, int MajorVersion, string? Tag);

public static class JavaService
{
    public static async Task<List<JavaInstall>> FindAllAsync()
    {
        var found = new Dictionary<string, JavaInstall>(StringComparer.OrdinalIgnoreCase);

        // 1. McSH managed JDKs (highest priority label)
        var jdksDir = PathService.ManagedJdksDir;
        if (Directory.Exists(jdksDir))
        {
            foreach (var exe in Directory.GetFiles(jdksDir, PlatformHelper.JavaExeName, SearchOption.AllDirectories))
            {
                if (found.ContainsKey(exe)) continue;
                var v = await GetMajorVersionAsync(exe);
                if (v > 0) found[exe] = new JavaInstall(exe, v, "McSH managed");
            }
        }

        // 2. Common system directories
        foreach (var root in GetSearchRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var exe = Path.Combine(dir, "bin", PlatformHelper.JavaExeName);
                if (!File.Exists(exe) || found.ContainsKey(exe)) continue;
                var v = await GetMajorVersionAsync(exe);
                if (v > 0) found[exe] = new JavaInstall(exe, v, null);
            }
        }

        // 3. PATH (last so system dirs get better labels where they overlap)
        foreach (var exe in await FindOnPathAsync())
        {
            if (found.ContainsKey(exe)) continue;
            var v = await GetMajorVersionAsync(exe);
            if (v > 0) found[exe] = new JavaInstall(exe, v, "PATH");
        }

        return [.. found.Values.OrderByDescending(j => j.MajorVersion)];
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Path.Combine(pf,   "Java");
            yield return Path.Combine(pf,   "Eclipse Adoptium");
            yield return Path.Combine(pf,   "Microsoft");
            yield return Path.Combine(pf,   "BellSoft");
            yield return Path.Combine(pf,   "Amazon Corretto");
            yield return Path.Combine(pf,   "Azul Systems");
            yield return Path.Combine(pf86, "Java");
        }
        else
        {
            yield return "/usr/lib/jvm";
            yield return "/usr/local/lib/jvm";
            yield return "/opt/jdk";
            yield return "/opt/java";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".sdkman", "candidates", "java");
            yield return Path.Combine(home, ".jdks");
        }
    }

    private static async Task<List<string>> FindOnPathAsync()
    {
        var result = new List<string>();
        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName               = isWindows ? "where" : "which",
                Arguments              = isWindows ? "java" : "-a java",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            };
            p.Start();
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (File.Exists(trimmed)) result.Add(trimmed);
            }
        }
        catch { }
        return result;
    }

    public static async Task<int> GetMajorVersionAsync(string javaExe)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName              = javaExe,
                Arguments             = "-version",
                UseShellExecute       = false,
                RedirectStandardError = true,  // java -version writes to stderr
                CreateNoWindow        = true,
            };
            p.Start();
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            p.Dispose();

            // "java version "21.0.3"" or "openjdk version "17.0.2"" or "1.8.0_xxx"
            var match = System.Text.RegularExpressions.Regex.Match(
                err, @"version ""(\d+)(?:\.(\d+))?");
            if (!match.Success) return 0;
            var first = int.Parse(match.Groups[1].Value);
            // Old format: 1.8.x → major is second component
            return first == 1 && match.Groups[2].Success
                ? int.Parse(match.Groups[2].Value)
                : first;
        }
        catch { return 0; }
    }
}
