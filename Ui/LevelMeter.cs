namespace Koromogae.Ui;

/// <summary>
/// 入出力メーターの 1 本分: 減衰つきのレベルと、クリップ表示の保持。時刻は引数で受け取る。
/// 画面のタイマー (80ms ごと) より短いクリップも見えるよう、検出後しばらく赤を保持する。
/// このファイルは WPF に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
public sealed class LevelMeter
{
    /// <summary>入力: フルスケール到達 (16bit の最大値 32767/32768 = 0.99997 も含める)。</summary>
    public const double InputClipThreshold = 0.9999;
    /// <summary>出力: プラグイン処理後の値が 1.0 を超え、デバイスへ書き出す際に実際に丸められる場合。</summary>
    public const double OutputClipThreshold = 1.0;

    public static readonly TimeSpan ClipHold = TimeSpan.FromMilliseconds(300);
    /// <summary>1 回の更新ごとにレベルが残る割合 (ピークが無いとき)。</summary>
    public const double Decay = 0.85;

    readonly double _threshold;
    readonly bool _inclusive;
    double _level;
    DateTime _clipUntil;

    /// <param name="threshold">クリップとみなすピーク値。</param>
    /// <param name="inclusive">true ならピークが threshold と等しくてもクリップ (入力)。false なら超えたときだけ (出力)。</param>
    public LevelMeter(double threshold, bool inclusive)
    {
        _threshold = threshold;
        _inclusive = inclusive;
    }

    public static LevelMeter ForInput() => new(InputClipThreshold, inclusive: true);
    public static LevelMeter ForOutput() => new(OutputClipThreshold, inclusive: false);

    /// <summary>メーターの値 (0..1)。ピークはすぐ反映し、下がるときは <see cref="Decay"/> ずつ。</summary>
    public double Level => Math.Min(1, _level);

    /// <summary>前回の更新以降のピーク値を渡す。</summary>
    public void Update(float peak, DateTime now)
    {
        _level = Math.Max(peak, _level * Decay);
        if (_inclusive ? peak >= _threshold : peak > _threshold) _clipUntil = now + ClipHold;
    }

    /// <summary>クリップ中 (検出後 <see cref="ClipHold"/> の間) か。メーターを赤くする。</summary>
    public bool IsClipping(DateTime now) => now < _clipUntil;
}
