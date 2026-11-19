using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Koromogae.Audio;
using Koromogae.Ui;
using VstCore;

namespace Koromogae;

/// <summary>
/// 実行環境から、バージョン情報の材料を集める (版・.NET・OS・PortAudio・プラグインホストの版・x86 版 .NET)。
/// 文言の組み立ては <see cref="AppVersionInfo"/> (WPF に依存しない純粋なクラス) が行う。
/// </summary>
public static class AppInfo
{
    /// <summary>実行ファイルに埋め込んだ版 (Directory.Build.props の Version)。ビルドメタデータは除く。</summary>
    public static string Version
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return AppVersionInfo.CleanVersion(informational ?? assembly.GetName().Version?.ToString());
        }
    }

    /// <summary>起動時に app.log へ残す 1 行。</summary>
    public static string StartupLine() => AppVersionInfo.StartupLine(
        Version, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString());

    /// <summary>バージョン情報の画面に出す内容を集める。</summary>
    public static AppVersionReport Collect() => new(
        Version,
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        AudioDevices.VersionText(),
        HostVersion(PluginBitness.X86),
        HostVersion(PluginBitness.X64),
        InstalledX86Runtimes());

    /// <summary>プラグインホストの実行ファイルの製品バージョン。プロセスは起動しない。見つからなければ null。</summary>
    static string? HostVersion(PluginBitness bitness)
    {
        string path = PluginLoader.HostPath(bitness);
        if (!File.Exists(path)) return null;
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch { return null; }
    }

    /// <summary>
    /// インストール済みの x86 版共有ランタイム (Microsoft.NETCore.App) の版。32bit のプラグインホストが使う。
    /// 場所は、レジストリの x86 の InstallLocation (なければ Program Files (x86)\dotnet)。読めなければ空。
    /// </summary>
    static IReadOnlyCollection<string> InstalledX86Runtimes()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x86");
            string? location = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(location))
                location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet");

            string shared = Path.Combine(location, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(shared)) return Array.Empty<string>();
            return Directory.GetDirectories(shared).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}