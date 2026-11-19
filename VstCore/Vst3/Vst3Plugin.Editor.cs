using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using VstCore.Vst3;

namespace VstCore;

/// <summary>VST3 プラグイン: エディタのウィンドウ (ウィンドウ自体は EditorWindow)。</summary>
public sealed unsafe partial class Vst3Plugin
{
    IntPtr _view;
    EditorWindow? _editor;
    bool _inResize;

    // ================= エディタ =================

    public void OpenEditor()
    {
        if (!HasEditor || _disposed) return;
        _thread.Post(OpenEditorOnThread);
    }

    /// <summary>エディタを閉じる (待たずに戻る)。実際の後始末はメッセージスレッドで行う。</summary>
    public void CloseEditor()
    {
        if (_disposed) return;
        _thread.Post(() =>
        {
            if (!_disposed) CloseEditorOnThread();
        });
    }

    void OpenEditorOnThread()
    {
        try
        {
            if (_editor != null)
            {
                _editor.Activate();
                return;
            }

            IntPtr editorType = Marshal.StringToHGlobalAnsi("editor");
            IntPtr hwndType = Marshal.StringToHGlobalAnsi("HWND");
            try
            {
                IntPtr view = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr>)Fn(_controller, 17))(_controller, editorType);
                if (view == IntPtr.Zero)
                {
                    HostLog.Warn($"VST3 エディタの view を作れません (エディタを持たないプラグインの可能性): {Name} ({_path})");
                    return;
                }

                int platformRc = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(view, 3))(view, hwndType);
                if (platformRc != 0)
                {
                    HostLog.Error($"VST3 エディタが HWND に対応していません: {Name} ({_path}) isPlatformTypeSupported = {platformRc}");
                    Release(ref view);
                    return;
                }

                var size = new ViewRect();
                ((delegate* unmanaged[Stdcall]<IntPtr, ViewRect*, int>)Fn(view, 9))(view, &size);
                int width = size.right - size.left, height = size.bottom - size.top;
                if (width <= 0) width = 400;
                if (height <= 0) height = 300;

                EditorWindow window;
                try
                {
                    window = EditorWindow.Create(this, Name, width, height, GetPrograms());
                }
                catch
                {
                    Release(ref view);
                    throw;
                }
                _view = view;
                _editor = window;

                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(view, 12))(view, _frame!.Primary);
                int rc = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Fn(view, 4))(view, window.Handle, hwndType);
                if (rc != 0)
                {
                    HostLog.Error($"VST3 エディタを attached できません: {Name} ({_path}) result = {rc}");
                    CloseEditorOnThread();
                    return;
                }

                window.Show();
            }
            finally
            {
                Marshal.FreeHGlobal(editorType);
                Marshal.FreeHGlobal(hwndType);
            }
        }
        catch (Exception ex)
        {
            // エディタの失敗でホスト全体を落とさない。原因はログに残す
            HostLog.Error($"VST3 エディタを開けません: {Name} ({_path})", ex);
        }
    }

    /// <summary>エディタを閉じる (メッセージスレッドで呼ぶ)。実際の後始末は EditorWindow から <see cref="IEditorOwner.CloseContent"/> が呼ばれる。</summary>
    void CloseEditorOnThread() => _editor?.Close();

    void IEditorOwner.SetProgram(int index) => SetProgram(index);

    void IEditorOwner.Idle()
    {
    }

    void IEditorOwner.CloseContent()
    {
        IntPtr view = _view;
        _view = IntPtr.Zero;
        if (view == IntPtr.Zero) return;

        try
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(view, 5))(view);                       // removed
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(view, 12))(view, IntPtr.Zero); // setFrame(null)
        }
        catch
        {
        }
        Release(ref view);
    }

    void IEditorOwner.Destroyed() => _editor = null;

    void OnResizeView(IntPtr view, ViewRect rect)
    {
        var editor = _editor;
        if (editor == null || _inResize) return;
        _inResize = true;
        try
        {
            editor.Resize(rect.right - rect.left, rect.bottom - rect.top);

            // ホストはリサイズ後に onSize で確定を通知する
            if (_view != IntPtr.Zero)
            {
                var newSize = rect;
                ((delegate* unmanaged[Stdcall]<IntPtr, ViewRect*, int>)Fn(_view, 10))(_view, &newSize);
            }
        }
        finally
        {
            _inResize = false;
        }
    }
}
