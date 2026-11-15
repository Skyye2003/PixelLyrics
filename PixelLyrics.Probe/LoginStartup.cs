using System.IO;
using Microsoft.Win32;

namespace PixelLyrics.Probe;

internal interface ILoginStartupStore
{
    object? Read();
    void Write(string command);
    void Delete();
}

internal sealed class CurrentUserLoginStartupStore : ILoginStartupStore
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "PixelLyrics";

    public object? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal sealed class LoginStartup(ILoginStartupStore store, Func<string> executablePath)
{
    internal static string CurrentExecutablePath()
    {
        string expected = Path.ChangeExtension(typeof(LoginStartup).Assembly.Location, ".exe");
        string? process = Environment.ProcessPath;
        string path = process is not null && string.Equals(Path.GetFileNameWithoutExtension(process),
            typeof(LoginStartup).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase) ? process : expected;
        if (!File.Exists(path)) throw new FileNotFoundException("未找到应用 exe，不能设置登录自启。");
        return path;
    }

    internal static string BuildCommand(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("自启必须使用本地应用 exe 的绝对路径。", nameof(path));
        return $"\"{Path.GetFullPath(path)}\" --tray";
    }

    internal bool ReadEnabled()
    {
        object? value = store.Read();
        if (value is null) return false;
        if (value is string command && string.Equals(command, BuildCommand(executablePath()), StringComparison.OrdinalIgnoreCase))
            return true;
        throw new InvalidDataException("PixelLyrics 自启项不是当前应用的托盘启动命令，请明确选择后保存修复。");
    }

    internal void Apply(bool enabled)
    {
        if (enabled) store.Write(BuildCommand(executablePath()));
        else store.Delete();
        if (ReadEnabled() != enabled) throw new IOException("自启状态回读与请求不一致。");
    }
}

internal static class DesktopLaunch
{
    // null 表示交给既有命令行解析器；托盘参数不能与联网或写屏参数混用。
    internal static bool? Parse(string[] args) => args.Length == 0 ? false
        : args.Length == 1 && args[0] == "--gui" ? false
        : args.Length == 1 && args[0] == "--tray" ? true : null;
}
