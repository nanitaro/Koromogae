using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VstCore;

/// <summary><see cref="EditorWindow"/> が、プラグイン固有の処理 (VST2 / VST3 で違う部分) を呼び出す先。メッセージスレッドから呼ばれる。</summary>
internal interface IEditorOwner
{
    /// <summary>メニューバーで選ばれたプリセットへ切り替える。</summary>
    void SetProgram(int index);

    /// <summary>タイマー (<see cref="EditorWindow.StartIdleTimer"/>) のたびに呼ばれる。</summary>
    void Idle();

    /// <summary>ウィンドウを壊す前に、プラグインのエディタ (VST2: effEditClose、VST3: IPlugView::removed など) を閉じる。</summary>
    void CloseContent();

    /// <summary>ウィンドウが壊れた後。持っている参照を捨てる。</summary>
    void Destroyed();
}

/// <summary>
/// プラグインのエディタを載せる Win32 ウィンドウ (VST2 / VST3 共通)。メッセージスレッドで作り、同じスレッドで操作する。
/// 内蔵プリセットがあれば、メニューバーに「プリセット」を付ける。ウィンドウのクラスの登録・外枠の計算・閉じる処理はここに集約する。
/// ウィンドウを閉じても <c>PostQuitMessage</c> は呼ばない (メッセージループはプラグインの寿命と同じ)。
/// </summary>
internal sealed class EditorWindow
{
    const string ClassName = "VstCoreEditorWindow";
    const uint Style = Win32.WS_OVERLAPPED | Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX;
    static readonly UIntPtr TimerId = (UIntPtr)1;

    static readonly Dictionary<IntPtr, EditorWindow> Windows = new();
    static readonly object Gate = new();
    static readonly Win32.WndProc WndProcDelegate = WndProc;
    static bool _classRegistered;

    readonly IEditorOwner _owner;
    readonly int _programCount;
    IntPtr _menu;
    bool _timer, _closing;

    public IntPtr Handle { get; private set; }

    EditorWindow(IEditorOwner owner, ProgramList programs)
    {
        _owner = owner;
        _programCount = programs.Names.Length;
        _menu = ProgramMenu.Build(programs.Names);
        ProgramMenu.Check(_menu, _programCount, programs.Current);
    }

    /// <summary>ウィンドウを作る (まだ表示しない)。クライアント領域の大きさは <paramref name="width"/> × <paramref name="height"/>。作れなければ <see cref="Win32Exception"/>。</summary>
    public static EditorWindow Create(IEditorOwner owner, string title, int width, int height, ProgramList programs)
    {
        EnsureWindowClass();
        var window = new EditorWindow(owner, programs);

        var rect = new Win32.RECT { right = width, bottom = height };
        Win32.AdjustWindowRect(ref rect, Style, window._menu != IntPtr.Zero);
        IntPtr hwnd = Win32.CreateWindowExW(0, ClassName, title, Style,
            Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top,
            IntPtr.Zero, window._menu, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), CoreLoc.T("Core.Editor.WindowFailed"));

        window.Handle = hwnd;
        lock (Gate) Windows[hwnd] = window;
        return window;
    }

    /// <summary>クライアント領域の大きさを変える (外枠とメニューバーの分は足す)。</summary>
    public void Resize(int width, int height)
    {
        if (Handle == IntPtr.Zero) return;
        var rect = new Win32.RECT { right = width, bottom = height };
        Win32.AdjustWindowRect(ref rect, Style, _menu != IntPtr.Zero);
        Win32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, rect.right - rect.left, rect.bottom - rect.top,
            Win32.SWP_NOMOVE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>ウィンドウを表示して、前面に出す。</summary>
    public void Show() => BringToFront();

    /// <summary>すでに開いているウィンドウを前面に出す。</summary>
    public void Activate() => BringToFront();

    /// <summary>
    /// 表示して (最小化されていれば元に戻して)、ほかのウィンドウより前に出す。前面に固定はしない。
    /// 前面ロックのためにフォーカスを取れないことがあるので、Z オーダーは一瞬だけ TOPMOST にしてすぐ戻す方法で前に出す
    /// (これは前面ロックの影響を受けない)。フォーカスは、本体が <see cref="Win32.AllowSetForegroundWindow"/> で権利を渡していれば移る。
    /// </summary>
    public void BringToFront()
    {
        if (Handle == IntPtr.Zero) return;
        Win32.ShowWindow(Handle, Win32.SW_RESTORE);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        Win32.SetWindowPos(Handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        Win32.SetForegroundWindow(Handle);
    }

    /// <summary><see cref="IEditorOwner.Idle"/> を一定間隔で呼ぶ (VST2 の effEditIdle 用)。</summary>
    public void StartIdleTimer(uint intervalMs)
    {
        if (Handle == IntPtr.Zero) return;
        Win32.SetTimer(Handle, TimerId, intervalMs, IntPtr.Zero);
        _timer = true;
    }

    /// <summary>プラグインのエディタを閉じて、ウィンドウを壊す。すでに閉じていれば何もしない。</summary>
    public void Close()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero || _closing) return;
        _closing = true;

        if (_timer) Win32.KillTimer(hwnd, TimerId);
        try { _owner.CloseContent(); } catch { }
        Win32.DestroyWindow(hwnd);   // WM_DESTROY で OnDestroyed が呼ばれる
    }

    void OnDestroyed()
    {
        IntPtr hwnd = Handle;
        lock (Gate) Windows.Remove(hwnd);
        Handle = IntPtr.Zero;
        _menu = IntPtr.Zero;   // メニューはウィンドウと一緒に壊れる
        _owner.Destroyed();
    }

    static void EnsureWindowClass()
    {
        lock (Gate)
        {
            if (_classRegistered) return;
            var cls = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
                hInstance = Win32.GetModuleHandleW(null),
                hCursor = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IDC_ARROW),
                hbrBackground = (IntPtr)6, // COLOR_WINDOW + 1
                lpszClassName = ClassName,
            };
            Win32.RegisterClassExW(ref cls);
            _classRegistered = true;
        }
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        EditorWindow? window;
        lock (Gate) Windows.TryGetValue(hwnd, out window);

        switch (msg)
        {
            case Win32.WM_TIMER:
                window?._owner.Idle();
                return IntPtr.Zero;
            case Win32.WM_COMMAND:
                if (window != null)
                {
                    int program = ProgramMenu.IndexFromCommand(wParam, window._programCount);
                    if (program >= 0)
                    {
                        window._owner.SetProgram(program);
                        ProgramMenu.Check(window._menu, window._programCount, program);
                        return IntPtr.Zero;
                    }
                }
                break;
            case Win32.WM_CLOSE:
                if (window != null) window.Close();
                else Win32.DestroyWindow(hwnd);
                return IntPtr.Zero;
            case Win32.WM_DESTROY:
                window?.OnDestroyed();
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
