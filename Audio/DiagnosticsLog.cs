namespace Koromogae.Audio;

/// <summary>
/// 停止時に app.log へ残す診断の警告 (音飛び・コールバックの例外・コールバックの停止があったときだけ)。
/// 利用者が後から原因を調べるときに頼る文面なので、判断と組み立てを、エンジンから切り出してテストしている。
/// このファイルは PortAudio にも画面にも依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class DiagnosticsLog
{
    /// <summary>ログに残すか。音飛びがあった、または異常 (例外・コールバックの停止・録音の欠落) があったとき。</summary>
    public static bool ShouldLog(AudioStats stats) => stats.Underruns > 0 || stats.ToDiagnostics().HasFault;

    /// <summary>警告の文面 (ログは、画面の言語にかかわらず日本語)。<paramref name="description"/> は、動作中の説明 (API・サンプルレート・形式) を日本語にしたもの。</summary>
    public static string Format(string description, AudioStats stats)
    {
        var diagnostics = stats.ToDiagnostics();
        string? firstError = stats.Input.FirstError ?? stats.Output.FirstError;
        return $"音飛びまたは異常がありました: {description} / {diagnostics.Format(Loc.Japanese)} / " +   // i18n:log
               (firstError == null ? "" : $"最初のコールバックの例外 {firstError} / ") +   // i18n:log
               $"入力コールバック間隔の最大 {stats.Input.MaxGapMs:F0}ms (サイズ {stats.Input.MinFrames}〜{stats.Input.MaxFrames})、" +   // i18n:log
               $"出力コールバック間隔の最大 {stats.Output.MaxGapMs:F0}ms (サイズ {stats.Output.MinFrames}〜{stats.Output.MaxFrames})、チェーン処理の最大 {stats.MaxChainMs:F1}ms";   // i18n:log
    }
}
