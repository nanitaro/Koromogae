using System.IO;

namespace Koromogae;

/// <summary>
/// 簡易ログ (%APPDATA%\Koromogae\app.log。保存先は AppPaths で変えられる)。1MB を超えたら app.log.old へ回して新しく始める。
/// ログの失敗でアプリを止めないよう、書き込みの例外は握りつぶす。
/// </summary>
public static class AppLog
{
    const long MaxBytes = 1_000_000;
    const int MaxMessageChars = 8000;
    static readonly object Gate = new();

    public static string LogPath { get; set; } = Path.Combine(AppPaths.DataDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    static void Write(string level, string message, Exception? ex)
    {
        try
        {
            string text = ex == null ? message : $"{message}{Environment.NewLine}{ex}";
            if (text.Length > MaxMessageChars) text = text[..MaxMessageChars] + "...(省略)";   // i18n:log
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {text}{Environment.NewLine}";

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // ログに書けなくても処理は続ける
        }
    }
}
