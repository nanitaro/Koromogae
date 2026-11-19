namespace Koromogae.Audio;

/// <summary>
/// ストリームのコールバック回数が増え続けているかを見て、止まっていれば何ミリ秒止まっているかを返す。
/// デバイスによっては、開始直後の充填のあと、コールバックが二度と来なくなる (音飛びの回数は増えないので、別に見張る必要がある)。
/// UI スレッドだけが使う (更新の周期は約 0.5 秒)。このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public sealed class CallbackWatch
{
    /// <summary>
    /// これだけ回数が変わらなければ「止まった」とみなす (ms)。
    /// WASAPI 排他の 500ms バッファでもコールバックの間隔は約 511ms なので、それより長くしてある。
    /// </summary>
    public const double StallThresholdMs = 1500;

    long _lastCount;
    double _lastChangeMs;

    /// <param name="startMs">監視を始める時刻 (ms)。開始直後からコールバックが一度も来ない場合も、ここから数える。</param>
    public CallbackWatch(double startMs) => _lastChangeMs = startMs;

    /// <summary>止まっていなければ 0。止まっていれば、最後に回数が増えてからの経過時間 (ms)。</summary>
    public double Update(long callbacks, double nowMs)
    {
        if (callbacks != _lastCount)
        {
            _lastCount = callbacks;
            _lastChangeMs = nowMs;
            return 0;
        }

        double stalled = nowMs - _lastChangeMs;
        return stalled >= StallThresholdMs ? stalled : 0;
    }
}
