namespace Koromogae.Audio;

/// <summary>
/// 録音ファイルの書き出し (WAV / FLAC に共通)。ステレオ float を受け取り、ファイルの形式に変換して追記する。
/// このファイルは他に依存しない (単体テストでソースごとリンクしている)。
/// </summary>
internal interface IAudioFileWriter : IDisposable
{
    string Path { get; }

    /// <summary>ファイルに書いたバイト数 (画面に出すサイズ)。WAV はデータ部、FLAC は圧縮後のファイル全体。</summary>
    long TotalBytes { get; }

    /// <summary>このファイルに、あと何フレーム書けるか。上限がなければ <see cref="long.MaxValue"/> (WAV は約 4GB の上限で次のファイルへ切り替える)。</summary>
    long RemainingFrames { get; }

    void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right);

    /// <summary>ヘッダの長さの欄を書き直し、書いた分をディスクへ流す (落ちても、直前までの音が読めるように)。</summary>
    void UpdateHeader();
}
