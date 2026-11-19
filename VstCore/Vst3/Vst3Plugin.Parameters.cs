using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using VstCore.Vst3;

namespace VstCore;

/// <summary>VST3 プラグイン: パラメータ (汎用 UI 用)。</summary>
public sealed unsafe partial class Vst3Plugin
{
    // ================= パラメータ (汎用 UI 用) =================

    uint[] _paramIds = Array.Empty<uint>();

    public ParameterInfo[] GetParameters()
    {
        if (_disposed || _controller == IntPtr.Zero) return Array.Empty<ParameterInfo>();
        return _thread.Invoke(() =>
        {
            int count = Math.Min(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(_controller, 8))(_controller), HostProtocol.MaxItemCount);
            var infos = new List<ParameterInfo>();
            var ids = new List<uint>();
            byte* buf = stackalloc byte[792]; // ParameterInfo: id, title[128], shortTitle[128], units[128], stepCount, default, unitId, flags
            for (int i = 0; i < count; i++)
            {
                new Span<byte>(buf, 792).Clear();
                if (((delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)Fn(_controller, 9))(_controller, i, buf) != 0) continue;

                int flags = *(int*)(buf + 788);
                if ((flags & ((1 << 4) | (1 << 1))) != 0) continue; // kIsHidden / kIsReadOnly は出さない

                string title = Marshal.PtrToStringUni((IntPtr)(buf + 4)) ?? "";
                if (string.IsNullOrWhiteSpace(title)) title = Marshal.PtrToStringUni((IntPtr)(buf + 260)) ?? $"Param {i + 1}";
                string units = Marshal.PtrToStringUni((IntPtr)(buf + 516)) ?? "";
                infos.Add(new ParameterInfo(infos.Count, title, units, *(int*)(buf + 772), *(double*)(buf + 776)));
                ids.Add(*(uint*)buf);
            }
            _paramIds = ids.ToArray();
            return infos.ToArray();
        });
    }

    public ParameterValue GetParameterValue(int index)
    {
        if (_disposed || index < 0 || index >= _paramIds.Length) return default;
        uint id = _paramIds[index];
        return _thread.Invoke(() =>
        {
            double value = ((delegate* unmanaged[Stdcall]<IntPtr, uint, double>)Fn(_controller, 14))(_controller, id);
            char* text = stackalloc char[128];
            new Span<char>(text, 128).Clear();
            string display = ((delegate* unmanaged[Stdcall]<IntPtr, uint, double, char*, int>)Fn(_controller, 10))(_controller, id, value, text) == 0
                ? new string(text)
                : "";
            return new ParameterValue(value, display);
        });
    }

    public void SetParameter(int index, double normalized)
    {
        if (_disposed || index < 0 || index >= _paramIds.Length) return;
        uint id = _paramIds[index];
        double value = Math.Clamp(normalized, 0, 1);
        _thread.Invoke(() =>
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, double, int>)Fn(_controller, 15))(_controller, id, value);
            _pendingEdits.Enqueue((id, value)); // 別オブジェクトのコントローラ変更はプロセッサへも伝える必要がある
        });
    }
}
