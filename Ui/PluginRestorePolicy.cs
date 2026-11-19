using System.IO;

namespace Koromogae.Ui;

/// <summary>起動時に復元できなかったプラグインを、設定に残すか消すか。</summary>
public enum RestoreAction
{
    /// <summary>設定に残す (次回の起動でも試す)。状態を失わないため。</summary>
    Keep,
    /// <summary>設定から消す。</summary>
    Delete,
}

/// <param name="Action">残すか消すか。</param>
/// <param name="FailCount">今回の失敗を数えた、連続の失敗回数 (設定に保存し直す値)。</param>
/// <param name="Message">利用者に見せる 1 行 (まとめて 1 つのダイアログに出す)。</param>
/// <param name="LogMessage">app.log に残す文。残さない (一時的な失敗) ときは null。</param>
public sealed record RestoreDecision(RestoreAction Action, int FailCount, string Message, string? LogMessage);

/// <summary>
/// 起動時のプラグイン復元の判断と、利用者に見せる文面。
/// ファイルが無いものは即削除、存在するのに <see cref="AppSettings.MaxLoadFailures"/> 回連続で失敗したものも削除、それ以外は残す
/// (一時的な失敗かもしれないので、状態を失わない)。画面 (ダイアログ) やファイルへの書き込みには触れない。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class PluginRestorePolicy
{
    /// <summary>画面の失敗の一覧に出す行数の上限。残りは「ほか N 件」にまとめる。</summary>
    public const int MaxFailureLines = 8;

    /// <summary>メッセージに出すプラグインの名前 (パスの末尾。VST3 のフォルダ形式で末尾に区切りが付いていてもよい)。</summary>
    public static string DisplayName(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));

    /// <summary>ロードに失敗した 1 つのプラグインについて、設定に残すかを決める。</summary>
    /// <param name="previousFailCount">これまでの連続の失敗回数 (今回は含まない)。</param>
    /// <param name="fileExists">ファイル (VST3 のバンドルはフォルダ) が存在するか。</param>
    public static RestoreDecision OnFailure(string path, string error, int previousFailCount, bool fileExists,
        int maxFailures = AppSettings.MaxLoadFailures)
    {
        string name = DisplayName(path);
        int failCount = previousFailCount + 1;

        if (!fileExists)
            return new(RestoreAction.Delete, failCount,
                Loc.F("Restore.Missing", name, error),
                $"見つからないプラグインを設定から削除: {path}");   // i18n:log

        if (failCount >= maxFailures)
            return new(RestoreAction.Delete, failCount,
                Loc.F("Restore.Failed", name, error, failCount),
                $"連続して失敗したプラグインを設定から削除: {path}");   // i18n:log

        return new(RestoreAction.Keep, failCount,
            Loc.F("Restore.Kept", name, error, failCount, maxFailures),
            null);
    }

    /// <summary>
    /// 失敗をまとめた、ダイアログの本文。ダイアログを連発せず 1 回にまとめ、多いときは先頭 <see cref="MaxFailureLines"/> 件と
    /// 「ほか N 件」にする。失敗が無ければ null (ダイアログを出さない)。
    /// </summary>
    public static string? FormatFailures(string header, IReadOnlyList<string> failures, string logPath)
    {
        if (failures.Count == 0) return null;
        string body = string.Join("\n", failures.Take(MaxFailureLines));
        if (failures.Count > MaxFailureLines) body += "\n" + Loc.F("Restore.More", failures.Count - MaxFailureLines);
        return Loc.F("Restore.Body", header, body, logPath);
    }

    /// <summary>プラグインを足せない理由 (上限に達している)。足せるなら null。設定に保持している分も数える。</summary>
    public static string? CapacityError(int loaded, int keptInSettings, int max = AppSettings.MaxPlugins)
        => loaded + keptInSettings >= max ? Loc.F("Restore.Capacity", max) : null;
}
