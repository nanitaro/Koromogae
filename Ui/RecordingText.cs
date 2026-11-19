using System.Globalization;
using Koromogae.Audio;

namespace Koromogae.Ui;

/// <summary>
/// 録音中に画面へ出す文言 (経過時間・ファイルサイズ)。画面の部品に触れない。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public static class RecordingText
{
    /// <summary>バイト数を MB / GB で表す (1MB = 1,048,576 バイト)。1GB 未満は MB、それ以上は GB。</summary>
    public static string FormatSize(long bytes)
    {
        double mb = bytes / 1048576.0;
        return mb < 1024 ? $"{mb:F1} MB" : $"{mb / 1024:F2} GB";
    }

    /// <summary>経過時間を <c>00:12:34</c> の形にする (1 時間を超えても時間の桁を増やすだけ。100 時間以上も崩れない)。</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    /// <summary>録音ファイルのビット深度の表示名。</summary>
    public static string DepthLabel(RecordDepth depth) => depth switch
    {
        RecordDepth.Float32 => "Float32",
        RecordDepth.Int16 => "Int16",
        _ => "Int24",
    };

    /// <summary>録音ファイルのディザの状態。Float32 は丸めないので、方式にかかわらず「ディザ オフ」(再生側の表示と同じ文言)。</summary>
    public static string DitherLabel(RecordDepth depth, DitherMode mode, CultureInfo? culture = null) => depth == RecordDepth.Float32
        ? Loc.T("Dither.Label.Off", culture)
        : mode switch
        {
            DitherMode.Tpdf => Loc.T("Dither.Label.Tpdf", culture),
            DitherMode.HpTpdf => Loc.T("Dither.Label.HpTpdf", culture),
            DitherMode.Shaped => Loc.T("Dither.Label.Shaped", culture),
            _ => Loc.T("Dither.Label.Off", culture),
        };

    /// <summary>
    /// 録音ファイルの形式の 1 行。例: <c>44100 Hz / 2ch Int16 / ディザ HP-TPDF</c>。FLAC のときは末尾に <c> / FLAC レベル5</c>。
    /// 再生グループの説明表示と同じ書き方: 「 / 」で区切り、チャンネル数とビット深度は空白だけでつなぐ (「2ch Int16」)。録音は常に 2ch。
    /// </summary>
    public static string Spec(int sampleRate, RecordDepth depth, DitherMode mode,
        RecordFormat format = RecordFormat.Wav, int flacLevel = AppSettings.DefaultFlacLevel, CultureInfo? culture = null)
    {
        string spec = $"{sampleRate} Hz / 2ch {DepthLabel(depth)} / {DitherLabel(depth, mode, culture)}";
        return format == RecordFormat.Flac ? Loc.F(culture, "Rec.Spec.Flac", spec, flacLevel) : spec;
    }

    /// <summary>録音していない (動作中の) ときの表示。これから録音される形式を予告する。</summary>
    public static string Ready(string spec) => Loc.F("Rec.Ready", spec);

    /// <summary>
    /// 録音中の状態の 1 行。<paramref name="spec"/> があれば先頭に録音ファイルの形式を置き、「  /  」で区切って経過時間とサイズを続ける
    /// (形式は変わらないので左に固定され、変わり続ける経過時間とサイズが右にくる。予告の「録音できます (…)」と形式の位置がそろう)。
    /// 加工前も録音しているときは、サイズを加工後 / 加工前に分ける。ディスクの空きが少ないときは警告を末尾に足す。
    /// </summary>
    public static string Format(TimeSpan elapsed, long wetBytes, long dryBytes, bool includesDry, bool lowDisk, string? spec = null)
    {
        string line = FormatSizes(elapsed, wetBytes, dryBytes, includesDry, lowDisk);
        return spec == null ? line : $"{spec}  /  {line}";
    }

    static string FormatSizes(TimeSpan elapsed, long wetBytes, long dryBytes, bool includesDry, bool lowDisk)
    {
        string size = includesDry
            ? Loc.F("Rec.Size.WetDry", FormatSize(wetBytes), FormatSize(dryBytes))
            : FormatSize(wetBytes);
        string text = $"{FormatElapsed(elapsed)}  {size}";
        return lowDisk ? text + Loc.T("Rec.LowDisk") : text;
    }
}