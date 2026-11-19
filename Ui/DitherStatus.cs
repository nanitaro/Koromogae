using Koromogae.Audio;

namespace Koromogae.Ui;

/// <summary>
/// ディザ方式のツールチップに足す、「いまの設定でディザが働くのは」の 1 行。
/// 録音しない予定でも方式を選べる (録音は動作を始めた後に押す) ので、再生と録音のどちらで働くかを示す。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class DitherStatus
{
    /// <param name="playback">再生の形式。動作中は実際に開いた形式、停止中は画面で選んだ形式 (自動を含む)。</param>
    /// <param name="playbackIsActual"><paramref name="playback"/> が実際に開いた形式か (動作中)。</param>
    /// <param name="record">録音のビット深度。</param>
    /// <param name="recording">録音中か。</param>
    public static string Describe(FormatChoice playback, bool playbackIsActual, RecordDepth record, bool recording)
    {
        string play = playback switch
        {
            FormatChoice.Auto => Loc.T("DitherStatus.Auto"),
            FormatChoice.Int24 or FormatChoice.Int16 => "○" + (playbackIsActual ? Loc.F("DitherStatus.Opened", playback) : ""),
            _ => "×" + (playbackIsActual ? Loc.F("DitherStatus.Opened", playback) : ""),
        };
        string rec = !SettingsForm.RecordDitherApplies(record) ? "×" : recording ? Loc.T("DitherStatus.RecNow") : Loc.T("DitherStatus.RecLater");
        return Loc.F("DitherStatus.Line", play, rec);
    }
}