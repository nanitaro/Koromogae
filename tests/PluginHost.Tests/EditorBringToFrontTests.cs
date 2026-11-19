using VstCore;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// エディタのウィンドウを前に出す処理 (<see cref="EditorWindow.BringToFront"/>) の検査。
/// ホストのプロセスは前面ロックのためにフォーカスを取れないことがあるので、Z オーダー (どのウィンドウの上にあるか) と、
/// 前面に固定していない (TOPMOST が残らない) ことを確かめる。対話的なデスクトップが無い環境ではスキップする。
/// </summary>
public class EditorBringToFrontTests
{
    sealed class FakeOwner : IEditorOwner
    {
        public void SetProgram(int index) { }
        public void Idle() { }
        public void CloseContent() { }
        public void Destroyed() { }
    }

    /// <summary>ウィンドウを作って操作するスレッド (STA)。作れなければ null (デスクトップが無い)。</summary>
    static void OnWindowThread(Action<EditorWindow, EditorWindow> body)
    {
        Exception? error = null;
        bool created = false;
        var thread = new Thread(() =>
        {
            EditorWindow? a = null, b = null;
            try
            {
                var programs = new ProgramList(Array.Empty<string>(), 0);
                a = EditorWindow.Create(new FakeOwner(), "A", 300, 200, programs);
                b = EditorWindow.Create(new FakeOwner(), "B", 300, 200, programs);
                created = true;
                body(a, b);
            }
            catch (Exception ex) when (!created) { _ = ex; }     // 作れない = デスクトップが無い
            catch (Exception ex) { error = ex; }
            finally
            {
                a?.Close();
                b?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Skip.IfNot(created, "ウィンドウを作れません (対話的なデスクトップが必要)");
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    /// <summary><paramref name="upper"/> が <paramref name="lower"/> より上 (手前) にあるか。</summary>
    static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (IntPtr w = Win32.GetWindow(lower, Win32.GW_HWNDPREV); w != IntPtr.Zero; w = Win32.GetWindow(w, Win32.GW_HWNDPREV))
            if (w == upper) return true;
        return false;
    }

    static bool IsTopmost(IntPtr hwnd) => (Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOPMOST) != 0;

    [SkippableFact]
    public void Bringing_a_window_to_the_front_puts_it_above_the_other_windows()
    {
        OnWindowThread((a, b) =>
        {
            a.Show();
            b.Show();                                           // 後から出した B が上
            Assert.True(IsAbove(b.Handle, a.Handle));

            a.BringToFront();

            Assert.True(IsAbove(a.Handle, b.Handle));
        });
    }

    [SkippableFact]
    public void Bringing_a_window_to_the_front_does_not_pin_it_on_top()
    {
        OnWindowThread((a, b) =>
        {
            a.Show();
            b.Show();

            a.BringToFront();

            Assert.False(IsTopmost(a.Handle));                  // 一瞬だけ TOPMOST にして、すぐ戻す (前面に固定しない)
            Assert.False(IsTopmost(b.Handle));
        });
    }

    [SkippableFact]
    public void Activating_an_already_open_window_brings_it_to_the_front()
    {
        OnWindowThread((a, b) =>
        {
            a.Show();
            b.Show();

            a.Activate();                                       // すでに開いているエディタを、もう一度「エディタを開く」したとき

            Assert.True(IsAbove(a.Handle, b.Handle));
            Assert.False(IsTopmost(a.Handle));
        });
    }

    [SkippableFact]
    public void A_minimized_window_is_restored_when_it_is_brought_to_the_front()
    {
        OnWindowThread((a, b) =>
        {
            a.Show();
            Win32.ShowWindow(a.Handle, 6 /* SW_MINIMIZE */);
            Assert.True(Win32.IsIconic(a.Handle));

            a.BringToFront();

            Assert.False(Win32.IsIconic(a.Handle));
        });
    }
}
