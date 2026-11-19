namespace VstCore;

/// <summary>
/// プラグインホスト側のログ。本体と同じ app.log (既定は %APPDATA%\Koromogae。環境変数 KOROMOGAE_DATA_DIR で変わる。子プロセスは本体の環境変数を引き継ぐ) へ追記する。
/// 本体は起動時に、決まった保存先をこの環境変数へ設定する (ポータブルモードでも、ホストは本体と同じフォルダへ書く)。
/// ローテーションは本体に任せる。ログの失敗で処理を止めないよう、例外は握りつぶす。
/// </summary>
internal static class HostLog
{
    static readonly object Gate = new();

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Info(string message) => Write("INFO", message, null);

    static void Write(string level, string message, Exception? ex)
    {
        try
        {
            string? dir = Environment.GetEnvironmentVariable("KOROMOGAE_DATA_DIR");
            dir = string.IsNullOrWhiteSpace(dir)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Koromogae")
                : System.IO.Path.GetFullPath(dir);
            string text = ex == null ? message : $"{message}{Environment.NewLine}{ex}";
            if (text.Length > 8000) text = text[..8000] + "...(省略)";
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [プラグインホスト {Environment.ProcessId}] {text}{Environment.NewLine}";
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(dir);
                using var fs = new System.IO.FileStream(System.IO.Path.Combine(dir, "app.log"),
                    System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                using var w = new System.IO.StreamWriter(fs);
                w.Write(line);
            }
        }
        catch
        {
        }
    }
}

