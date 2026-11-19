using System.Runtime.CompilerServices;
using Koromogae;

namespace Settings.Tests;

static class TestSupport
{
    /// <summary>全テストで共有するログの場所 (実際の %APPDATA% を汚さない)。</summary>
    public static readonly string LogDirectory = Path.Combine(Path.GetTempPath(), "KoromogaeSettingsTests_" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    public static void Initialize() => AppLog.LogPath = Path.Combine(LogDirectory, "app.log");

    public static string ReadLog() => File.Exists(AppLog.LogPath) ? File.ReadAllText(AppLog.LogPath) : "";
}

/// <summary>テストごとの使い捨てフォルダ。</summary>
sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KoromogaeTest_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.GetFiles(Path))
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}
