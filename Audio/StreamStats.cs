namespace Koromogae.Audio;

/// <summary>ストリーム 1 本分の診断情報。音飛びの原因 (デバイス側の XRUN / コールバックの遅れ / 周期の乱れ) を切り分けるために使う。</summary>
public readonly record struct StreamStats(
    long Callbacks, long XrunCallbacks, uint StatusFlags, int MinFrames, int MaxFrames,
    double MaxHandlerMs, double MinGapMs, double MaxGapMs, double AvgGapMs, double ReportedLatencyMs, double SampleRate,
    long HandlerErrors = 0, string? FirstError = null);
