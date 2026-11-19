using System.Collections.Concurrent;

namespace VstCore;

/// <summary>
/// プラグイン専用の STA スレッド + Win32 メッセージループ。
/// VST2/VST3 (特に JUCE 製) はロード・エディタ生成・破棄を同一スレッドで行う必要があるため、非オーディオ操作はすべてここへ集約する。
/// </summary>
internal sealed class MessageThread
{
    readonly ConcurrentQueue<Action> _queue = new();
    readonly ManualResetEventSlim _ready = new();
    readonly Thread _thread;
    uint _threadId;

    public MessageThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        Win32.PeekMessageW(out _, IntPtr.Zero, 0, 0, Win32.PM_NOREMOVE); // スレッドメッセージキューを作成
        _ready.Set();

        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == Win32.WM_APP)
            {
                while (_queue.TryDequeue(out var action))
                {
                    try { action(); }
                    catch { }
                }
                continue;
            }
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        Win32.PostThreadMessageW(_threadId, Win32.WM_APP, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>スレッド上で実行して結果を待つ。例外は呼び出し側へ再スローする。</summary>
    public T Invoke<T>(Func<T> func, int timeoutMs = 15000)
    {
        if (IsCurrent) return func();

        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        Post(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });

        if (!done.Wait(timeoutMs))
            throw new TimeoutException(CoreLoc.T("Core.Thread.Timeout"));
        if (error != null) throw new InvalidOperationException(error.Message, error);
        return result;
    }

    public void Invoke(Action action, int timeoutMs = 15000) => Invoke<object?>(() => { action(); return null; }, timeoutMs);

    public void Stop()
    {
        if (_threadId != 0) Win32.PostThreadMessageW(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!IsCurrent) _thread.Join(2000);
    }
}

