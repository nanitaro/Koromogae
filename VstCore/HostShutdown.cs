namespace VstCore;

/// <summary>
/// プラグインホスト終了時の判断。音声スレッドがプラグインの処理中のまま残っているのに、
/// プラグインの破棄 (バッファ解放・DLL アンロード) や共有メモリのアンマップをすると、
/// そのスレッドが解放済みのメモリやアンロード済みのコードに触れてクラッシュする。
/// </summary>
public static class HostShutdown
{
    public const int AudioThreadTimeoutMs = 1000;

    /// <summary>音声スレッドが止まらなかったため、後始末を省略してプロセスごと終了した。</summary>
    public const int ExitAudioThreadStuck = 4;

    /// <summary>音声スレッドの終了を待つ。終了した (または元から無い) なら true。タイムアウトしたら false。</summary>
    public static bool WaitForAudioThread(Thread? audioThread, int timeoutMs = AudioThreadTimeoutMs)
        => audioThread == null || audioThread.Join(timeoutMs);

    /// <summary>
    /// 終了コードを決める。すでに別の理由 (プロトコル違反 = 3 など) があればそれを優先し、
    /// 正常 (0) のときだけ、音声スレッドが止まらなかった事実を 4 で伝える。
    /// </summary>
    public static int ResolveExitCode(int currentExitCode, bool audioThreadStopped)
        => audioThreadStopped || currentExitCode != 0 ? currentExitCode : ExitAudioThreadStuck;
}
