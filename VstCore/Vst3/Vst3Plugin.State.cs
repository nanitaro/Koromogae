using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using VstCore.Vst3;

namespace VstCore;

/// <summary>VST3 プラグイン: 状態 (プリセット) の保存と復元。</summary>
public sealed unsafe partial class Vst3Plugin
{
    // ================= 状態 (プリセット) =================

    public byte[]? GetState()
    {
        if (_disposed) return null;
        return _thread.Invoke<byte[]?>(() =>
        {
            using var component = new MemStream();
            int rc = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_component, 13))(_component, component.Primary);
            if (rc != 0) return null;

            byte[] controllerState = Array.Empty<byte>();
            if (_controller != IntPtr.Zero && !_controllerIsComponent)
            {
                using var ctrl = new MemStream();
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_controller, 7))(_controller, ctrl.Primary) == 0)
                    controllerState = ctrl.ToArray();
            }

            byte[] comp = component.ToArray();
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)3);
            bw.Write(comp.Length);
            bw.Write(comp);
            bw.Write(controllerState.Length);
            bw.Write(controllerState);
            bw.Flush();
            return ms.ToArray();
        });
    }

    public void SetState(byte[] data)
    {
        if (_disposed || data.Length < 1 || data[0] != 3) return;
        _thread.Invoke(() =>
        {
            using var br = new BinaryReader(new MemoryStream(data, 1, data.Length - 1));
            byte[] comp = br.ReadBytes(br.ReadInt32());
            byte[] ctrl = br.ReadBytes(br.ReadInt32());

            using (var s = new MemStream(comp))
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_component, 12))(_component, s.Primary);

            if (_controller != IntPtr.Zero)
            {
                using (var s = new MemStream(comp))
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_controller, 5))(_controller, s.Primary);
                if (ctrl.Length > 0 && !_controllerIsComponent)
                {
                    using var s = new MemStream(ctrl);
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_controller, 6))(_controller, s.Primary);
                }
            }
        });
    }
}
