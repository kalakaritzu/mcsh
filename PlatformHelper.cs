using System.Diagnostics;
using System.Runtime.InteropServices;

namespace McSH;

/// <summary>
/// Cross-platform helpers so McSH runs on Windows, Linux, and macOS
/// without any conditional logic scattered across the codebase.
/// </summary>
public static class PlatformHelper
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux   => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMacOS   => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>OS name used in Minecraft library rules: "windows", "linux", or "osx".</summary>
    public static string McOsName => IsWindows ? "windows" : IsLinux ? "linux" : "osx";

    /// <summary>Java executable filename: "java.exe" on Windows, "java" everywhere else.</summary>
    public static string JavaExeName => IsWindows ? "java.exe" : "java";

    /// <summary>OS identifier for the Adoptium API: "windows", "linux", or "mac".</summary>
    public static string AdoptiumOs => IsWindows ? "windows" : IsLinux ? "linux" : "mac";

    /// <summary>JDK download archive extension: ".zip" on Windows, ".tar.gz" on Linux/macOS.</summary>
    public static string JdkArchiveExt => IsWindows ? ".zip" : ".tar.gz";

    /// <summary>
    /// Opens a folder in the platform's native file manager
    /// (Explorer on Windows, Nautilus/Thunar via xdg-open on Linux, Finder on macOS).
    /// </summary>
    public static void OpenFolder(string path)
    {
        ProcessStartInfo psi;

        if (IsWindows)
            psi = new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { path }, UseShellExecute = true };
        else if (IsMacOS)
            psi = new ProcessStartInfo { FileName = "open", ArgumentList = { path }, UseShellExecute = false };
        else
            psi = new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { path }, UseShellExecute = false };

        try { Process.Start(psi); }
        catch { /* silently ignore if no file manager is available */ }
    }
}
