using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamWID.Services;

public static class CliIntegrationService
{
    private static readonly string ShimFolder = Path.Combine(AppSettings.Folder, "cli");
    private const string WindowsShimMarker = "StreamWID CLI shim v2";
    private static readonly string[] MediaExtensions = [".mp4", ".mov", ".mkv", ".webm", ".avi"];

    public static string CommandName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "swid.cmd" : "swid";
    public static string ShimPath => Path.Combine(ShimFolder, CommandName);
    public static bool IsContextMenuSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsCliAvailable()
    {
        return File.Exists(ShimPath) && IsFolderOnPath(ShimFolder) && IsShimCurrent();
    }

    public static void InstallCli()
    {
        Directory.CreateDirectory(ShimFolder);
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("Could not resolve the StreamWID executable path.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.WriteAllText(ShimPath, BuildWindowsShim(exePath), Encoding.ASCII);
            AddFolderToUserPath(ShimFolder);
            return;
        }

        File.WriteAllText(ShimPath, BuildUnixShim(exePath), Encoding.ASCII);
        TryMakeExecutable(ShimPath);
        AddFolderToShellProfile(ShimFolder);
    }

    public static void InstallWindowsContextMenu()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Explorer context menu integration is only available on Windows.");

        InstallCli();

        foreach (var extension in MediaExtensions)
        {
            using var commandKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\StreamWID\command");
            commandKey?.SetValue("", $"\"{ShimPath}\" --auto \"%1\"");

            using var shellKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\StreamWID");
            shellKey?.SetValue("", "Analyze and cut with StreamWID");
            shellKey?.SetValue("Icon", Environment.ProcessPath ?? "");
        }
    }

    private static string BuildWindowsShim(string exePath)
    {
        var escaped = exePath.Replace("\"", "\"\"");
        var dllPath = Path.ChangeExtension(exePath, ".dll").Replace("\"", "\"\"");
        var consolePath = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "swid.exe").Replace("\"", "\"\"");
        return
            "@echo off\r\n" +
            $"rem {WindowsShimMarker}\r\n" +
            "setlocal\r\n" +
            $"set \"SWID_EXE={consolePath}\"\r\n" +
            $"set \"SWID_DLL={dllPath}\"\r\n" +
            "if exist \"%SWID_EXE%\" (\r\n" +
            "  \"%SWID_EXE%\" %*\r\n" +
            ") else if exist \"%SWID_DLL%\" (\r\n" +
            "  dotnet \"%SWID_DLL%\" %*\r\n" +
            ") else (\r\n" +
            $"  \"{escaped}\" %*\r\n" +
            ")\r\n" +
            "exit /b %ERRORLEVEL%\r\n";
    }

    private static bool IsShimCurrent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;

        try
        {
            return File.ReadAllText(ShimPath).Contains(WindowsShimMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildUnixShim(string exePath)
    {
        var escaped = exePath.Replace("\"", "\\\"");
        var consolePath = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "swid").Replace("\"", "\\\"");
        var dllPath = Path.ChangeExtension(exePath, ".dll").Replace("\"", "\\\"");
        return
            "#!/usr/bin/env sh\n" +
            $"SWID_EXE=\"{consolePath}\"\n" +
            $"SWID_DLL=\"{dllPath}\"\n" +
            "if [ -x \"$SWID_EXE\" ]; then\n" +
            "  exec \"$SWID_EXE\" \"$@\"\n" +
            "elif [ -f \"$SWID_DLL\" ]; then\n" +
            "  exec dotnet \"$SWID_DLL\" \"$@\"\n" +
            "else\n" +
            $"  exec \"{escaped}\" \"$@\"\n" +
            "fi\n";
    }

    private static bool IsFolderOnPath(string folder)
    {
        var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "";
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        return IsFolderInPathValue(processPath, folder) || IsFolderInPathValue(userPath, folder);
    }

    private static void AddFolderToUserPath(string folder)
    {
        if (IsFolderOnPath(folder))
            return;

        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var updated = string.IsNullOrWhiteSpace(path) ? folder : path.TrimEnd(Path.PathSeparator) + Path.PathSeparator + folder;
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);

        var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "";
        if (!IsFolderInPathValue(processPath, folder))
            Environment.SetEnvironmentVariable("PATH", processPath + Path.PathSeparator + folder, EnvironmentVariableTarget.Process);
    }

    private static bool IsFolderInPathValue(string path, string folder) =>
        path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(x => string.Equals(TrimPath(x), folder, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal));

    private static void AddFolderToShellProfile(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("Could not resolve the user profile folder.");

        var line = $"export PATH=\"$PATH:{folder}\"";
        var profiles = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? [".zprofile", ".zshrc", ".profile"]
            : new[] { ".profile", ".bashrc", ".zshrc" };

        foreach (var profileName in profiles)
            AppendLineIfMissing(Path.Combine(home, profileName), line);
    }

    private static void AppendLineIfMissing(string path, string line)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        if (!existing.Contains(line, StringComparison.Ordinal))
            File.AppendAllText(path, $"{Environment.NewLine}{line}{Environment.NewLine}");
    }

    private static void TryMakeExecutable(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit();
        }
        catch
        {
        }
    }

    private static string TrimPath(string value) => value.Trim().Trim('"');
}
