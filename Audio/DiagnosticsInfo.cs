using System.Globalization;

namespace Koromogae.Audio;

/// <summary>
/// 画面に出す音飛びの診断。音飛びの原因が、デバイスの XRUN か、リングバッファの余裕かを、利用者が数字で確かめられるようにする。
/// このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public readonly record struct DiagnosticsInfo(
    long Underruns,          // 再生中にバッファが空になった回数 (= 音飛び)
    long TrimmedFrames,      // 溜まりすぎて捨てた音の累計
    long DeliveredFrames,    // 入力から受け取った音の累計
    double HeadroomMs,       // エンジンが確保している余裕
    long DeviceXruns,        // PortAudio が報告した、入出力デバイスの XRUN (オーバーフロー/アンダーフロー) の回数
    long HandlerErrors = 0,  // コールバックの中で起きた例外の回数 (入出力の合計)。続くと無音や音割れの原因になる
    double InputStalledMs = 0,    // 入力のコールバックが止まっている時間 (止まっていなければ 0)
    double OutputStalledMs = 0,   // 出力のコールバックが止まっている時間 (止まっていなければ 0)
    double RecordDroppedMs = 0)   // 録音で、書き込みが追いつかず捨てた長さ (録音していなければ 0)
{
    /// <summary>捨てた音の割合 (%)。</summary>
    public double DroppedPercent => DeliveredFrames > 0 ? TrimmedFrames * 100.0 / DeliveredFrames : 0;

    /// <summary>コールバックが止まっているか、例外が起きているか (画面を赤くして知らせる状態)。</summary>
    public bool HasFault => HandlerErrors > 0 || InputStalledMs > 0 || OutputStalledMs > 0 || RecordDroppedMs > 0;

    /// <summary>画面の 1 行。<paramref name="culture"/> を省くと画面の言語 (ログに残すときは日本語を渡す)。</summary>
    public string Format(CultureInfo? culture = null)
    {
        string text = Loc.F(culture, "Diag.Main", Underruns, DroppedPercent, HeadroomMs, DeviceXruns);
        if (HandlerErrors > 0) text += Loc.F(culture, "Diag.HandlerErrors", HandlerErrors);
        if (InputStalledMs > 0) text += Loc.F(culture, "Diag.InputStalled", InputStalledMs / 1000);
        if (OutputStalledMs > 0) text += Loc.F(culture, "Diag.OutputStalled", OutputStalledMs / 1000);
        if (RecordDroppedMs > 0) text += Loc.F(culture, "Diag.RecordDropped", RecordDroppedMs);
        return text;
    }
}
