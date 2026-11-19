namespace Koromogae.Audio;

/// <summary>エンジン全体の診断情報 (音飛びの原因を切り分けるため)。</summary>
public sealed record AudioStats(
    StreamStats Input, StreamStats Output,
    long Underruns,        // 再生中にリングバッファが空になり、無音にして再充填待ちに戻った回数 (= 音飛び)
    long SilentChunks,     // 無音で出力したチャンク数 (開始直後の充填待ちを含む)
    long Trims,            // 溜まりすぎて古い音をまとめて捨てた回数
    long TrimmedFrames,    // その合計フレーム数
    long MinSlackFrames,   // 再生中に、読み出した後に残っていたフレーム数の最小値 (0 に近いほど余裕がない)
    double MaxChainMs,     // VST チェーン 1 チャンク分の処理時間の最大値
    long DeliveredFrames,  // 入力から受け取った音の累計
    double HeadroomMs,     // エンジンが確保している余裕 (直前の入力コールバック 1 回ぶん)
    double InputStalledMs, // 入力のコールバックが止まっている時間 (止まっていなければ 0)
    double OutputStalledMs, // 出力のコールバックが止まっている時間 (止まっていなければ 0)
    double RecordDroppedMs) // 録音で、書き込みが追いつかず捨てた長さ (録音していなければ 0)
{
    /// <summary>コールバックの中で起きた例外の回数 (入出力の合計)。</summary>
    public long HandlerErrors => Input.HandlerErrors + Output.HandlerErrors;

    /// <summary>画面に出す診断。デバイスの XRUN は入出力の合計。</summary>
    public DiagnosticsInfo ToDiagnostics() => new(Underruns, TrimmedFrames, DeliveredFrames, HeadroomMs,
        Input.XrunCallbacks + Output.XrunCallbacks, HandlerErrors, InputStalledMs, OutputStalledMs, RecordDroppedMs);
}
